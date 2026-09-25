using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Qoder2Api.Configuration;

namespace Qoder2Api.Services.Qoder;

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

    public async Task<bool> TryRefreshAsync(CancellationToken ct = default)
        => (await SyncAsync(allowAdd: true, ct)).Success;

    public Task<CatalogSyncOutcome> SyncManualAsync(CancellationToken ct = default)
        => SyncAsync(allowAdd: true, ct);

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
