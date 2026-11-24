using SlofferWatcher;

namespace SlofferWatcher.Tests;

public class RunOutcomesTests
{
    private static readonly TriggerOptions ResultLineTrigger = new()
    {
        Status = "ready", Agent = "Backend", Skill = "implement-task", ClaimStatus = "in progress", UseResultLine = true,
    };

    private static ClaudeRunResult Ok(string? resultText, int exit = 0) =>
        new(true, null, exit, false, TimeSpan.FromSeconds(5), null, null, "C:\\logs\\run.log", resultText);

    [Fact]
    public void Decide_pr_open_result_marks_pr_open_and_includes_the_link()
    {
        var run = Ok("Done.\nSLOFFER_RESULT {\"status\":\"pr_open\",\"branch\":\"task/abc-x\",\"prUrl\":\"https://github.com/o/r/pull/7\",\"summary\":\"Built it\"}");

        var outcome = RunOutcomes.Decide(run, ResultLineTrigger);

        Assert.True(outcome.PrOpen);
        Assert.Contains("https://github.com/o/r/pull/7", outcome.Comment);
        Assert.Contains("Built it", outcome.Comment);
    }

    [Fact]
    public void Decide_exit_zero_without_a_result_line_is_a_failure()
    {
        var outcome = RunOutcomes.Decide(Ok("I could not read the task."), ResultLineTrigger);

        Assert.False(outcome.PrOpen);
        Assert.Contains("SLOFFER_RESULT", outcome.Comment);
    }

    [Fact]
    public void Decide_failed_result_is_a_failure_with_the_agents_summary()
    {
        var run = Ok("SLOFFER_RESULT {\"status\":\"failed\",\"summary\":\"tests could not run: no database\"}");

        var outcome = RunOutcomes.Decide(run, ResultLineTrigger);

        Assert.False(outcome.PrOpen);
        Assert.Contains("tests could not run: no database", outcome.Comment);
    }

    [Fact]
    public void Decide_unknown_status_is_a_failure()
    {
        var outcome = RunOutcomes.Decide(Ok("SLOFFER_RESULT {\"status\":\"maybe\"}"), ResultLineTrigger);

        Assert.False(outcome.PrOpen);
    }

    [Fact]
    public void Decide_failed_run_reports_the_reason_and_log_path()
    {
        var run = new ClaudeRunResult(false, "timed out after 30 min (process tree killed)", null, true, TimeSpan.FromMinutes(30), null, null, "C:\\logs\\run.log");

        var outcome = RunOutcomes.Decide(run, ResultLineTrigger);

        Assert.False(outcome.PrOpen);
        Assert.Contains("timed out after 30 min", outcome.Comment);
        Assert.Contains("C:\\logs\\run.log", outcome.Comment);
    }

    [Fact]
    public void Decide_without_result_line_mode_leaves_success_to_the_skill()
    {
        var trigger = new TriggerOptions { Skill = "shape-idea", UseResultLine = false };

        var outcome = RunOutcomes.Decide(Ok("anything"), trigger);

        Assert.False(outcome.PrOpen);
        Assert.Equal("", outcome.Comment);
    }
}
