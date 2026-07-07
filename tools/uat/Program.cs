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

Header("done");
return 0;

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

static async Task Probe(string title, Func<Task> body)
{
    Header(title);
    try
    {
        await body();
    }
    catch (Exception ex)
    {
        // Diagnostic tool: surface the failing route's exact type + message (a 404/401/shape
        // mismatch here IS the finding) instead of aborting the remaining probes.
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
