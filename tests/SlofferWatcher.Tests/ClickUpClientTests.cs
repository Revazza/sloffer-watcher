using System.Net;
using System.Text;
using SlofferWatcher;

namespace SlofferWatcher.Tests;

public class ClickUpClientTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add($"{request.Method} {request.RequestUri!.PathAndQuery}");
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static ClickUpClient Client(StubHandler handler)
    {
        var settings = new WatcherSettings
        {
            ClickUp = new ClickUpOptions { ListId = "1", ApiToken = "pk_test" },
            Claude = new ClaudeOptions(),
            Agents = new Dictionary<string, AgentOptions>(),
            Triggers = [],
        };
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.clickup.com/api/v2/") };
        return new ClickUpClient(http, settings);
    }

    [Fact]
    public async Task GetTaskSpecAsync_reads_description_and_orders_comments_oldest_first()
    {
        var handler = new StubHandler(req => req.RequestUri!.AbsolutePath.EndsWith("/comment")
            ? Json("""
                   {"comments":[
                     {"comment_text":"second","user":{"username":"watcher"},"date":"1795480000000"},
                     {"comment_text":"first","user":{"username":"sandro"},"date":"1795470000000"},
                     {"comment_text":"   ","user":{"username":"x"},"date":"1795490000000"}
                   ]}
                   """)
            : Json("""{"id":"abc","name":"Backend: thing","markdown_description":"## Goal\nDo it"}"""));

        var spec = await Client(handler).GetTaskSpecAsync("abc", CancellationToken.None);

        Assert.Equal("Backend: thing", spec.Name);
        Assert.Equal("## Goal\nDo it", spec.Description);
        Assert.Equal(new[] { "first", "second" }, spec.Comments.Select(c => c.Text).ToArray());
        Assert.Equal("sandro", spec.Comments[0].Author);
        Assert.Contains("GET /api/v2/task/abc?include_markdown_description=true", handler.Requests);
        Assert.Contains("GET /api/v2/task/abc/comment", handler.Requests);
    }

    [Fact]
    public async Task GetTaskSpecAsync_falls_back_to_plain_text_when_markdown_is_empty()
    {
        var handler = new StubHandler(req => req.RequestUri!.AbsolutePath.EndsWith("/comment")
            ? Json("""{"comments":[]}""")
            : Json("""{"id":"abc","name":"n","markdown_description":"","text_content":"plain text"}"""));

        var spec = await Client(handler).GetTaskSpecAsync("abc", CancellationToken.None);

        Assert.Equal("plain text", spec.Description);
        Assert.Empty(spec.Comments);
    }

    [Fact]
    public async Task GetTaskSpecAsync_throws_when_clickup_returns_an_error()
    {
        var handler = new StubHandler(_ => Json("""{"err":"Rate limit"}""", HttpStatusCode.TooManyRequests));

        await Assert.ThrowsAsync<HttpRequestException>(() => Client(handler).GetTaskSpecAsync("abc", CancellationToken.None));
    }
}
