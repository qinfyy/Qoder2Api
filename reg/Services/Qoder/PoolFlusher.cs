using Microsoft.Extensions.Hosting;

namespace reg.Services.Qoder;

/// <summary>
/// 号池状态的后台落盘器 + 账号列表对齐器。
///
/// **为什么必须有它**：号池的请求路径只改内存，不碰数据库。原因是
/// Microsoft.Data.Sqlite **没有真正的异步 I/O**（其 async 方法内部就是同步执行的），
/// 在请求路径上写库会直接阻塞 Kestrel 的线程；而 SQLite 的默认 busy 行为是
/// **阻塞等待**（默认 30 秒）而非快速失败，并发写会把线程池吃干，
/// 连带拖垮 Blazor Server 的渲染调度（表现为整个管理页"点不动"）。
///
/// 把落盘收敛到这一个后台循环里，阻塞就只发生在这一条线程上。
///
/// 同时它也负责周期性地把数据库里的账号列表同步进池——这样用户在管理页新增/
/// 删除/停用账号后，池能在一个周期内感知到，而不需要每条写路径都去通知池。
/// </summary>
public sealed class PoolFlusher : BackgroundService
{
    private readonly QoderPool _pool;
    private readonly QoderAuthService _auth;

    public PoolFlusher(QoderPool pool, QoderAuthService auth)
    {
        _pool = pool;
        _auth = auth;
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
            Console.WriteLine($"[PoolFlusher] 账号列表同步失败（下轮重试）: {ex.Message}");
        }
    }
}
