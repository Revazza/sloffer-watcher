using SlofferWatcher;

namespace SlofferWatcher.Tests;

public class ResultLineTests
{
    [Fact]
    public void Parse_valid_line_returns_result()
    {
        var text = "Done.\nSLOFFER_RESULT {\"status\":\"pr_open\",\"branch\":\"task/abc-x\",\"prUrl\":\"https://github.com/o/r/pull/1\",\"summary\":\"All good\"}";

        var result = ResultLine.Parse(text);

        Assert.NotNull(result);
        Assert.True(result.IsPrOpen);
        Assert.Equal("task/abc-x", result.Branch);
        Assert.Equal("https://github.com/o/r/pull/1", result.PrUrl);
        Assert.Equal("All good", result.Summary);
    }

    [Fact]
    public void Parse_takes_the_last_of_several_lines()
    {
        var text =
            "SLOFFER_RESULT {\"status\":\"failed\",\"summary\":\"first\"}\r\n" +
            "more text\r\n" +
            "SLOFFER_RESULT {\"status\":\"pr_open\",\"summary\":\"last\"}\r\n";

        var result = ResultLine.Parse(text);

        Assert.NotNull(result);
        Assert.True(result.IsPrOpen);
        Assert.Equal("last", result.Summary);
    }

    [Fact]
    public void Parse_missing_line_returns_null()
    {
        Assert.Null(ResultLine.Parse("I finished the task, trust me."));
        Assert.Null(ResultLine.Parse(null));
        Assert.Null(ResultLine.Parse("   "));
    }

    [Fact]
    public void Parse_malformed_json_returns_null()
    {
        Assert.Null(ResultLine.Parse("SLOFFER_RESULT {not json"));
        Assert.Null(ResultLine.Parse("SLOFFER_RESULT [1,2,3]"));
    }

    [Fact]
    public void Parse_missing_status_returns_null()
    {
        Assert.Null(ResultLine.Parse("SLOFFER_RESULT {\"summary\":\"no status\"}"));
    }

    [Fact]
    public void Parse_unknown_status_is_returned_but_is_not_pr_open()
    {
        var result = ResultLine.Parse("SLOFFER_RESULT {\"status\":\"maybe\"}");

        Assert.NotNull(result);
        Assert.False(result.IsPrOpen);
    }
}
