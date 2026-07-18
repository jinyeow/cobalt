using System.Diagnostics;
using System.Net;
using Cobalt.Core.Ado;
using Cobalt.Core.Auth;
using Cobalt.Core.Config;
using Cobalt.Core.Models;

// UAT route prober (Harness A). Exercises the ADO routes that the remote build session
// could only verify "by shape" against the live org: org-wide WIQL + workitemsbatch,
// cross-project work-item metadata, teams ($mine=true), and the Team-tab PR halves.
// It prints structured shapes so a human can eyeball correctness; each probe is isolated
// so one failing route still lets the rest run (revealing *which* route misbehaves is the
// whole point).
//
// Probes 5-6 measure the diff-review load path against the live org: round-trip latency is
// the one thing the headless suite cannot see (it proves the calls are concurrent, never
// how long they take), and whether ADO compresses authenticated responses.
//
// Read-only: every call is a GET. Nothing votes, comments, or mutates a thread.
//
//   dotnet run --project tools/uat -- [--context <name>]

var contextName = ParseContext(args);

var config = ConfigLoader.Load(ConfigPaths.ConfigFile());
var context = config.Resolve(contextName);
var tokens = AzureTokenProvider.CreateDefault(
    Path.Join(ConfigPaths.ConfigDirectory(), "auth-record.json"));

using var connection = AdoConnection.Create(context, tokens);
var workItems = new WorkItemsApi(connection.Http, context);
var git = new GitApi(connection.Http, context);
var ct = CancellationToken.None;
var anyFailed = false;

Header("context");
Console.WriteLine($"  name         : {context.Name}");
Console.WriteLine($"  organization : {context.OrganizationUrl}");
Console.WriteLine($"  project      : {context.Project}");
Console.WriteLine($"  pr_scope     : {context.PrScope}");

AdoUser? me = null;
await Probe("identity (who am I)", async () =>
{
    me = await connection.Identity.GetAuthenticatedUserAsync(ct);
    Console.WriteLine($"  {me.DisplayName}  ({me.Id})");
});

// ---- Probe 1: org-wide work items (all projects) ----
IReadOnlyList<WorkItem> orgItems = [];
await Probe("org-wide work items  (scope=Org, project-segment-less WIQL + workitemsbatch)", async () =>
{
    orgItems = await workItems.QueryMyWorkItemsAsync(new WorkItemQuery(), PrScope.Org, ct);
    var projects = orgItems.Select(w => w.TeamProject).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    Console.WriteLine($"  rows       : {orgItems.Count}");
    Console.WriteLine($"  projects   : {projects.Count}  [{string.Join(", ", projects)}]");
    Console.WriteLine($"  spans >1 project : {(projects.Count > 1 ? "YES (project column expected)" : "no")}");
    foreach (var w in orgItems.Take(8))
    {
        Console.WriteLine($"    #{w.Id,-8} {Trunc(w.TeamProject, 22),-22} {w.WorkItemType,-14} {w.State,-12} {Trunc(w.Title, 44)}");
    }
});

// ---- Probe 2: cross-project drill-in metadata (the round-2 HIGH fix) ----
await Probe("cross-project WI metadata  (states/detail must use the item's OWN project)", async () =>
{
    var cross = orgItems.FirstOrDefault(w =>
        !string.IsNullOrEmpty(w.TeamProject) &&
        !string.Equals(w.TeamProject, context.Project, StringComparison.OrdinalIgnoreCase));
    if (cross is null)
    {
        Console.WriteLine($"  (no assigned item outside '{context.Project}' — cannot exercise cross-project drill-in from your data)");
        return;
    }
    Console.WriteLine($"  target     : #{cross.Id} '{Trunc(cross.Title, 40)}' in project '{cross.TeamProject}' (type {cross.WorkItemType})");
    var states = await workItems.GetStatesAsync(cross.WorkItemType, cross.TeamProject, ct);
    Console.WriteLine($"  states     : [{string.Join(", ", states.Select(s => s.Name))}]  ({states.Count})");
    var detail = await workItems.GetWorkItemAsync(cross.Id, cross.TeamProject, ct);
    Console.WriteLine($"  detail get : #{detail.Id} state='{detail.State}'  -> OK (no 404 using project B)");
});

// ---- Probe 3: my teams (Get All Teams, $mine=true) ----
IReadOnlyList<AdoTeam> myTeams = [];
await Probe("my teams  (_apis/teams?$mine=true)", async () =>
{
    myTeams = await connection.Teams.GetMyTeamsAsync(ct);
    Console.WriteLine($"  teams      : {myTeams.Count}");
    foreach (var t in myTeams.Take(12))
    {
        Console.WriteLine($"    {Trunc(t.Name, 34),-34} {Trunc(t.ProjectName, 24),-24} {t.Id}");
    }
});

// ---- Probe 4: Team-tab PR halves (team-as-reviewer + active/teammate-authored) ----
await Probe("Team-tab PR routes  (reviewerId={teamGuid} + active-PR union)", async () =>
{
    var scope = context.PrScope;

    // (a) Sanity the reviewer route with a KNOWN reviewer (me). If this is >0 the route
    //     works and any per-team zero is genuine; if it's 0 too, suspect the route itself.
    if (me is not null)
    {
        var mine = await git.ListPullRequestsForReviewerAsync(me.Id, scope, ct);
        Console.WriteLine($"  reviewerId=me         -> {mine.Count} active PR(s)  (route sanity: expect >0 if you're on any review queue)");
    }

    var active = await git.ListPullRequestsAsync(PrListFilter.Active, Guid.Empty, scope, ct);
    Console.WriteLine($"  active union (org-wide, $top capped): {active.Count} active PR(s)");

    // (b) Decisive cross-check: do ANY of my teams actually appear as a requested reviewer
    //     on the active PRs? If yes here but the per-team query returns 0, the
    //     reviewerId={teamGuid} route is broken. If none appear, the zeros are legitimate.
    var teamGuids = myTeams.ToDictionary(t => t.Id.ToString(), t => t.Name, StringComparer.OrdinalIgnoreCase);
    var teamsSeenAsReviewer = active
        .SelectMany(pr => pr.Reviewers.Select(r => r.Id))
        .Where(teamGuids.ContainsKey)
        .GroupBy(id => id, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
    if (teamsSeenAsReviewer.Count == 0)
    {
        Console.WriteLine("  cross-check: NONE of your teams is a requested reviewer on any active PR -> per-team zeros are legitimate");
    }
    else
    {
        Console.WriteLine("  cross-check: teams that ARE reviewers on active PRs (per-team query MUST match):");
        foreach (var (id, count) in teamsSeenAsReviewer)
        {
            Console.WriteLine($"    {Trunc(teamGuids[id], 34),-34} appears on {count} active PR(s)  ({id})");
        }
    }

    foreach (var t in myTeams)
    {
        var prs = await git.ListPullRequestsForReviewerAsync(t.Id, scope, ct);
        var expected = teamsSeenAsReviewer.TryGetValue(t.Id.ToString(), out var e) ? e : 0;
        var flag = prs.Count == 0 && expected > 0 ? "  <-- MISMATCH (route broken)" : "";
        Console.WriteLine($"  reviewer={Trunc(t.Name, 28),-28} -> {prs.Count} (cross-check expects {expected}){flag}");
    }
});

// ---- Probe 5: diff-review load path (round-trip latency) ----
// The headless suite proves these calls are concurrent; only a live org shows what that
// buys. Reports each leg, then the two numbers that matter: what an open costs now vs
// what the same work cost when every call was serial.
PullRequest? diffPr = null;
PrIteration? iteration = null;
IReadOnlyList<FileChange> changes = [];
await Probe("diff-review load path  (latency of an actual PR open)", async () =>
{
    var candidates = await git.ListPullRequestsAsync(PrListFilter.Active, Guid.Empty, context.PrScope, ct);
    if (candidates.Count == 0)
    {
        Console.WriteLine("  (no active PRs in scope — cannot measure an open from your data)");
        return;
    }

    // Warm the connection first: otherwise leg 1 carries the DNS+TCP+TLS handshake and every
    // number below is really a measurement of the cold pool.
    await connection.Http.WarmUpAsync(ct);

    foreach (var candidate in candidates)
    {
        var (it, ch) = await IterationAndChangesAsync(candidate);
        if (it is not null && ch.Count > 0)
        {
            diffPr = candidate;
            iteration = it;
            changes = ch;
            break;
        }
    }
    if (diffPr is null || iteration is null)
    {
        Console.WriteLine($"  (none of the {candidates.Count} active PR(s) had an iteration with file changes)");
        return;
    }

    Console.WriteLine($"  target     : !{diffPr.PullRequestId} '{Trunc(diffPr.Title, 40)}'  ({changes.Count} changed file(s))");
    Console.WriteLine();

    var iterationMs = await TimeAsync(() =>
        git.GetLatestIterationAsync(diffPr.RepositoryId, diffPr.PullRequestId, diffPr.ProjectName, ct));
    var changesMs = await TimeAsync(() =>
        git.GetIterationChangesAsync(diffPr.RepositoryId, diffPr.PullRequestId, iteration.Id, diffPr.ProjectName, ct));
    var threadsMs = await TimeAsync(() =>
        git.GetThreadsAsync(diffPr.RepositoryId, diffPr.PullRequestId, diffPr.ProjectName, ct));

    // An edited file exercises both blobs; an add/delete has only one side.
    var edited = changes.FirstOrDefault(c => c.ChangeType == FileChangeKind.Edit) ?? changes[0];
    var basePath = edited.OriginalPath ?? edited.Path;
    Console.WriteLine($"  blob file  : {Trunc(edited.Path, 52)}  ({edited.ChangeType})");

    var baseMs = await TimeAsync(() =>
        git.GetFileContentAsync(diffPr.RepositoryId, basePath, iteration.BaseCommitId ?? "", diffPr.ProjectName, ct));
    var sourceMs = await TimeAsync(() =>
        git.GetFileContentAsync(diffPr.RepositoryId, edited.Path, iteration.SourceCommitId ?? "", diffPr.ProjectName, ct));

    // The pair as the view-model actually issues it: both started, then both awaited.
    var pairSw = Stopwatch.StartNew();
    var baseTask = git.GetFileContentAsync(diffPr.RepositoryId, basePath, iteration.BaseCommitId ?? "", diffPr.ProjectName, ct);
    var sourceTask = git.GetFileContentAsync(diffPr.RepositoryId, edited.Path, iteration.SourceCommitId ?? "", diffPr.ProjectName, ct);
    await Task.WhenAll(baseTask, sourceTask);
    pairSw.Stop();

    Console.WriteLine();
    Console.WriteLine("  per-leg (warm pool, each measured alone):");
    Console.WriteLine($"    iteration        {iterationMs,6:F0} ms");
    Console.WriteLine($"    changes          {changesMs,6:F0} ms");
    Console.WriteLine($"    threads          {threadsMs,6:F0} ms");
    Console.WriteLine($"    blob (base)      {baseMs,6:F0} ms");
    Console.WriteLine($"    blob (source)    {sourceMs,6:F0} ms");
    Console.WriteLine();
    Console.WriteLine($"    blob pair concurrent : {pairSw.Elapsed.TotalMilliseconds,6:F0} ms   (serial would be ~{baseMs + sourceMs:F0} ms)");
    Console.WriteLine();

    // The branch's claim, in the shape LoadAsync issues it: iteration -> changes -> then
    // threads and both blobs together. Modelled from the measured legs rather than re-run,
    // so the comparison is like-for-like against the old serial chain.
    var now = iterationMs + changesMs + Math.Max(threadsMs, pairSw.Elapsed.TotalMilliseconds);
    var before = iterationMs + changesMs + threadsMs + baseMs + sourceMs;
    Console.WriteLine($"  PR open  now    : ~{now,6:F0} ms   (iteration -> changes -> max(threads, blob pair))");
    Console.WriteLine($"  PR open  before : ~{before,6:F0} ms   (5 serial round-trips)");
    Console.WriteLine($"  saved           : ~{before - now,6:F0} ms  ({(before > 0 ? (1 - now / before) * 100 : 0):F0}%)");
    Console.WriteLine();
    Console.WriteLine("  NOTE: a warm pool and one sample. Re-run a few times — ADO latency is noisy.");
});

// ---- Probe 6: gzip on authenticated responses (the plan's open Unknown) ----
// AutomaticDecompression strips Content-Encoding transparently, so the shipped client
// cannot answer this about itself. A raw client with decompression OFF can.
await Probe("gzip on authenticated responses  (plan Unknown: unverified until now)", async () =>
{
    if (diffPr is null || iteration is null || changes.Count == 0)
    {
        Console.WriteLine("  (skipped — probe 5 found no PR with file changes to fetch)");
        return;
    }

    using var raw = new HttpClient(new BearerTokenHandler(tokens)
    {
        InnerHandler = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.None },
    })
    {
        BaseAddress = new Uri($"{context.OrganizationUrl.AbsoluteUri.TrimEnd('/')}/"),
    };
    raw.DefaultRequestHeaders.UserAgent.ParseAdd("cobalt-uat");
    raw.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip, deflate");

    // Largest changed file gives compression the best chance to show up.
    var file = changes.OrderByDescending(c => c.Path.Length).First();
    var query =
        $"path={Uri.EscapeDataString(file.Path)}" +
        $"&versionDescriptor.version={Uri.EscapeDataString(iteration.SourceCommitId ?? "")}" +
        "&versionDescriptor.versionType=commit" +
        "&includeContent=true&$format=text&api-version=7.2-preview.1";
    var route =
        $"{Uri.EscapeDataString(diffPr.ProjectName)}/_apis/git/repositories/" +
        $"{Uri.EscapeDataString(diffPr.RepositoryId)}/items?{query}";

    using var response = await raw.GetAsync(new Uri(route, UriKind.Relative), ct);
    var bytes = (await response.Content.ReadAsByteArrayAsync(ct)).Length;
    var encoding = response.Content.Headers.ContentEncoding.Count > 0
        ? string.Join(", ", response.Content.Headers.ContentEncoding)
        : "(none)";

    Console.WriteLine($"  file             : {Trunc(file.Path, 52)}");
    Console.WriteLine($"  status           : {(int)response.StatusCode} {response.StatusCode}");
    Console.WriteLine($"  sent             : Accept-Encoding: gzip, deflate");
    Console.WriteLine($"  Content-Encoding : {encoding}");
    Console.WriteLine($"  bytes on wire    : {bytes:N0}");
    Console.WriteLine();
    Console.WriteLine(response.Content.Headers.ContentEncoding.Count > 0
        ? "  -> ADO DOES compress authenticated responses. AutomaticDecompression earns its keep."
        : "  -> ADO does NOT compress this response. AutomaticDecompression costs nothing but buys\n" +
          "     nothing here either; the setting stays (free) but claim no bandwidth win from it.");
});

Header("done");
// Non-zero if any probe failed, so a script/wrapper sees a broken route as failure
// (each probe still printed its own error above and the rest continued).
return anyFailed ? 1 : 0;

// Returns the PR's latest iteration and its changed files, or (null, []) when it has neither —
// used to pick a PR that can actually exercise the diff path.
async Task<(PrIteration? Iteration, IReadOnlyList<FileChange> Changes)> IterationAndChangesAsync(PullRequest pr)
{
    var it = await git.GetLatestIterationAsync(pr.RepositoryId, pr.PullRequestId, pr.ProjectName, ct);
    if (it is null)
    {
        return (null, []);
    }
    var ch = await git.GetIterationChangesAsync(pr.RepositoryId, pr.PullRequestId, it.Id, pr.ProjectName, ct);
    return (it, ch);
}

static async Task<double> TimeAsync<T>(Func<Task<T>> call)
{
    var sw = Stopwatch.StartNew();
    await call();
    sw.Stop();
    return sw.Elapsed.TotalMilliseconds;
}

static string? ParseContext(string[] argv)
{
    var i = Array.IndexOf(argv, "--context");
    return i >= 0 && i + 1 < argv.Length ? argv[i + 1] : null;
}

static void Header(string title)
{
    Console.WriteLine();
    Console.WriteLine($"== {title} ".PadRight(78, '='));
}

async Task Probe(string title, Func<Task> body)
{
    Header(title);
    try
    {
        await body();
    }
    catch (Exception ex)
    {
        // Diagnostic tool: surface the failing route's exact type + message (a 404/401/shape
        // mismatch here IS the finding) instead of aborting the remaining probes — but record
        // the failure so the process exit code reflects it.
        anyFailed = true;
        Console.WriteLine($"  !! {ex.GetType().Name}: {FirstLine(ex.Message)}");
        if (ex is AdoApiException ado)
        {
            Console.WriteLine($"     status={(int)ado.StatusCode} {ado.StatusCode}");
        }
    }
}

static string FirstLine(string message)
{
    var nl = message.IndexOf('\n');
    return (nl < 0 ? message : message[..nl]).TrimEnd('\r');
}

static string Trunc(string value, int max) =>
    value.Length <= max ? value : value[..Math.Max(0, max - 1)] + "…";
