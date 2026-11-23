using SlofferWatcher;

// `--once` = poll a single time, wait for any runs, then exit.
var runOnce = args.Contains("--once", StringComparer.OrdinalIgnoreCase);
args = args.Where(a => !string.Equals(a, "--once", StringComparison.OrdinalIgnoreCase)).ToArray();

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    // Load appsettings.json from the build output no matter where `dotnet run` is started.
    ContentRootPath = AppContext.BaseDirectory,
});

// Secrets (ClickUp:ApiToken) come from user-secrets in any environment; env vars and args still win.
builder.Configuration.AddUserSecrets<Program>(optional: true);
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddCommandLine(args);

builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
});

var settings = WatcherSettings.Load(builder.Configuration, builder.Environment.ContentRootPath, runOnce);
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(_ =>
{
    var http = new HttpClient { BaseAddress = new Uri(settings.ClickUp.ApiBaseUrl), Timeout = TimeSpan.FromSeconds(60) };
    http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", settings.ClickUp.ApiToken);
    return new ClickUpClient(http, settings);
});
builder.Services.AddSingleton<ClaudeRunner>();
builder.Services.AddHostedService<Worker>();

builder.Build().Run();
