namespace Cobalt.Core.Models;

/// <summary>A single branch-policy evaluation result for a pull request (Policy Evaluations API).</summary>
public sealed record PolicyEvaluation(string DisplayName, string Status, bool IsBlocking);
