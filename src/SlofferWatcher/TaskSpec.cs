using System.Text;
using System.Text.Json;

namespace SlofferWatcher;

public sealed record TaskComment(string Author, DateTimeOffset? Date, string Text);

/// <summary>Everything the agent needs to know about a task, fetched by the watcher over the ClickUp REST API.</summary>
public sealed record TaskSpec(string Id, string Name, string Description, IReadOnlyList<TaskComment> Comments);

/// <summary>Builds the stdin prompt for runs that get the task spec inline (no ClickUp tools for the agent).</summary>
public static class TaskPrompt
{
    public const string OpenTag = "<task-spec>";
    public const string CloseTag = "</task-spec>";

    public static string Build(string skill, TaskSpec spec)
    {
        var sb = new StringBuilder();
        sb.Append('/').Append(skill).Append(' ').AppendLine(spec.Id);
        sb.AppendLine(OpenTag);
        sb.Append("Task id: ").AppendLine(spec.Id);
        sb.Append("Name: ").AppendLine(Neutralise(spec.Name));
        sb.AppendLine("Description:");
        sb.AppendLine(Neutralise(string.IsNullOrWhiteSpace(spec.Description) ? "(empty)" : spec.Description));
        sb.AppendLine("Comments (oldest first):");
        if (spec.Comments.Count == 0)
            sb.AppendLine("(none)");
        foreach (var c in spec.Comments)
        {
            var date = c.Date?.ToString("u") ?? "unknown date";
            sb.Append("- ").Append(Neutralise(c.Author)).Append(", ").Append(date).Append(": ").AppendLine(Neutralise(c.Text));
        }
        sb.Append(CloseTag);
        return sb.ToString();
    }

    /// <summary>Task text must not be able to close (or reopen) the spec block early.</summary>
    internal static string Neutralise(string text) =>
        text.Replace("</task-spec", "<\\/task-spec", StringComparison.OrdinalIgnoreCase)
            .Replace("<task-spec", "<\\task-spec", StringComparison.OrdinalIgnoreCase);
}

public sealed record AgentResult(string Status, string Branch, string PrUrl, string Summary)
{
    public const string PrOpenStatus = "pr_open";
    public bool IsPrOpen => string.Equals(Status, PrOpenStatus, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Parses the `SLOFFER_RESULT {json}` line that implement-task prints as the last line of its final message.</summary>
public static class ResultLine
{
    public const string Prefix = "SLOFFER_RESULT ";

    /// <summary>The last well-formed result line in <paramref name="text"/>, or null if there is none.</summary>
    public static AgentResult? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        string? lastJson = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith(Prefix, StringComparison.Ordinal))
                lastJson = line[Prefix.Length..];
        }
        if (lastJson is null) return null;

        try
        {
            using var doc = JsonDocument.Parse(lastJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var status = Read(root, "status");
            if (string.IsNullOrWhiteSpace(status)) return null;
            return new AgentResult(status, Read(root, "branch"), Read(root, "prUrl"), Read(root, "summary"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Read(JsonElement root, string name) =>
        root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
}

/// <summary>What the watcher writes back to ClickUp after a run.</summary>
public sealed record RunOutcome(bool PrOpen, string Comment);

public static class RunOutcomes
{
    /// <summary>
    /// Exit code 0 is not success by itself: for result-line skills the agent must report `pr_open`.
    /// Every other case becomes a failure with a comment explaining why, so a task never stays `in progress`.
    /// </summary>
    public static RunOutcome Decide(ClaudeRunResult run, TriggerOptions trigger)
    {
        var footer =
            $"Exit code: {run.ExitCode?.ToString() ?? "n/a"}, duration: {run.Duration:g}.\n" +
            $"Raw output log (watcher machine): {run.LogFile ?? "n/a"}";

        if (!run.Success)
            return new RunOutcome(false, $"Sloffer watcher: /{trigger.Skill} run failed: {run.FailureReason}.\n{footer}");

        if (!trigger.UseResultLine)
            return new RunOutcome(false, "");

        var result = ResultLine.Parse(run.ResultText);
        if (result is null)
            return new RunOutcome(false,
                $"Sloffer watcher: /{trigger.Skill} finished but did not print a valid SLOFFER_RESULT line, so the outcome is unknown. Marking failed.\n{footer}");

        if (!result.IsPrOpen)
            return new RunOutcome(false,
                $"Sloffer watcher: /{trigger.Skill} reported '{result.Status}'.\n{Describe(result)}\n{footer}");

        return new RunOutcome(true,
            $"Sloffer watcher: /{trigger.Skill} finished.\n{Describe(result)}\n{footer}");
    }

    private static string Describe(AgentResult r)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(r.PrUrl)) parts.Add($"PR: {r.PrUrl}");
        if (!string.IsNullOrWhiteSpace(r.Branch)) parts.Add($"Branch: {r.Branch}");
        if (!string.IsNullOrWhiteSpace(r.Summary)) parts.Add(r.Summary);
        return string.Join("\n", parts);
    }
}
