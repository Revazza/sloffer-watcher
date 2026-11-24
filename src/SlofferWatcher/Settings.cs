namespace SlofferWatcher;

public sealed class ClickUpOptions
{
    public string ApiBaseUrl { get; set; } = "https://api.clickup.com/api/v2/";

    /// <summary>Comes from user-secrets or the ClickUp__ApiToken env var. Never put it in appsettings.</summary>
    public string ApiToken { get; set; } = "";

    public string ListId { get; set; } = "";
    public string AgentFieldName { get; set; } = "Agent";
    public string FailedStatus { get; set; } = "failed";

    /// <summary>Status set when a result-line run reports `pr_open`.</summary>
    public string PrOpenStatus { get; set; } = "pr open";
    public int PollIntervalSeconds { get; set; } = 15;
}

public sealed class ClaudeOptions
{
    public string ExecutablePath { get; set; } = "claude";
    public int TimeoutMinutes { get; set; } = 30;
    public int MaxConcurrency { get; set; } = 1;
    public bool DryRun { get; set; } = true;
    public string RunLogDirectory { get; set; } = "logs\\runs";
}

public sealed class AgentOptions
{
    public string Path { get; set; } = "";
    public List<string> AddDirs { get; set; } = [];
    public string Model { get; set; } = "";
}

public sealed class TriggerOptions
{
    public string Status { get; set; } = "";
    public string Agent { get; set; } = "";
    public string Skill { get; set; } = "";
    public string ClaimStatus { get; set; } = "";

    /// <summary>Skip the task while any task it is "waiting on" is not done.</summary>
    public bool RequireDependenciesDone { get; set; }

    /// <summary>
    /// When true the watcher fetches the task over REST, passes it to the skill inside a &lt;task-spec&gt; block,
    /// expects a final `SLOFFER_RESULT {json}` line and sets the next status itself. The agent needs no ClickUp access.
    /// </summary>
    public bool UseResultLine { get; set; }
}

public sealed class WatcherSettings
{
    public required ClickUpOptions ClickUp { get; init; }
    public required ClaudeOptions Claude { get; init; }

    /// <summary>Agent name (case-insensitive) → where and how to run Claude.</summary>
    public required IReadOnlyDictionary<string, AgentOptions> Agents { get; init; }

    public required IReadOnlyList<TriggerOptions> Triggers { get; init; }

    /// <summary>Poll once, wait for any launched runs, then exit (handy for testing).</summary>
    public bool RunOnce { get; init; }

    public static WatcherSettings Load(IConfiguration config, string contentRoot, bool runOnce)
    {
        var clickUp = config.GetSection("ClickUp").Get<ClickUpOptions>() ?? new ClickUpOptions();
        var claude = config.GetSection("Claude").Get<ClaudeOptions>() ?? new ClaudeOptions();
        var agents = config.GetSection("Agents").Get<Dictionary<string, AgentOptions>>() ?? [];
        var triggers = config.GetSection("Triggers").Get<List<TriggerOptions>>() ?? [];

        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(clickUp.ApiToken))
            errors.Add("ClickUp:ApiToken is missing. Set it with `dotnet user-secrets set \"ClickUp:ApiToken\" \"pk_...\"` or the ClickUp__ApiToken env var.");
        if (string.IsNullOrWhiteSpace(clickUp.ListId))
            errors.Add("ClickUp:ListId is missing.");
        if (clickUp.PollIntervalSeconds < 10)
            errors.Add("ClickUp:PollIntervalSeconds must be at least 10.");
        if (claude.MaxConcurrency < 1)
            errors.Add("Claude:MaxConcurrency must be at least 1.");
        if (claude.TimeoutMinutes < 1)
            errors.Add("Claude:TimeoutMinutes must be at least 1.");
        foreach (var t in triggers)
        {
            if (string.IsNullOrWhiteSpace(t.Status) || string.IsNullOrWhiteSpace(t.Agent) ||
                string.IsNullOrWhiteSpace(t.Skill) || string.IsNullOrWhiteSpace(t.ClaimStatus))
                errors.Add($"Trigger '{t.Status}/{t.Agent}' needs Status, Agent, Skill and ClaimStatus.");
        }
        foreach (var (name, a) in agents)
        {
            if (string.IsNullOrWhiteSpace(a.Path)) errors.Add($"Agents:{name}:Path is missing.");
            if (string.IsNullOrWhiteSpace(a.Model)) errors.Add($"Agents:{name}:Model is missing.");
        }
        if (errors.Count > 0)
            throw new InvalidOperationException("Invalid watcher configuration:\n - " + string.Join("\n - ", errors));

        if (!System.IO.Path.IsPathRooted(claude.RunLogDirectory))
            claude.RunLogDirectory = System.IO.Path.GetFullPath(System.IO.Path.Combine(contentRoot, claude.RunLogDirectory));

        return new WatcherSettings
        {
            ClickUp = clickUp,
            Claude = claude,
            Agents = new Dictionary<string, AgentOptions>(agents, StringComparer.OrdinalIgnoreCase),
            Triggers = triggers,
            RunOnce = runOnce,
        };
    }
}
