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

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

string saveDir = Path.Combine(Directory.GetCurrentDirectory(), "save");
if (!Directory.Exists(saveDir))
{
    Directory.CreateDirectory(saveDir);
}
string dbPath = Path.Combine(saveDir, "qoder2api.db");

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
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("reg.Services.Qoder.QoderConstants"));

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
