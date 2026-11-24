using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlofferWatcher;

/// <param name="WaitingOn">IDs of tasks this task depends on (is waiting on).</param>
public sealed record ClickUpTask(string Id, string Name, string Status, string? Agent, IReadOnlyList<string> WaitingOn);

/// <summary>Thin wrapper over the ClickUp REST API v2: only the calls the watcher needs.</summary>
public sealed class ClickUpClient(HttpClient http, WatcherSettings settings)
{
    private const int MaxPages = 50;

    /// <summary>All open tasks (including subtasks) on the configured list in any of the given statuses.</summary>
    public async Task<List<ClickUpTask>> GetTasksAsync(IEnumerable<string> statuses, CancellationToken ct)
    {
        var statusList = statuses.ToList();
        var result = new List<ClickUpTask>();
        for (var page = 0; page < MaxPages; page++)
        {
            var url = new StringBuilder($"list/{Uri.EscapeDataString(settings.ClickUp.ListId)}/task?page={page}&subtasks=true&include_closed=false");
            foreach (var s in statusList)
                url.Append("&statuses[]=").Append(Uri.EscapeDataString(s));

            var json = await SendAsync(HttpMethod.Get, url.ToString(), null, ct);
            var tasks = json["tasks"] as JsonArray ?? [];
            foreach (var t in tasks.OfType<JsonObject>())
                result.Add(ParseTask(t));

            var lastPage = json["last_page"] is JsonValue v && v.TryGetValue<bool>(out var lp) ? lp : true;
            if (lastPage || tasks.Count == 0) break;
        }
        return result;
    }

    /// <summary>True when the task's status is closed/done.</summary>
    public async Task<bool> IsDoneAsync(string taskId, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, $"task/{Uri.EscapeDataString(taskId)}", null, ct);
        var type = Str(json["status"]?["type"]);
        var name = Str(json["status"]?["status"]);
        return type is "closed" or "done" || string.Equals(name, "done", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The task's name, markdown description and comments (oldest first), for passing to the agent.</summary>
    public async Task<TaskSpec> GetTaskSpecAsync(string taskId, CancellationToken ct)
    {
        var id = Uri.EscapeDataString(taskId);
        var task = await SendAsync(HttpMethod.Get, $"task/{id}?include_markdown_description=true", null, ct);
        var commentJson = await SendAsync(HttpMethod.Get, $"task/{id}/comment", null, ct);

        var description = Str(task["markdown_description"]);
        if (string.IsNullOrWhiteSpace(description)) description = Str(task["text_content"]);
        if (string.IsNullOrWhiteSpace(description)) description = Str(task["description"]);

        // Only the first page of comments (ClickUp returns a page at a time); fine for the few comments a task has.
        var comments = (commentJson["comments"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(cm => new TaskComment(
                Str(cm["user"]?["username"]) ?? "unknown",
                ParseMillis(Str(cm["date"])),
                (Str(cm["comment_text"]) ?? "").Trim()))
            .Where(cm => cm.Text.Length > 0)
            .OrderBy(cm => cm.Date ?? DateTimeOffset.MinValue)
            .ToList();

        return new TaskSpec(Str(task["id"]) ?? taskId, Str(task["name"]) ?? "", description ?? "", comments);
    }

    private static DateTimeOffset? ParseMillis(string? value) =>
        long.TryParse(value, out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;

    public Task SetStatusAsync(string taskId, string status, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"task/{Uri.EscapeDataString(taskId)}", new JsonObject { ["status"] = status }, ct);

    public Task AddCommentAsync(string taskId, string text, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"task/{Uri.EscapeDataString(taskId)}/comment",
            new JsonObject { ["comment_text"] = text, ["notify_all"] = false }, ct);

    private ClickUpTask ParseTask(JsonObject t)
    {
        var id = Str(t["id"]) ?? "";
        var waitingOn = (t["dependencies"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(d => Str(d["task_id"]) == id)          // entries where *this* task depends on another
            .Select(d => Str(d["depends_on"]))
            .Where(x => !string.IsNullOrEmpty(x))
            .Select(x => x!)
            .Distinct()
            .ToList();

        return new ClickUpTask(
            id,
            Str(t["name"]) ?? "",
            Str(t["status"]?["status"]) ?? "",
            ReadAgent(t["custom_fields"] as JsonArray),
            waitingOn);
    }

    /// <summary>
    /// Reads the Agent field. Supports a dropdown (value = option id or orderindex) or a plain text field.
    /// If several fields share the name, the first one with a value wins.
    /// </summary>
    private string? ReadAgent(JsonArray? fields) =>
        (fields ?? [])
            .OfType<JsonObject>()
            .Where(f => string.Equals(Str(f["name"]), settings.ClickUp.AgentFieldName, StringComparison.OrdinalIgnoreCase))
            .Select(ReadFieldValue)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string? ReadFieldValue(JsonObject field)
    {
        var raw = Str(field["value"]);
        if (string.IsNullOrWhiteSpace(raw)) return null;

        if (Str(field["type"]) == "drop_down")
        {
            var option = (field["type_config"]?["options"] as JsonArray ?? [])
                .OfType<JsonObject>()
                .FirstOrDefault(o => Str(o["id"]) == raw || Str(o["orderindex"]) == raw);
            return Str(option?["name"])?.Trim();
        }
        return raw.Trim();
    }

    private async Task<JsonNode> SendAsync(HttpMethod method, string relativeUrl, JsonNode? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, relativeUrl);
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var snippet = text.Length > 500 ? text[..500] + "…" : text;
            throw new HttpRequestException($"ClickUp {method} {relativeUrl} failed: {(int)response.StatusCode} {response.ReasonPhrase}. {snippet}", null, response.StatusCode);
        }
        try
        {
            return JsonNode.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text) ?? new JsonObject();
        }
        catch (JsonException ex)
        {
            throw new HttpRequestException($"ClickUp {method} {relativeUrl} returned non-JSON content.", ex);
        }
    }

    internal static string? Str(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => node.ToJsonString().Trim('"'),
    };
}
