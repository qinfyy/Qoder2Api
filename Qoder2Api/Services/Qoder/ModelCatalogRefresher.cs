using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Qoder2Api.Configuration;

namespace Qoder2Api.Services.Qoder;

/// <summary>
/// 从上游同步模型目录，写回 models.xml。
///
/// 两条路径，行为不同：
/// - <see cref="TryRefreshAsync"/> 后台定时（周期默认 **2 分钟**，与官方客户端一致，
///   客户端常量 eec = 12e4）：只刷新已有模型的倍率 / 折扣，**不新增模型**。
/// - <see cref="SyncManualAsync"/> 管理员手动：缺失的模型一并新增，用于首次生成 models.xml。
///
/// 周期设为 0 可关闭后台同步（此时只能靠手动）。
/// </summary>
public sealed class ModelCatalogRefresher : BackgroundService
{
    private readonly QoderModelCatalog _catalog;
    private readonly QoderAuthService _auth;
    private readonly TimeSpan _interval;
    private readonly ILogger<ModelCatalogRefresher> _log;

    /// <summary>目录服务（端点需读最近一次快照）。</summary>
    public QoderModelCatalog Catalog => _catalog;

    public ModelCatalogRefresher(
        QoderModelCatalog catalog,
        QoderAuthService auth,
        IOptions<RefreshIntervalOptions> options,
        ILogger<ModelCatalogRefresher> log)
    {
        _catalog = catalog;
        _auth = auth;
        _interval = options.Value.ModelCatalog;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_interval <= TimeSpan.Zero)
        {
            _log.LogInformation("模型目录定时同步已关闭（仅在前端手动刷新）");
            return;
        }

        // 启动后先等一个周期再拉，避免拖慢启动；首次数据由前端或首次同步补齐。
        try
        {
            await Task.Delay(_interval, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await TryRefreshAsync(stoppingToken);
        }
    }

    /// <summary>
    /// 后台定时同步：只刷新已有模型的倍率 / 折扣，不新增模型。
    /// 失败只记日志——目录是增强信息，失败不影响代理主流程。
    /// </summary>
    public async Task<bool> TryRefreshAsync(CancellationToken ct = default)
        => (await SyncAsync(allowAdd: false, ct)).Success;

    /// <summary>
    /// 管理员手动同步：上游有、models.xml 里没有的模型一并新增，
    /// 用于首次生成 models.xml 或补上上游新上的模型。
    /// </summary>
    public Task<CatalogSyncOutcome> SyncManualAsync(CancellationToken ct = default)
        => SyncAsync(allowAdd: true, ct);

    /// <summary>
    /// 拉一次上游目录并合并。
    /// 用池里的活跃账号（目录是账号无关的，任意一个有效账号即可）。
    /// </summary>
    private async Task<CatalogSyncOutcome> SyncAsync(bool allowAdd, CancellationToken ct)
    {
        try
        {
            var acc = _auth.Database.GetActiveAccount();
            if (acc is null)
            {
                return new CatalogSyncOutcome(false, 0, 0, "尚未连接任何账号");
            }
            var creds = await _auth.GetCredsForAccountAsync(acc, ct);
            var snap = await _catalog.RefreshAsync(creds, ct);
            if (!snap.FromUpstream)
            {
                return new CatalogSyncOutcome(false, 0, 0, snap.Error ?? "拉取失败");
            }
            var merged = QoderConstants.MergeUpstreamCatalog(snap.Models, allowAdd, _log);
            return new CatalogSyncOutcome(true, merged.Added, merged.Updated, null);
        }
        catch (OperationCanceledException)
        {
            return new CatalogSyncOutcome(false, 0, 0, "请求被取消");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "同步模型目录失败");
            return new CatalogSyncOutcome(false, 0, 0, ex.Message);
        }
    }
}

/// <summary>一次模型目录同步的结果。</summary>
public sealed record CatalogSyncOutcome(bool Success, int Added, int Updated, string? Error);
