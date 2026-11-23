using System.Collections.Concurrent;

namespace SlofferWatcher;

/// <summary>
/// Polls ClickUp, matches tasks to triggers, claims them and launches Claude.
/// Deliberately dumb: it routes work and never interprets it.
/// </summary>
public sealed class Worker(
    WatcherSettings settings,
    ClickUpClient clickUp,
    ClaudeRunner runner,
    IHostApplicationLifetime lifetime,
    ILogger<Worker> logger) : BackgroundService
{
    private readonly SemaphoreSlim _slots = new(settings.Claude.MaxConcurrency, settings.Claude.MaxConcurrency);
    private readonly ConcurrentDictionary<string, Task> _running = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Sloffer watcher started. List={ListId} PollEvery={Interval}s MaxConcurrency={Max} Timeout={Timeout}min DryRun={DryRun} Agents=[{Agents}] Triggers=[{Triggers}]",
            settings.ClickUp.ListId, settings.ClickUp.PollIntervalSeconds, settings.Claude.MaxConcurrency,
            settings.Claude.TimeoutMinutes, settings.Claude.DryRun,
            string.Join(", ", settings.Agents.Keys),
            string.Join(", ", settings.Triggers.Select(t => $"{t.Agent}:{t.Status}->{t.Skill}")));
        if (settings.Claude.DryRun)
            logger.LogWarning("DryRun is ON: Claude is never launched and no ClickUp statuses change. Only logging what would happen.");

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(settings.ClickUp.PollIntervalSeconds));
        try
        {
            do
            {
                try
                {
                    await PollOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Poll failed; will try again next interval");
                }

                if (settings.RunOnce)
                {
                    await Task.WhenAll(_running.Values);
                    logger.LogInformation("--once: single poll finished, stopping.");
                    lifetime.StopApplication();
                    return;
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        // Shutdown: running jobs see the cancelled token and kill their process trees.
        await Task.WhenAll(_running.Values);
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        var statuses = settings.Triggers.Select(t => t.Status).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var tasks = await clickUp.GetTasksAsync(statuses, ct);
        logger.LogInformation("Poll: {Count} task(s) in trigger statuses [{Statuses}]", tasks.Count, string.Join(", ", statuses));

        foreach (var task in tasks)
        {
            if (_running.ContainsKey(task.Id)) continue;

            if (task.Agent is null)
            {
                logger.LogInformation("Skip {TaskId} \"{Name}\": no {Field} set", task.Id, task.Name, settings.ClickUp.AgentFieldName);
                continue;
            }

            var trigger = settings.Triggers.FirstOrDefault(t =>
                Eq(t.Status, task.Status) && Eq(t.Agent, task.Agent));
            if (trigger is null)
            {
                logger.LogDebug("Skip {TaskId}: no trigger for status '{Status}' + agent '{Agent}'", task.Id, task.Status, task.Agent);
                continue;
            }

            if (!settings.Agents.TryGetValue(trigger.Agent, out var agent))
            {
                logger.LogInformation("Skip {TaskId} \"{Name}\": agent '{Agent}' is not configured", task.Id, task.Name, task.Agent);
                continue;
            }

            if (trigger.RequireDependenciesDone && task.WaitingOn.Count > 0)
            {
                var blockers = await GetUnfinishedAsync(task.WaitingOn, ct);
                if (blockers.Count > 0)
                {
                    logger.LogInformation("Skip {TaskId} \"{Name}\": waiting on unfinished task(s) {Blockers}", task.Id, task.Name, string.Join(", ", blockers));
                    continue;
                }
            }

            if (settings.Claude.DryRun)
            {
                logger.LogInformation(
                    "[DryRun] Would claim {TaskId} \"{Name}\" ({Status} -> {ClaimStatus}) and run `{Exe} {Args}` with stdin \"/{Skill} {TaskId2}\" in {Path}",
                    task.Id, task.Name, task.Status, trigger.ClaimStatus,
                    settings.Claude.ExecutablePath, string.Join(' ', runner.BuildArguments(agent)),
                    trigger.Skill, task.Id, agent.Path);
                continue;
            }

            if (!await _slots.WaitAsync(0, ct))
            {
                logger.LogInformation("All {Max} run slot(s) busy; {TaskId} waits for a later poll", settings.Claude.MaxConcurrency, task.Id);
                break;
            }

            // Claim before launch: if the status update fails, don't launch.
            try
            {
                await clickUp.SetStatusAsync(task.Id, trigger.ClaimStatus, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _slots.Release();
                logger.LogError(ex, "Could not claim {TaskId} (-> {ClaimStatus}); not launching", task.Id, trigger.ClaimStatus);
                continue;
            }

            logger.LogInformation("Claimed {TaskId} \"{Name}\" ({Status} -> {ClaimStatus}); launching /{Skill} as {Agent}",
                task.Id, task.Name, task.Status, trigger.ClaimStatus, trigger.Skill, trigger.Agent);

            var job = RunJobAsync(task, trigger, agent, ct);
            _running[task.Id] = job;
            _ = job.ContinueWith(_ =>
            {
                _running.TryRemove(task.Id, out Task? _);
                _slots.Release();
            }, TaskScheduler.Default);
        }
    }

    private async Task RunJobAsync(ClickUpTask task, TriggerOptions trigger, AgentOptions agent, CancellationToken ct)
    {
        ClaudeRunResult result;
        try
        {
            result = await runner.RunAsync(task.Id, trigger.Agent, agent, trigger.Skill, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Run crashed for {TaskId}", task.Id);
            result = new ClaudeRunResult(false, $"watcher error: {ex.Message}", null, false, TimeSpan.Zero, null, null, null);
        }

        logger.LogInformation(
            "Run finished: task={TaskId} agent={Agent} skill={Skill} duration={Duration:g} exit={ExitCode} timedOut={TimedOut} success={Success} cost_usd={Cost} usage={Usage} log={LogFile}",
            task.Id, trigger.Agent, trigger.Skill, result.Duration, result.ExitCode?.ToString() ?? "n/a",
            result.TimedOut, result.Success, result.CostUsd, result.Usage ?? "n/a", result.LogFile ?? "n/a");

        if (result.Success) return; // the skill itself sets the next status

        // No automatic retries: report and mark failed.
        var comment =
            $"Sloffer watcher: /{trigger.Skill} run failed: {result.FailureReason}.\n" +
            $"Exit code: {result.ExitCode?.ToString() ?? "n/a"}, duration: {result.Duration:g}.\n" +
            $"Raw output log (watcher machine): {result.LogFile ?? "n/a"}";
        try
        {
            await clickUp.AddCommentAsync(task.Id, comment, CancellationToken.None);
            await clickUp.SetStatusAsync(task.Id, settings.ClickUp.FailedStatus, CancellationToken.None);
            logger.LogWarning("Marked {TaskId} as '{Failed}': {Reason}", task.Id, settings.ClickUp.FailedStatus, result.FailureReason);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not report failure on {TaskId}", task.Id);
        }
    }

    private async Task<List<string>> GetUnfinishedAsync(IEnumerable<string> taskIds, CancellationToken ct)
    {
        var unfinished = new List<string>();
        foreach (var id in taskIds)
        {
            try
            {
                if (!await clickUp.IsDoneAsync(id, ct)) unfinished.Add(id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not check dependency {DependencyId}; treating it as unfinished", id);
                unfinished.Add(id);
            }
        }
        return unfinished;
    }

    private static bool Eq(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
}
