using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace reg.Services.Qoder;

/// <summary>
/// 定时从上游刷新模型目录（倍率 / 错峰折扣），写回 models.xml。
///
/// 周期默认 **5 分钟**：客户端自身是 2 分钟（常量 eec = 12e4），但我们是服务端、
/// 多账号共享一份目录，不必跟得那么紧；折扣时段以小时计，5 分钟足够及时。
/// 设为 0 可关闭（只在前端手动刷新）。
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
    /// 拉一次。失败只记日志——目录是增强信息，失败不影响代理主流程。
    /// 用池里的活跃账号（目录是账号无关的，任意一个有效账号即可）。
    /// </summary>
    public async Task<bool> TryRefreshAsync(CancellationToken ct = default)
    {
        try
        {
            var acc = _auth.Database.GetActiveAccount();
            if (acc is null)
            {
                return false;
            }
            var creds = await _auth.GetCredsForAccountAsync(acc, ct);
            var snap = await _catalog.RefreshAsync(creds, ct);
            if (!snap.FromUpstream)
            {
                return false;
            }
            QoderConstants.MergeUpstreamCatalog(snap.Models, _log);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "定时同步模型目录失败");
            return false;
        }
    }
}

/// <summary>后台刷新周期。设为 0 表示关闭该项。</summary>
public sealed class RefreshIntervalOptions
{
    public const string SectionName = "Refresh";

    /// <summary>模型目录同步周期。0 = 关闭。</summary>
    public TimeSpan ModelCatalog { get; init; } = TimeSpan.FromMinutes(5);
}
