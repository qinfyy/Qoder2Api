using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;

namespace reg.Services.Qoder;

/// <summary>
/// 号池状态后台落盘 + 账号列表对齐。
/// 请求路径只改内存不碰库：Microsoft.Data.Sqlite 没有真正的异步 I/O，
/// 且默认是阻塞等待（30s）而非快速失败，放请求路径上会把线程池吃干。
/// </summary>
public sealed class PoolFlusher : BackgroundService
{
    private readonly QoderPool _pool;
    private readonly QoderAuthService _auth;
    private readonly ILogger<PoolFlusher> _log;

    public PoolFlusher(QoderPool pool, QoderAuthService auth, ILogger<PoolFlusher> log)
    {
        _pool = pool;
        _auth = auth;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 启动时先对齐一次账号列表（构造函数里已加载过持久化状态）。
        SyncAccounts();

        var interval = _pool.Options.FlushInterval;
        using var timer = new PeriodicTimer(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                SyncAccounts();
                _pool.FlushIfDirty();
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停机
        }
        finally
        {
            // 退出前做最后一次落盘，尽量不丢状态。
            _pool.FlushOnShutdown();
        }
    }

    private void SyncAccounts()
    {
        try
        {
            _pool.SyncAccounts(_auth.Database.GetAllAccounts());
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "账号列表同步失败，下轮重试");
        }
    }
}
