using System.Diagnostics;
using System.Text;
using System.Text.Json;
using reg.Models;
using reg.Services.Qoder;
using reg.Services.Usage;

namespace reg.Endpoints;

/// <summary>
/// <c>/v1/chat/completions</c>：OpenAI 兼容的对话端点。
///
/// **核心约束**：流式响应一旦写出字节就无法再换号——客户端会收到两段拼接的内容，
/// 而且无法察觉。所以轮换必须发生在**写出任何响应字节之前**：
/// 先选号 → 发起上游连接 → 探测首包 → 确认首包不是错误 → 才提交给客户端。
/// 这一步把绝大多数账号级故障（401/403/429/模型无权限，都发生在首包）变成了可安全重试的。
/// </summary>
public static class ChatEndpoint
{
    public static void MapChatEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/chat/completions", HandleAsync);
    }

    private static async Task<IResult> HandleAsync(
        ChatCompletionRequest request,
        QoderProxyService proxy,
        QoderAuthService auth,
        QoderPool pool,
        HttpContext context,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var db = auth.Database;

        // ---- 1. 鉴权。需要拿到 Key 记录本身，才能实现「API Key 粘性绑定账号」。 ----
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

        // ---- 2. 池准入闸门。 ----
        // 不能用 auth.IsAuthenticated：它背后是「首选账号是否 active」，
        // 首选账号被停用时，即使池里还有 5 个健康账号也会被拦掉。
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
        var tried = new HashSet<string>(StringComparer.Ordinal);

        // ---- 3. 轮换循环：选号 → 连上游 → 探测首包 ----
        AccountLease? lease = null;
        QoderStreamSession? session = null;
        QoderUpstreamException? lastError = null;

        for (int attempt = 0; attempt < pool.Options.MaxRotate; attempt++)
        {
            lease = pool.TryAcquire(request.Model, boundAccountId, tried);
            if (lease is null)
            {
                break; // 没有更多可选账号
            }
            // 用非空局部量贯穿本轮尝试，避免在 catch 里反复判空（编译器也据此消除可空警告）。
            var held = lease;
            tried.Add(held.AccountId);

            var account = db.GetAccountById(held.AccountId);
            if (account is null)
            {
                // 账号在选中后被删除：释放并继续。
                held.Dispose();
                lease = null;
                continue;
            }

            try
            {
                var creds = await auth.GetCredsForAccountAsync(account, ct);
                session = await proxy.OpenAsync(request, creds, held.AccountId, ct);

                var prime = await session.PrimeAsync(ct);
                if (prime == PrimeResult.Committed)
                {
                    break; // ★ 提交点：从这里开始绑定这个账号，不再换号
                }

                // 首包就是错误：分类 → 惩罚 → 换号（此时**尚未写出任何响应字节**）
                lastError = session.Error;
                if (lastError is not null)
                {
                    pool.ApplyError(held.AccountId, lastError.Kind, lastError.RawBody, request.Model);
                }
                await session.DisposeAsync();
                session = null;
                held.Dispose();
                lease = null;

                if (prime == PrimeResult.FatalError)
                {
                    break; // 请求级错误（内容拦截/上下文超长）：换号也没用，直接返回原文
                }
                if (!await RotateBackoffAsync(attempt, ct))
                {
                    break; // 客户端断连
                }
            }
            catch (QoderUpstreamException ex)
            {
                lastError = ex;
                pool.ApplyError(held.AccountId, ex.Kind, ex.RawBody, request.Model);
                if (session is not null)
                {
                    await session.DisposeAsync();
                    session = null;
                }
                held.Dispose();
                lease = null;

                if (!ex.Kind.IsRetryable())
                {
                    break;
                }
                if (!await RotateBackoffAsync(attempt, ct))
                {
                    break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 客户端断连：不是账号的错，不记 NoteError、不惩罚账号。
                held.Dispose();
                lease = null;
                if (session is not null)
                {
                    await session.DisposeAsync();
                    session = null;
                }
                sw.Stop();
                db.LogUsage(new UsageRecord
                {
                    Model = request.Model,
                    LatencyMs = sw.ElapsedMilliseconds,
                    HttpStatus = 499,
                    Status = "cancelled",
                    ErrorMessage = "客户端断开连接",
                });
                return Results.Empty;
            }
        }

        // ---- 4. 全部尝试失败 ----
        if (session is null || lease is null)
        {
            sw.Stop();
            if (lastError is not null)
            {
                db.LogUsage(new UsageRecord
                {
                    Model = request.Model,
                    LatencyMs = sw.ElapsedMilliseconds,
                    HttpStatus = 502,
                    Status = "error",
                    ErrorMessage = lastError.Message,
                });
                return OpenAiErrors.FromUpstream(lastError);
            }
            db.LogUsage(new UsageRecord
            {
                Model = request.Model,
                LatencyMs = sw.ElapsedMilliseconds,
                HttpStatus = StatusCodes.Status503ServiceUnavailable,
                Status = "error",
                ErrorMessage = "重试后仍无可用账号",
            });
            return OpenAiErrors.NoAccount("重试后仍无可用账号，请稍后再试。");
        }

        // ---- 5. 已提交：流式或非流式 ----
        return isStream
            ? await StreamCommittedAsync(context, session, lease, request, pool, db, sw, ct)
            : await NonStreamCommittedAsync(session, lease, request, pool, db, sw, ct);
    }

    /// <summary>已提交后的流式转发。</summary>
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
            // 此刻才设置 SSE 响应头并开始写——之前任何一步失败都还没污染响应。
            context.Response.Headers.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers.Append("X-Accel-Buffering", "no");

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
                        // OpenAI 契约：只有客户端显式请求了 include_usage 才发这个 chunk。
                        // 但**无论发不发，usage 都已经被 session 采集**——统计不依赖转发。
                        if (includeUsage)
                        {
                            await WriteSseAsync(context, ev.Json, ct);
                        }
                        break;

                    case StreamEventKind.Finish:
                        // 上游私有事件，不透传（OpenAI 客户端不认）。
                        break;

                    case StreamEventKind.Done:
                        // include_usage 为真但上游没给 usage chunk 时，不发伪造的 0——
                        // 客户端对缺失 usage 是容忍的，伪造反而会让下游统计出错。
                        await WriteRawAsync(context, "data: [DONE]\n\n", ct);
                        goto done;
                }
            }
            // 上游没发 [DONE] 就断了：补一个，否则客户端会一直转圈等它。
            if (!ct.IsCancellationRequested)
            {
                await WriteRawAsync(context, "data: [DONE]\n\n", ct);
            }
        done:;
            pool.NoteSuccess(lease.AccountId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 客户端断连：不惩罚账号、不写错误（响应已断，写也没人收）。
            streamError = "客户端断开连接";
        }
        catch (Exception ex)
        {
            streamError = ex.Message;
            pool.ApplyError(lease.AccountId, ClassifyException(ex), ExtractBody(ex), request.Model);
            // 已提交 → 只能把错误塞进 SSE 流里降级，**绝不能换号重试**
            // （客户端会收到两段拼接的内容且无法察觉）。
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

    /// <summary>已提交后的非流式聚合。</summary>
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

    /// <summary>把流式 chunk 聚合成一个完整的 chat.completion 响应体。</summary>
    private static async Task<string> AggregateAsync(QoderStreamSession session, ChatCompletionRequest request, CancellationToken ct)
    {
        string id = "chatcmpl-" + Guid.NewGuid().ToString("N");
        long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var contentSb = new StringBuilder();
        var reasoningSb = new StringBuilder();
        string finishReason = "stop";

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
            model = request.Model,
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

    // ---------------------------------------------------------------------
    // 辅助
    // ---------------------------------------------------------------------

    /// <summary>
    /// 校验 API Key。返回 (是否放行, Key 记录)。
    /// 不启用校验时放行且无绑定；启用时 Key 记录用于取 AccountId 做粘性绑定。
    /// </summary>
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

    /// <summary>轮换退避：500ms·2^i，封顶 8s，±25% 抖动。返回 false 表示客户端已断连。</summary>
    private static async Task<bool> RotateBackoffAsync(int attempt, CancellationToken ct)
    {
        double baseMs = 500 * Math.Pow(2, attempt);
        if (baseMs > 8000)
        {
            baseMs = 8000;
        }
        double jitter = 1.0 + (Random.Shared.NextDouble() - 0.5) * 0.5; // ±25%
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

    /// <summary>
    /// 已提交后的错误降级：写一条 OpenAI 风格的 error 对象 + [DONE]。
    ///
    /// 三点讲究：
    ///   1. 绝不换号重试（客户端会收到两段拼接内容）；
    ///   2. 必须补 [DONE]，否则 NextChat/Cherry Studio 会一直转圈；
    ///   3. 写之前判 ct——客户端已断连时再写会抛异常，把原始错误吞掉，
    ///      日志里就只剩"断连"、看不到真实原因了。
    /// </summary>
    private static async Task TryWriteStreamErrorAsync(HttpContext context, Exception ex, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || context.Response.HasStarted == false)
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
}
