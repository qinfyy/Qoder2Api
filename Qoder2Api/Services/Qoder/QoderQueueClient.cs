using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Qoder2Api.Configuration;
using System.Text.Json;
using Qoder2Api.Models;

namespace Qoder2Api.Services.Qoder;

public enum QueueWaitOutcome
{
    /// <summary>排到了，可以重试推理。</summary>
    Ready,

    /// <summary>等待超时。</summary>
    Timeout,

    /// <summary>轮询端点不可用（404）。</summary>
    EndpointUnavailable,

    /// <summary>连续轮询失败过多。</summary>
    PollFailed,

    /// <summary>客户端断连。</summary>
    Cancelled,
}

public readonly record struct QueueWaitResult(
    QueueWaitOutcome Outcome,
    long WaitedMs,
    int PollCount,
    string? Detail = null);

public sealed class QoderQueueClient
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly QueueOptions _opt;
    private readonly ILogger<QoderQueueClient> _log;

    public QoderQueueClient(IHttpClientFactory httpFactory, IOptions<QueueOptions> options, ILogger<QoderQueueClient> log)
    {
        _httpFactory = httpFactory;
        _opt = options.Value;
        _log = log;
    }
    public async Task<QueueWaitResult> WaitUntilReadyAsync(
        string requestSetId,
        string modelKey,
        string? queueType,
        QoderQueueContext? initial,
        CosyCreds creds,
        TimeSpan alreadyWaited,
        CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow;
        // MaxWait 是跨恢复轮次的累计预算（对齐官方客户端 n = maxWaitMs - waitMs），
        // 不是每轮各给一份——否则 10 次恢复 × 1 小时能拖到 10 小时。
        var budget = _opt.MaxWait - alreadyWaited;
        if (budget <= TimeSpan.Zero)
        {
            return new QueueWaitResult(QueueWaitOutcome.Timeout, 0, 0,
                $"排队预算已用尽（预算 {_opt.MaxWait.TotalSeconds:F0}s，已等 {alreadyWaited.TotalSeconds:F0}s）");
        }
        var deadline = startedAt + budget;
        var current = initial ?? new QoderQueueContext();
        int pollCount = 0;
        int consecutiveFailures = 0;
        bool polledOnce = false;

        while (true)
        {
            if (ct.IsCancellationRequested)
            {
                return new QueueWaitResult(QueueWaitOutcome.Cancelled, Elapsed(startedAt), pollCount);
            }

            var now = DateTimeOffset.UtcNow;
            if (now >= deadline)
            {
                return new QueueWaitResult(QueueWaitOutcome.Timeout, Elapsed(startedAt), pollCount, $"排队等待超时（预算 {budget.TotalSeconds:F0}s）");
            }

            // 首次不睡：立刻查一次当前队列状态。
            if (polledOnce)
            {
                var interval = ResolveInterval(current, _opt);
                var remaining = deadline - DateTimeOffset.UtcNow;
                var sleep = interval < remaining ? interval : remaining;
                if (sleep > TimeSpan.Zero && !await SleepAsync(sleep, ct))
                {
                    return new QueueWaitResult(QueueWaitOutcome.Cancelled, Elapsed(startedAt), pollCount);
                }
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return new QueueWaitResult(QueueWaitOutcome.Timeout, Elapsed(startedAt), pollCount, $"排队等待超时（预算 {budget.TotalSeconds:F0}s）");
            }

            polledOnce = true;
            pollCount++;

            QueuePollOutcome poll;
            try
            {
                poll = await PollOnceAsync(requestSetId, modelKey, current.QueueType ?? queueType,
                    creds, _opt.PollRequestTimeout, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return new QueueWaitResult(QueueWaitOutcome.Cancelled, Elapsed(startedAt), pollCount);
            }
            catch (Exception ex)
            {
                poll = new QueuePollOutcome(QueuePollStatus.Failed, null, ex.Message);
            }

            switch (poll.Status)
            {
                case QueuePollStatus.EndpointUnavailable:
                    return new QueueWaitResult(QueueWaitOutcome.EndpointUnavailable, Elapsed(startedAt),
                        pollCount, poll.Detail);

                case QueuePollStatus.Cancelled:
                    return new QueueWaitResult(QueueWaitOutcome.Cancelled, Elapsed(startedAt), pollCount);

                case QueuePollStatus.Failed:
                    consecutiveFailures++;
                    if (consecutiveFailures >= _opt.MaxConsecutivePollFailures)
                    {
                        return new QueueWaitResult(QueueWaitOutcome.PollFailed, Elapsed(startedAt),
                            pollCount, poll.Detail);
                    }
                    _log.LogWarning("排队状态查询失败 {Failures}/{Max}，将重试: {Detail}",
                        consecutiveFailures, _opt.MaxConsecutivePollFailures, poll.Detail);
                    // 与客户端一致：失败后清掉 retryAfterSeconds，下轮用默认间隔。
                    current.RetryAfterSeconds = null;
                    continue;
            }

            consecutiveFailures = 0;
            current = poll.Queue ?? current;

            if (current.IsQueued == false)
            {
                // 排到了。服务端可能还给了一个 retryAfterSeconds，等完再发起推理。
                var extra = ResolveInterval(current, _opt);
                var remaining = deadline - DateTimeOffset.UtcNow;
                var sleep = extra < remaining ? extra : remaining;
                if (sleep > TimeSpan.Zero && !await SleepAsync(sleep, ct))
                {
                    return new QueueWaitResult(QueueWaitOutcome.Cancelled, Elapsed(startedAt), pollCount);
                }
                return new QueueWaitResult(QueueWaitOutcome.Ready, Elapsed(startedAt), pollCount);
            }

            if (current.ServiceAvailable == false)
            {
                // 服务不可用但仍在排队：客户端的行为是**继续轮询**，不是放弃。
                _log.LogInformation("服务暂不可用，继续排队 requestSetId={RequestSetId} 已查 {PollCount} 次 队列长度={QueueCount}",
                    Short(requestSetId), pollCount, current.QueueCount?.ToString() ?? "-");
                continue;
            }

            if (current.IsQueued == true)
            {
                continue; // 正常排队中
            }

            // 既没排到、也没说在排队、服务也没说不可用——状态无法解释，视为失败。
            return new QueueWaitResult(QueueWaitOutcome.PollFailed, Elapsed(startedAt), pollCount,
                "排队状态无法解释（既非排队中也非就绪）");
        }
    }

    private enum QueuePollStatus { Ok, EndpointUnavailable, Failed, Cancelled }

    private readonly record struct QueuePollOutcome(
        QueuePollStatus Status, QoderQueueContext? Queue, string? Detail);

    private async Task<QueuePollOutcome> PollOnceAsync(
        string requestSetId, string modelKey, string? queueType,
        CosyCreds creds, TimeSpan timeout, CancellationToken ct)
    {
        var url = BuildQueueStatusUrl(requestSetId, modelKey, queueType);

        // GET 无请求体：COSY 签名对空 body 同样成立（签名串里的 body 段为空）。
        var headers = CosySigner.BuildCosyHeaders([], url, creds);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var (k, v) in headers)
        {
            req.Headers.TryAddWithoutValidation(k, v);
        }
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        req.Headers.TryAddWithoutValidation("X-Request-ID", requestSetId);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        var http = _httpFactory.CreateClient(QoderHttp.ClientName);
        try
        {
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, timeoutCts.Token);

            if ((int)resp.StatusCode == 404)
            {
                var notFoundBody = await resp.Content.ReadAsStringAsync(timeoutCts.Token);
                return new QueuePollOutcome(QueuePollStatus.EndpointUnavailable, null,
                    $"排队状态端点返回 404；url={url}；响应体={Truncate(notFoundBody, 300)}");
            }
            if (!resp.IsSuccessStatusCode)
            {
                var errBody = await resp.Content.ReadAsStringAsync(timeoutCts.Token);
                return new QueuePollOutcome(QueuePollStatus.Failed, null,
                    $"HTTP {(int)resp.StatusCode}；响应体={Truncate(errBody, 300)}");
            }

            var body = await resp.Content.ReadAsStringAsync(timeoutCts.Token);
            var queue = QoderQueueParser.Parse(body);
            if (queue is null)
            {
                return new QueuePollOutcome(QueuePollStatus.Failed, null,
                    $"排队状态响应无法解析: {Truncate(body, 200)}");
            }
            return new QueuePollOutcome(QueuePollStatus.Ok, queue, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new QueuePollOutcome(QueuePollStatus.Cancelled, null, null);
        }
        catch (OperationCanceledException)
        {
            return new QueuePollOutcome(QueuePollStatus.Failed, null, $"轮询请求超时（{timeout.TotalSeconds:F0}s）");
        }
        catch (HttpRequestException ex)
        {
            return new QueuePollOutcome(QueuePollStatus.Failed, null, ex.Message);
        }
    }

    public static string BuildQueueStatusUrl(string requestSetId, string modelKey, string? queueType)
    {
        var query = $"requestSetId={Uri.EscapeDataString(requestSetId)}&modelKey={Uri.EscapeDataString(modelKey)}";
        if (!string.IsNullOrWhiteSpace(queueType))
        {
            query += $"&queueType={Uri.EscapeDataString(queueType)}";
        }
        return $"{QoderConstants.QueueStatusURL}?{query}";
    }

    private static TimeSpan ResolveInterval(QoderQueueContext? ctx, QueueOptions opt)
    {
        var ms = ctx?.RetryAfterSeconds is > 0
            ? TimeSpan.FromSeconds(ctx.RetryAfterSeconds.Value)
            : opt.DefaultPollInterval;
        if (ms < opt.MinPollInterval) ms = opt.MinPollInterval;
        if (ms > opt.MaxPollInterval) ms = opt.MaxPollInterval;
        return ms;
    }

    private static async Task<bool> SleepAsync(TimeSpan d, CancellationToken ct)
    {
        try
        {
            await Task.Delay(d, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static long Elapsed(DateTimeOffset start) =>
        (long)(DateTimeOffset.UtcNow - start).TotalMilliseconds;

    private static string Short(string s) => s.Length > 8 ? s[..8] : s;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
