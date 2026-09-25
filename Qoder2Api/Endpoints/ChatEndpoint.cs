using Microsoft.Extensions.Options;
using Qoder2Api.Configuration;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Qoder2Api.Models;
using Qoder2Api.Services.Qoder;
using Qoder2Api.Services.Usage;

namespace Qoder2Api.Endpoints;

public static class ChatEndpoint
{
    private enum AttemptOutcome
    {
        /// <summary>已拿到可转发的内容，提交给客户端。</summary>
        Committed,

        /// <summary>本账号不可用，换一个号试试。</summary>
        Rotate,

        /// <summary>请求级错误或队列耗尽——换号也没用，直接把错误透传给下游。</summary>
        GiveUp,
    }

    public static void MapChatEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/chat/completions", HandleAsync);
    }

    private static async Task<IResult> HandleAsync(
        ChatCompletionRequest request,
        QoderProxyService proxy,
        QoderAuthService auth,
        QoderPool pool,
        QoderQueueClient queue,
        IOptions<QueueOptions> queueOptions,
        HttpContext context,
        ILoggerFactory logFactory,
        CancellationToken ct)
    {
        var log = logFactory.CreateLogger("reg.Endpoints.ChatEndpoint");
        var sw = Stopwatch.StartNew();
        var db = auth.Database;

        var (keyOk, key) = ResolveKey(auth, context);
        if (!keyOk)
        {
            db.LogUsage(new UsageRecord
            {
                Model = request.Model,
                HttpStatus = StatusCodes.Status401Unauthorized,
                Status = "error",
                ErrorMessage = "API Key 校验失败或未授权",
                LatencyMs = sw.ElapsedMilliseconds,
            });
            return OpenAiErrors.Unauthorized("API Key 无效或缺失。");
        }

        // 模型名必须能在 models.xml 里精确命中（key / displayName / alias），
        // 否则直接 404——不做任何猜测性兜底。
        if (QoderConstants.ResolveModel(request.Model) is null)
        {
            db.LogUsage(new UsageRecord
            {
                Model = request.Model,
                HttpStatus = StatusCodes.Status404NotFound,
                Status = "error",
                ErrorMessage = "模型不存在",
                LatencyMs = sw.ElapsedMilliseconds,
            });
            return OpenAiErrors.ModelNotFound(request.Model);
        }

        if (!pool.HasUsableAccount())
        {
            db.LogUsage(new UsageRecord
            {
                Model = request.Model,
                HttpStatus = StatusCodes.Status503ServiceUnavailable,
                Status = "error",
                ErrorMessage = "池内无可用账号",
                LatencyMs = sw.ElapsedMilliseconds,
            });
            return OpenAiErrors.NoAccount("池内暂无可用账号（全部在冷却/熔断中，或未连接任何账号）。");
        }

        string? boundAccountId = string.IsNullOrEmpty(key?.AccountId) ? null : key.AccountId;
        bool isStream = request.Stream == true;
        var qOpts = queueOptions.Value;
        var tried = new HashSet<string>(StringComparer.Ordinal);

        // 排队期间向下游发 SSE 保活。官方客户端用 model_queue_status 事件让 UI 显示"排队中"，
        // 本代理没有这条通道，只能用 SSE 注释行维持连接。仅流式请求适用——非流式必须一次性
        // 返回一个 JSON，中途发不了东西。
        IQueueWaitObserver? queueObserver = isStream && qOpts.KeepAliveInterval > TimeSpan.Zero
            ? new SseQueueKeepAlive(context, logFactory.CreateLogger<SseQueueKeepAlive>(), qOpts.KeepAliveInterval)
            : null;

        AccountLease? lease = null;
        QoderStreamSession? session = null;
        QoderUpstreamException? lastError = null;

        for (int rotate = 0; rotate < pool.Options.MaxRotate; rotate++)
        {
            lease = pool.TryAcquire(request.Model, boundAccountId, tried);
            if (lease is null)
            {
                break; // 没有更多可选账号
            }
            var held = lease;
            tried.Add(held.AccountId);

            var account = db.GetAccountById(held.AccountId);
            if (account is null)
            {
                held.Dispose();
                lease = null;
                continue; // 选中后被删除
            }

            CosyCreds creds;
            try
            {
                creds = await auth.GetCredsForAccountAsync(account, ct);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "取账号凭证失败 account={Account}", Short(held.AccountId));
                lastError = new QoderUpstreamException(QoderErrorKind.SessionDead, null, ex.Message, ex.Message);
                pool.ApplyError(held.AccountId, QoderErrorKind.SessionDead, ex.Message, request.Model);
                held.Dispose();
                lease = null;
                continue;
            }

            var outcome = await ServeOnAccountAsync(
                request, proxy, queue, qOpts, pool, held.AccountId, creds, log, queueObserver, ct,
                s => session = s,
                e => lastError = e);

            if (outcome == AttemptOutcome.Committed)
            {
                break; // session 已就位，去转发
            }

            session = null;
            held.Dispose();
            lease = null;

            if (outcome == AttemptOutcome.GiveUp)
            {
                break; // 请求级错误：换号也没用，直接把上游原文透传给下游
            }

            if (!await RotateBackoffAsync(rotate, ct))
            {
                break; // 客户端断连
            }
        }

        if (session is null || lease is null)
        {
            sw.Stop();
            db.LogUsage(new UsageRecord
            {
                Model = request.Model,
                LatencyMs = sw.ElapsedMilliseconds,
                HttpStatus = lastError is null ? StatusCodes.Status503ServiceUnavailable : 502,
                Status = "error",
                ErrorMessage = lastError?.Message ?? "重试后仍无可用账号",
            });

            if (context.Response.HasStarted)
            {
                // 排队保活已经把响应开成了 200 流，此时头已只读，只能把错误写进流里。
                var streamError = lastError ?? new QoderUpstreamException(
                    QoderErrorKind.Server, null, "", "重试后仍无可用账号");
                await TryWriteStreamErrorAsync(context, streamError, ct);
                return Results.Empty;
            }

            return lastError is not null ? OpenAiErrors.FromUpstream(lastError) : OpenAiErrors.NoAccount("重试后仍无可用账号，请稍后再试。");
        }

        return isStream ? await StreamCommittedAsync(context, session, lease, request, pool, db, sw, ct) : await NonStreamCommittedAsync(session, lease, request, pool, db, sw, ct);
    }

    private static async Task<AttemptOutcome> ServeOnAccountAsync(
        ChatCompletionRequest request,
        QoderProxyService proxy,
        QoderQueueClient queue,
        QueueOptions queueOptions,
        QoderPool pool,
        string accountId,
        CosyCreds creds,
        ILogger log,
        IQueueWaitObserver? queueObserver,
        CancellationToken ct,
        Action<QoderStreamSession> onCommitted,
        Action<QoderUpstreamException> onError)
    {
        QoderRequestIds? ids = null;
        int queueRecoveries = 0;
        var queuedSoFar = TimeSpan.Zero;   // 累计排队时长，MaxWait 是跨轮次的总预算

        while (true)
        {
            if (ct.IsCancellationRequested)
            {
                return AttemptOutcome.GiveUp;
            }

            QoderStreamSession? session = null;
            try
            {
                session = await proxy.OpenAsync(request, creds, accountId, ids, ct);
                var prime = await session.PrimeAsync(ct);

                if (prime == PrimeResult.Committed)
                {
                    onCommitted(session);
                    return AttemptOutcome.Committed;
                }

                var error = session.Error
                            ?? new QoderUpstreamException(QoderErrorKind.Server, null, "", "上游无响应");
                onError(error);

                if (error.Kind == QoderErrorKind.ModelQueued)
                {
                    if (!queueOptions.Enabled)
                    {
                        log.LogWarning("收到排队响应但排队已禁用，改为换号");
                    }
                    else if (queueRecoveries >= queueOptions.MaxRecoveries)
                    {
                        log.LogWarning("排队恢复次数已达上限 {Max}，放弃 account={Account}",
                            queueOptions.MaxRecoveries, Short(accountId));
                    }
                    else
                    {
                        var queueCtx = QoderQueueParser.Parse(error.RawBody);
                        var modelKey = session.ModelKey;
                        var requestSetId = session.Ids.RequestSetId;
                        ids = session.Ids; // 复用同一组 ID —— 排队记录按 RequestSetId 关联

                        await session.DisposeAsync();
                        session = null;

                        log.LogInformation(
                            "模型排队，等待中 account={Account} requestSetId={RequestSetId} 队列类型={QueueType} 队列长度={QueueCount} 建议间隔={RetryAfter}s 第 {Attempt} 次",
                            Short(accountId), Short(requestSetId), queueCtx?.QueueType ?? "-",
                            queueCtx?.QueueCount?.ToString() ?? "-", queueCtx?.RetryAfterSeconds?.ToString() ?? "-",
                            queueRecoveries + 1);

                        var wait = await queue.WaitUntilReadyAsync(
                            requestSetId, modelKey, queueCtx?.QueueType, queueCtx, creds, queuedSoFar, ct,
                            queueObserver);
                        queuedSoFar += TimeSpan.FromMilliseconds(wait.WaitedMs);

                        switch (wait.Outcome)
                        {
                            case QueueWaitOutcome.Ready:
                                queueRecoveries++;
                                log.LogInformation("排队结束，在原账号重试 account={Account} 等待 {WaitedMs}ms 轮询 {PollCount} 次",
                                    Short(accountId), wait.WaitedMs, wait.PollCount);
                                continue; // 同一账号重试，不换号

                            case QueueWaitOutcome.Cancelled:
                                return AttemptOutcome.GiveUp;

                            default:
                                log.LogWarning("排队未成功（{Outcome}）: {Detail} —— 改为换号",
                                    wait.Outcome, wait.Detail ?? "-");
                                break;
                        }
                    }
                }

                // 其他错误：按策略惩罚账号 ----
                pool.ApplyError(accountId, error.Kind, error.RawBody, request.Model);
                if (session is not null)
                {
                    await session.DisposeAsync();
                }

                // 请求级错误（内容拦截/上下文超长）换号也没用，直接把上游原文透传给下游。
                return prime == PrimeResult.FatalError ? AttemptOutcome.GiveUp : AttemptOutcome.Rotate;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                if (session is not null)
                {
                    await session.DisposeAsync();
                }
                return AttemptOutcome.GiveUp; // 客户端断连，不惩罚账号
            }
            catch (QoderUpstreamException ex)
            {
                onError(ex);
                if (session is not null)
                {
                    await session.DisposeAsync();
                }
                pool.ApplyError(accountId, ex.Kind, ex.RawBody, request.Model);
                return ex.Kind.IsRetryable() ? AttemptOutcome.Rotate : AttemptOutcome.GiveUp;
            }
            catch (Exception ex)
            {
                // 兜底分支：任何没被上面分流的异常都归为 Transport。
                // 必须记日志——否则真实原因（比如请求体构造/序列化失败）会被
                // upstream_unreachable 这个笼统的错误码盖住，排查时只剩一句 ex.Message。
                log.LogError(ex, "请求上游时发生未预期异常 account={Account} model={Model}",
                    Short(accountId), request.Model);
                onError(new QoderUpstreamException(QoderErrorKind.Transport, null, "", ex.Message));
                if (session is not null)
                {
                    await session.DisposeAsync();
                }
                pool.ApplyError(accountId, QoderErrorKind.Transport, null, request.Model);
                return AttemptOutcome.Rotate;
            }
        }
    }

    private static async Task<IResult> StreamCommittedAsync(
        HttpContext context,
        QoderStreamSession session,
        AccountLease lease,
        ChatCompletionRequest request,
        QoderPool pool,
        Services.Database.SqliteDbService db,
        Stopwatch sw,
        CancellationToken ct)
    {
        bool includeUsage = request.IncludeUsage;
        var usage = session.Usage;
        string? streamError = null;

        try
        {
            // 响应可能已被排队保活提前开成流，此时头已只读。
            if (!context.Response.HasStarted)
            {
                context.Response.Headers.ContentType = "text/event-stream";
                context.Response.Headers.CacheControl = "no-cache";
                context.Response.Headers.Append("X-Accel-Buffering", "no");
            }

            await foreach (var ev in session.ReadEventsAsync(ct))
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }
                switch (ev.Kind)
                {
                    case StreamEventKind.Chunk when ev.Json is not null:
                        await WriteSseAsync(context, ev.Json, ct);
                        break;

                    case StreamEventKind.Usage when ev.Json is not null:
                        if (includeUsage)
                        {
                            await WriteSseAsync(context, ev.Json, ct);
                        }
                        break;

                    case StreamEventKind.Finish:
                        break; // 上游私有事件，不透传

                    case StreamEventKind.Done:
                        await WriteRawAsync(context, "data: [DONE]\n\n", ct);
                        goto done;
                }
            }
            if (!ct.IsCancellationRequested)
            {
                await WriteRawAsync(context, "data: [DONE]\n\n", ct);
            }
        done:;
            pool.NoteSuccess(lease.AccountId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            streamError = "客户端断开连接";
        }
        catch (Exception ex)
        {
            streamError = ex.Message;
            pool.ApplyError(lease.AccountId, ClassifyException(ex), ExtractBody(ex), request.Model);
            await TryWriteStreamErrorAsync(context, ex, ct);
        }
        finally
        {
            sw.Stop();
            await session.DisposeAsync();
            lease.Dispose();
            LogStreamUsage(db, request, usage, session, sw, streamError, lease.AccountId);
        }

        return Results.Empty;
    }

    private static async Task<IResult> NonStreamCommittedAsync(
        QoderStreamSession session,
        AccountLease lease,
        ChatCompletionRequest request,
        QoderPool pool,
        Services.Database.SqliteDbService db,
        Stopwatch sw,
        CancellationToken ct)
    {
        try
        {
            string json = await AggregateAsync(session, request, ct);
            sw.Stop();
            pool.NoteSuccess(lease.AccountId);

            var usage = session.Usage.Build();
            db.LogUsage(new UsageRecord
            {
                Model = request.Model,
                PromptTokens = usage.PromptTokens ?? 0,
                CompletionTokens = usage.CompletionTokens ?? 0,
                TotalTokens = usage.TotalTokens ?? 0,
                ReasoningTokens = usage.CompletionTokensDetails?.ReasoningTokens ?? 0,
                CachedTokens = usage.PromptTokensDetails?.CachedTokens ?? 0,
                Credits = usage.Credits ?? 0,
                UsageSource = usage.Source,
                AccountUid = lease.AccountId,
                FirstTokenMs = session.FirstTokenMs,
                LatencyMs = sw.ElapsedMilliseconds,
                HttpStatus = 200,
                Status = "success",
            });

            return Results.Content(json, "application/json");
        }
        catch (Exception ex)
        {
            sw.Stop();
            pool.ApplyError(lease.AccountId, ClassifyException(ex), ExtractBody(ex), request.Model);
            db.LogUsage(new UsageRecord
            {
                Model = request.Model,
                LatencyMs = sw.ElapsedMilliseconds,
                HttpStatus = 502,
                Status = "error",
                ErrorMessage = ex.Message,
                AccountUid = lease.AccountId,
            });
            return ex is QoderUpstreamException qe
                ? OpenAiErrors.FromUpstream(qe)
                : OpenAiErrors.Json(502, ex.Message, "api_error", "upstream_error");
        }
        finally
        {
            await session.DisposeAsync();
            lease.Dispose();
        }
    }

    private static async Task<string> AggregateAsync(QoderStreamSession session, ChatCompletionRequest request, CancellationToken ct)
    {
        string id = "chatcmpl-" + Guid.NewGuid().ToString("N");
        long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var contentSb = new StringBuilder();
        var reasoningSb = new StringBuilder();
        string finishReason = "stop";
        // 响应模型原样透传上游；上游一直没给才退回请求模型。
        string model = request.Model;

        await foreach (var ev in session.ReadEventsAsync(ct))
        {
            if (ev.Kind == StreamEventKind.Done)
            {
                break;
            }
            if (ev.Kind != StreamEventKind.Chunk || ev.Json is null)
            {
                continue;
            }
            try
            {
                using var doc = JsonDocument.Parse(ev.Json);
                var root = doc.RootElement;
                if (root.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
                {
                    id = idProp.GetString() ?? id;
                }
                if (root.TryGetProperty("model", out var modelProp) && modelProp.ValueKind == JsonValueKind.String)
                {
                    model = modelProp.GetString() ?? model;
                }
                if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }
                foreach (var choice in choices.EnumerateArray())
                {
                    if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                    {
                        finishReason = fr.GetString() ?? finishReason;
                    }
                    if (!choice.TryGetProperty("delta", out var delta))
                    {
                        continue;
                    }
                    if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                    {
                        contentSb.Append(c.GetString());
                    }
                    if (delta.TryGetProperty("reasoning_content", out var r) && r.ValueKind == JsonValueKind.String)
                    {
                        reasoningSb.Append(r.GetString());
                    }
                }
            }
            catch (JsonException)
            {
                // 跳过无法解析的 chunk
            }
        }

        var usage = session.Usage.Build();
        var responseObj = new
        {
            id,
            @object = "chat.completion",
            created,
            model,
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new
                    {
                        role = "assistant",
                        content = contentSb.ToString(),
                        reasoning_content = reasoningSb.Length > 0 ? reasoningSb.ToString() : null,
                    },
                    finish_reason = finishReason,
                },
            },
            usage,
        };
        return JsonSerializer.Serialize(responseObj, JsonOpts);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static (bool Ok, ApiKeyRecord? Key) ResolveKey(QoderAuthService auth, HttpContext context)
    {
        if (!auth.RequireApiKey)
        {
            return (true, null);
        }

        string? token = null;
        string? authHeader = context.Request.Headers.Authorization;
        if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            token = authHeader[7..].Trim();
        }
        else if (context.Request.Headers.TryGetValue("x-api-key", out var xKey))
        {
            token = xKey.ToString().Trim();
        }

        if (string.IsNullOrEmpty(token))
        {
            return (false, null);
        }

        var key = auth.Database.FindActiveKey(token);
        return (key is not null, key);
    }

    private static async Task<bool> RotateBackoffAsync(int attempt, CancellationToken ct)
    {
        double baseMs = 500 * Math.Pow(2, attempt);
        if (baseMs > 8000)
        {
            baseMs = 8000;
        }
        double jitter = 1.0 + (Random.Shared.NextDouble() - 0.5) * 0.5;
        int delayMs = (int)(baseMs * jitter);
        try
        {
            await Task.Delay(delayMs, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task WriteSseAsync(HttpContext context, string json, CancellationToken ct)
    {
        await WriteRawAsync(context, $"data: {json}\n\n", ct);
    }

    private static async Task WriteRawAsync(HttpContext context, string text, CancellationToken ct)
    {
        await context.Response.WriteAsync(text, Encoding.UTF8, ct);
        await context.Response.Body.FlushAsync(ct);
    }

    private static async Task TryWriteStreamErrorAsync(HttpContext context, Exception ex, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || !context.Response.HasStarted)
        {
            return;
        }
        try
        {
            var errObj = new
            {
                error = new
                {
                    message = ex.Message,
                    type = "qoder_proxy_error",
                    code = ex is QoderUpstreamException qe ? qe.Kind.ToCode() : "upstream_error",
                },
            };
            await WriteRawAsync(context, $"data: {JsonSerializer.Serialize(errObj, JsonOpts)}\n\n", CancellationToken.None);
            await WriteRawAsync(context, "data: [DONE]\n\n", CancellationToken.None);
        }
        catch
        {
            // 响应已不可写，忽略
        }
    }

    private static void LogStreamUsage(
        Services.Database.SqliteDbService db,
        ChatCompletionRequest request,
        UsageCollector usage,
        QoderStreamSession session,
        Stopwatch sw,
        string? streamError,
        string accountId)
    {
        var built = usage.Build();
        bool cancelled = streamError == "客户端断开连接";
        db.LogUsage(new UsageRecord
        {
            Model = request.Model,
            PromptTokens = built.PromptTokens ?? 0,
            CompletionTokens = built.CompletionTokens ?? 0,
            TotalTokens = built.TotalTokens ?? 0,
            ReasoningTokens = built.CompletionTokensDetails?.ReasoningTokens ?? 0,
            CachedTokens = built.PromptTokensDetails?.CachedTokens ?? 0,
            Credits = built.Credits ?? 0,
            UsageSource = built.Source,
            AccountUid = accountId,
            FirstTokenMs = session.FirstTokenMs,
            LatencyMs = sw.ElapsedMilliseconds,
            HttpStatus = cancelled ? 499 : streamError is null ? 200 : 502,
            Status = cancelled ? "cancelled" : streamError is null ? "success" : "error",
            ErrorMessage = streamError,
        });
    }

    private static QoderErrorKind ClassifyException(Exception ex) =>
        ex is QoderUpstreamException qe ? qe.Kind : QoderErrorKind.Client;

    private static string? ExtractBody(Exception ex) =>
        ex is QoderUpstreamException qe ? qe.RawBody : null;

    private static string Short(string s) => s.Length > 8 ? s[..8] : s;
}
