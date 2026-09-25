using Microsoft.EntityFrameworkCore;
using Qoder2Api.Components;
using Qoder2Api.Configuration;
using Qoder2Api.Endpoints;
using Qoder2Api.Services.Database;
using Qoder2Api.Services.Qoder;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseStaticWebAssets();

builder.Configuration.Sources.Clear();
builder.Configuration
    .SetBasePath(builder.Environment.ContentRootPath)
    .AddXmlFile("appsettings.xml", optional: true, reloadOnChange: true)
    .AddXmlFile($"appsettings.{builder.Environment.EnvironmentName}.xml", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables()
    .AddCommandLine(args);

var serverOptions = builder.Configuration
    .GetSection(ServerOptions.SectionName)
    .Get<ServerOptions>() ?? new ServerOptions();

if (!string.IsNullOrWhiteSpace(serverOptions.Urls) && !HasExternalUrls(args))
{
    builder.WebHost.UseUrls(serverOptions.Urls);
}

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// SQLite 数据库。相对路径按内容根目录（cwd）解析，默认 save/SaveData.db。
string dbPath = Path.IsPathRooted(serverOptions.DatabasePath)
    ? serverOptions.DatabasePath
    : Path.Combine(Directory.GetCurrentDirectory(), serverOptions.DatabasePath);
string? dbDir = Path.GetDirectoryName(dbPath);
if (!string.IsNullOrEmpty(dbDir) && !Directory.Exists(dbDir))
{
    Directory.CreateDirectory(dbDir);
}

string connStr = $"Data Source={dbPath};Default Timeout=5";
builder.Services.AddDbContextFactory<AppDbContext>(options =>
{
    options.UseSqlite(connStr);
});

builder.Services.AddHttpClient(QoderHttp.ClientName, c =>
{
    c.Timeout = Timeout.InfiniteTimeSpan;
});

builder.Services.AddSingleton<SqliteDbService>();
builder.Services.AddSingleton<QoderAuthService>();
builder.Services.AddSingleton<QoderProxyService>();
builder.Services.AddSingleton<QoderCreditsService>();

builder.Services.Configure<QueueOptions>(builder.Configuration.GetSection(QueueOptions.SectionName));
builder.Services.AddSingleton<QoderQueueClient>();
builder.Services.AddSingleton<QoderModelCatalog>();
builder.Services.Configure<RefreshIntervalOptions>(builder.Configuration.GetSection(RefreshIntervalOptions.SectionName));
builder.Services.AddSingleton<ModelCatalogRefresher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ModelCatalogRefresher>());

builder.Services.Configure<PoolOptions>(builder.Configuration.GetSection(PoolOptions.SectionName));
builder.Services.AddSingleton<QoderPool>();

builder.Services.AddSingleton<PoolFlusher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PoolFlusher>());

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

var app = builder.Build();

QoderConstants.ReloadModels(
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(QoderConstants)));

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseCors();
app.UseAntiforgery();

app.MapStaticAssets();

app.MapChatEndpoint();
app.MapQoderApiRoutes();
app.MapAdminApiRoutes();

// Map Blazor UI with interactive server mode
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static bool HasExternalUrls(string[] argv) =>
    !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS"))
    || argv.Any(a => a.StartsWith("--urls", StringComparison.OrdinalIgnoreCase));
