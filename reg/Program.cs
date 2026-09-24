using Microsoft.EntityFrameworkCore;
using reg.Components;
using reg.Endpoints;
using reg.Services.Database;
using reg.Services.Qoder;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseStaticWebAssets();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// EF Core SQLite in ./save/qoder2api.db
string saveDir = Path.Combine(Directory.GetCurrentDirectory(), "save");
if (!Directory.Exists(saveDir))
{
    Directory.CreateDirectory(saveDir);
}
string dbPath = Path.Combine(saveDir, "qoder2api.db");

// Default Timeout=5：Microsoft.Data.Sqlite 默认 30s，且它是 sqlite3_busy_timeout 语义
// —— 并发写时【阻塞等待】而非快速失败。号池高频写场景下 30s 会把线程池吃干，
// 连带拖垮 Blazor Server 的渲染调度（它对线程池饥饿极敏感）。5s 足够且不会雪崩。
string connStr = $"Data Source={dbPath};Default Timeout=5";
builder.Services.AddDbContextFactory<AppDbContext>(options =>
{
    options.UseSqlite(connStr);
});

// 具名 HttpClient：Timeout 必须显式设为无限。
// AddHttpClient() 的默认 Timeout=100s，而 SSE 长回答（reasoning 模型动辄数分钟）
// 会在 100s 处被 TaskCanceledException 掐断、并被误判成上游故障。
// 真正的超时由 QoderStreamSession 自己管（首字节超时 + 读空闲超时）。
// 另外：Singleton 直接注入 HttpClient 会把 Transient 的实例永久捕获（handler 轮换
// 失效、DNS 变更不生效），故统一改为注入 IHttpClientFactory 按需 CreateClient。
builder.Services.AddHttpClient(QoderHttp.ClientName, c =>
{
    c.Timeout = Timeout.InfiniteTimeSpan;
    c.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));
});

builder.Services.AddSingleton<SqliteDbService>();
builder.Services.AddSingleton<QoderAuthService>();
builder.Services.AddSingleton<QoderProxyService>();

// 号池调优参数（可由 appsettings 的 "Pool" 节覆盖，缺省即用 PoolOptions 的默认值）。
var poolOptions = builder.Configuration.GetSection("Pool").Get<PoolOptions>() ?? new PoolOptions();
builder.Services.AddSingleton(poolOptions);

// 号池。**不能**注入 AppDbContext——AddDbContextFactory 同时把 AppDbContext 注册为
// Scoped，而本类是 Singleton，构造函数里写 AppDbContext 会在 Build() 时直接抛
// "Cannot consume scoped service from singleton"。池只依赖同样是 Singleton 的
// SqliteDbService，且只在后台循环里碰数据库。
builder.Services.AddSingleton<QoderPool>();

// 后台落盘器与账号列表同步器。注意写法：先 AddSingleton 再 AddHostedService
// 取同一个实例——直接 AddHostedService<QoderPool>() 会把池注册成 IHostedService
// 而不是可注入的单例，两个不同的实例会各持一份状态。
builder.Services.AddSingleton<PoolFlusher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PoolFlusher>());

// CORS for web clients (e.g. NextChat, LobeChat, Cherry Studio, Cursor, etc.)
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

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseCors();
app.UseAntiforgery();

app.MapStaticAssets();

// Map OpenAI compatible proxy endpoints & Admin REST endpoints
app.MapChatEndpoint();   // /v1/chat/completions（带号池轮换）
app.MapQoderApiRoutes(); // /v1/models、/api/status
app.MapAdminApiRoutes();

// Map Blazor UI with interactive server mode
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
