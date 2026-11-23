using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlofferWatcher;

public sealed record ClaudeRunResult(
    bool Success,
    string? FailureReason,
    int? ExitCode,
    bool TimedOut,
    TimeSpan Duration,
    double? CostUsd,
    string? Usage,
    string? LogFile);

/// <summary>Runs Claude Code headless for one task and captures its JSON result.</summary>
public sealed class ClaudeRunner(WatcherSettings settings, ILogger<ClaudeRunner> logger)
{
    public async Task<ClaudeRunResult> RunAsync(string taskId, string agentName, AgentOptions agent, string skill, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(settings.Claude.ExecutablePath)
        {
            WorkingDirectory = agent.Path,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in BuildArguments(agent)) psi.ArgumentList.Add(arg);

        // The prompt goes in via stdin to avoid Windows quoting issues.
        var prompt = $"/{skill} {taskId}";
        var started = DateTime.Now;
        var sw = Stopwatch.StartNew();

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            var log = WriteRunLog(started, taskId, agentName, skill, psi, prompt, null, false, sw.Elapsed, "", ex.ToString());
            return new ClaudeRunResult(false, $"could not start '{settings.Claude.ExecutablePath}': {ex.Message}", null, false, sw.Elapsed, null, null, log);
        }

        using (process)
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

            try
            {
                await process.StandardInput.WriteAsync(prompt);
                process.StandardInput.Close();
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Could not write prompt to Claude stdin for task {TaskId}", taskId);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(settings.Claude.TimeoutMinutes));
            var timedOut = false;
            var cancelled = false;
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                cancelled = ct.IsCancellationRequested;
                timedOut = !cancelled;
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) { logger.LogWarning(ex, "Failed to kill Claude process tree for task {TaskId}", taskId); }
                await process.WaitForExitAsync(CancellationToken.None);
            }
            sw.Stop();

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            int? exitCode = process.HasExited ? process.ExitCode : null;
            var logFile = WriteRunLog(started, taskId, agentName, skill, psi, prompt, exitCode, timedOut, sw.Elapsed, stdout, stderr);

            if (timedOut)
                return new ClaudeRunResult(false, $"timed out after {settings.Claude.TimeoutMinutes} min (process tree killed)", exitCode, true, sw.Elapsed, null, null, logFile);
            if (cancelled)
                return new ClaudeRunResult(false, "watcher was shut down during the run (process tree killed)", exitCode, false, sw.Elapsed, null, null, logFile);

            JsonNode? json = null;
            try { json = JsonNode.Parse(stdout); } catch (JsonException) { }

            var cost = json?["total_cost_usd"] is JsonValue c && c.TryGetValue<double>(out var d) ? d : (double?)null;
            var usage = json?["usage"]?.ToJsonString();

            if (exitCode != 0)
                return new ClaudeRunResult(false, $"Claude exited with code {exitCode}", exitCode, false, sw.Elapsed, cost, usage, logFile);
            if (json is null)
                return new ClaudeRunResult(false, "Claude output was not valid JSON", exitCode, false, sw.Elapsed, cost, usage, logFile);
            if (json["is_error"] is JsonValue e && e.TryGetValue<bool>(out var isError) && isError)
                return new ClaudeRunResult(false, $"Claude reported an error ({ClickUpClient.Str(json["subtype"]) ?? "unknown"})", exitCode, false, sw.Elapsed, cost, usage, logFile);

            return new ClaudeRunResult(true, null, exitCode, false, sw.Elapsed, cost, usage, logFile);
        }
    }

    public IEnumerable<string> BuildArguments(AgentOptions agent)
    {
        yield return "-p";
        yield return "--model";
        yield return agent.Model;
        yield return "--output-format";
        yield return "json";
        foreach (var dir in agent.AddDirs)
        {
            yield return "--add-dir";
            yield return dir;
        }
    }

    private string? WriteRunLog(DateTime started, string taskId, string agentName, string skill, ProcessStartInfo psi,
        string prompt, int? exitCode, bool timedOut, TimeSpan duration, string stdout, string stderr)
    {
        try
        {
            Directory.CreateDirectory(settings.Claude.RunLogDirectory);
            var path = Path.Combine(settings.Claude.RunLogDirectory, $"{started:yyyyMMdd-HHmmss}_{taskId}_{skill}.log");
            var sb = new StringBuilder()
                .AppendLine($"task:      {taskId}")
                .AppendLine($"agent:     {agentName}")
                .AppendLine($"skill:     {skill}")
                .AppendLine($"started:   {started:O}")
                .AppendLine($"duration:  {duration}")
                .AppendLine($"exit code: {exitCode?.ToString() ?? "n/a"}")
                .AppendLine($"timed out: {timedOut}")
                .AppendLine($"cwd:       {psi.WorkingDirectory}")
                .AppendLine($"command:   {psi.FileName} {string.Join(' ', psi.ArgumentList)}")
                .AppendLine($"stdin:     {prompt}")
                .AppendLine("----- stdout -----")
                .AppendLine(stdout)
                .AppendLine("----- stderr -----")
                .AppendLine(stderr);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            return path;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not write run log for task {TaskId}", taskId);
            return null;
        }
    }
}
