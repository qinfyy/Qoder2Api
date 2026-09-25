using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Qoder2Api.Services.Qoder;

namespace Qoder2Api.Endpoints;

/// <summary>
/// 排队等待期间向下游写 SSE 保活。只写以 ":" 开头的注释行——SSE 规范里注释会被
/// 解析器直接丢弃，对 OpenAI 兼容客户端完全透明，却能持续刷新读超时，避免动辄
/// 几分钟到一小时的静默把连接拖断。
/// 响应只在真正开始等待后才开启：短等待（不足一个保活间隔）完全不改变原有行为，
/// 仍能拿到正确的 HTTP 错误码。
/// </summary>
internal sealed class SseQueueKeepAlive : IQueueWaitObserver
{
    private readonly HttpContext _context;
    private readonly ILogger<SseQueueKeepAlive> _log;
    private readonly TimeSpan _interval;

    private QueueProgress _latest;
    private TimeSpan _waited;
    private int _pollCount;
    private bool _started;
    private bool _broken;

    public SseQueueKeepAlive(HttpContext context, ILogger<SseQueueKeepAlive> log, TimeSpan interval)
    {
        _context = context;
        _log = log;
        _interval = interval;
    }

    public Task OnPollAsync(QueueProgress progress, TimeSpan waited, int pollCount, CancellationToken ct)
    {
        // 只记录最新进度，写入交给 DelayAsync——避免「刚进队列就把响应定成 200」。
        _latest = progress;
        _waited = waited;
        _pollCount = pollCount;
        return Task.CompletedTask;
    }

    public async Task<bool> DelayAsync(TimeSpan duration, CancellationToken ct)
    {
        var remaining = duration;
        while (remaining > TimeSpan.Zero)
        {
            var slice = remaining < _interval ? remaining : _interval;
            try
            {
                await Task.Delay(slice, ct);
                remaining -= slice;
                if (remaining > TimeSpan.Zero)
                {
                    await EmitAsync(ct);
                }
            }
            catch (OperationCanceledException)
            {
                return false; // 客户端断连
            }
        }
        return true;
    }

    private async Task EmitAsync(CancellationToken ct)
    {
        if (_broken)
        {
            return;
        }
        try
        {
            if (!_started)
            {
                _context.Response.Headers.ContentType = "text/event-stream";
                _context.Response.Headers.CacheControl = "no-cache";
                _context.Response.Headers.Append("X-Accel-Buffering", "no");
                _context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
                _started = true;
            }
            await _context.Response.WriteAsync($": {Describe()}\n\n", Encoding.UTF8, ct);
            await _context.Response.Body.FlushAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw; // 交给 DelayAsync 判为取消
        }
        catch (Exception ex)
        {
            // 写不进去（响应已结束等）就停掉保活，让 ct 去终结这次请求。
            _broken = true;
            _log.LogDebug(ex, "排队保活写入失败，已停止保活");
        }
    }

    private string Describe()
    {
        var sb = new StringBuilder("queue");
        if (_latest.QueueType is { Length: > 0 } qt)
        {
            sb.Append(" type=").Append(OneLine(qt));
        }
        if (_latest.QueueCount is { } count)
        {
            sb.Append(" position=").Append(count);
        }
        if (_latest.WaitTimeSeconds is { } est)
        {
            sb.Append(" est=").Append(est).Append('s');
        }
        if (_latest.ServiceAvailable == false)
        {
            sb.Append(" service=unavailable");
        }
        sb.Append(" waited=").Append((int)_waited.TotalSeconds).Append('s');
        sb.Append(" polls=").Append(_pollCount);
        return sb.ToString();
    }

    // SSE 注释行不能含换行，否则会把一行拆成两行、污染下游解析。
    private static string OneLine(string s) => s.Replace('\r', ' ').Replace('\n', ' ');
}
