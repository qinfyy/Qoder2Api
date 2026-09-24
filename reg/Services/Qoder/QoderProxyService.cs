using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using reg.Models;
using reg.Services.Usage;

namespace reg.Services.Qoder;

public enum StreamEventKind
{
    Chunk,
    Usage,
    Done,
    Finish,
}

public readonly record struct StreamEvent(StreamEventKind Kind, string? Json = null, long FirstTokenMs = 0);

public sealed record QoderRequestIds(string RequestId, string RequestSetId, string ChatRecordId, string SessionId)
{
    public static QoderRequestIds New()
    {
        string recordId = Guid.NewGuid().ToString("N")[..16];
        return new QoderRequestIds(
            RequestId: Guid.NewGuid().ToString(),
            RequestSetId: recordId,
            ChatRecordId: recordId,
            SessionId: Guid.NewGuid().ToString("N")[..16]);
    }
}

public enum PrimeResult
{
    Committed,
    RetryableError,
    FatalError,
}

public sealed class QoderStreamSession : IAsyncDisposable
{
    private readonly HttpResponseMessage _resp;
    private readonly StreamReader _reader;
    private readonly CancellationTokenSource _linked;
    private readonly CancellationToken _outer;
    private readonly string _requestedModel;
    private readonly string _accountId;

    private string? _primedJson;

    public QoderUpstreamException? Error { get; private set; }

    public UsageCollector Usage { get; }

    public long FirstTokenMs { get; private set; }

    public QoderRequestIds Ids { get; }

    public string ModelKey { get; }

    internal QoderStreamSession(
        HttpResponseMessage resp,
        StreamReader reader,
        CancellationTokenSource linked,
        CancellationToken outer,
        string accountId,
        string requestedModel,
        string modelKey,
        QoderRequestIds ids,
        UsageCollector usage)
    {
        _resp = resp;
        _reader = reader;
        _linked = linked;
        _outer = outer;
        _accountId = accountId;
        _requestedModel = requestedModel;
        ModelKey = modelKey;
        Ids = ids;
        Usage = usage;
    }

    public string AccountId => _accountId;

    public async Task<PrimeResult> PrimeAsync(CancellationToken ct)
    {
        // 首字节超时：连首包都拿不到就换号。
        _linked.CancelAfter(QoderHttp.PrimeTimeout);
        try
        {
            while (true)
            {
                var line = await ReadLineAsync(ct);
                if (line is null)
                {
                    // 流提前结束且没有任何业务内容。
                    Error = new QoderUpstreamException(
                        QoderErrorKind.Server, null, "", "上游在返回任何内容前就结束了连接");
                    return QoderErrorKind.Server.IsRetryable() ? PrimeResult.RetryableError : PrimeResult.FatalError;
                }

                var parsed = TryParseEnvelope(line);
                if (parsed.Kind == EnvelopeKind.Skip)
                {
                    continue;
                }
                if (parsed.Kind == EnvelopeKind.Finish)
                {
                    FirstTokenMs = parsed.FirstTokenMs;
                    continue;
                }
                if (parsed.Kind == EnvelopeKind.Error)
                {
                    var kind = QoderErrorClassifier.Classify(parsed.HttpStatus, parsed.RawBody);
                    Error = new QoderUpstreamException(kind, parsed.HttpStatus, parsed.RawBody,
                        $"上游返回错误（{kind}）: {Truncate(parsed.RawBody, 300)}");
                    return kind.IsRetryable() ? PrimeResult.RetryableError : PrimeResult.FatalError;
                }
                if (parsed.Kind == EnvelopeKind.Done)
                {
                    // 还没吐任何内容就 [DONE]：异常收尾，视为可重试的上游故障。
                    Error = new QoderUpstreamException(
                        QoderErrorKind.Server, null, parsed.RawBody, "上游未返回任何内容即结束");
                    return PrimeResult.RetryableError;
                }

                // 有效业务 chunk：缓存下来（**绝不能丢**，否则首帧内容缺失），提交。
                _primedJson = RewriteModel(parsed.InnerJson!, _requestedModel);
                ObserveUsage(parsed.InnerJson!);
                return PrimeResult.Committed;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 是首字节超时触发的取消（而非客户端断连）。
            Error = new QoderUpstreamException(
                QoderErrorKind.Server, null, "", $"上游 {QoderHttp.PrimeTimeout.TotalSeconds:F0}s 内未返回首包");
            return PrimeResult.RetryableError;
        }
    }

    public async IAsyncEnumerable<StreamEvent> ReadEventsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        if (_primedJson is not null)
        {
            yield return new StreamEvent(StreamEventKind.Chunk, _primedJson);
            _primedJson = null;
        }

        while (true)
        {
            string? line;
            try
            {
                line = await ReadLineAsync(ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new QoderUpstreamException(
                    QoderErrorKind.Server, null, "", $"上游 {QoderHttp.IdleTimeout.TotalSeconds:F0}s 无数据（读空闲超时）");
            }

            if (line is null)
            {
                yield return new StreamEvent(StreamEventKind.Done);
                yield break;
            }

            var parsed = TryParseEnvelope(line);
            switch (parsed.Kind)
            {
                case EnvelopeKind.Skip:
                    continue;

                case EnvelopeKind.Finish:
                    FirstTokenMs = parsed.FirstTokenMs;
                    yield return new StreamEvent(StreamEventKind.Finish, null, parsed.FirstTokenMs);
                    continue;

                case EnvelopeKind.Error:
                {
                    var kind = QoderErrorClassifier.Classify(parsed.HttpStatus, parsed.RawBody);
                    throw new QoderUpstreamException(kind, parsed.HttpStatus, parsed.RawBody, $"上游流中错误（{kind}）: {Truncate(parsed.RawBody, 300)}");
                }

                case EnvelopeKind.Done:
                    yield return new StreamEvent(StreamEventKind.Done);
                    yield break;

                case EnvelopeKind.Chunk:
                default:
                {
                    var inner = parsed.InnerJson!;
                    ObserveUsage(inner);
                    // 纯用量 chunk：不当作内容转发（是否发给客户端由 include_usage 决定）。
                    if (IsUsageOnlyChunk(inner))
                    {
                        yield return new StreamEvent(StreamEventKind.Usage, RewriteModel(inner, _requestedModel));
                        continue;
                    }
                    yield return new StreamEvent(StreamEventKind.Chunk, RewriteModel(inner, _requestedModel));
                    continue;
                }
            }
        }
    }

    private void ObserveUsage(string innerJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(innerJson);
            Usage.ObserveChunk(doc.RootElement);
        }
        catch (JsonException)
        {
            // 不是合法 JSON，忽略（不影响转发）。
        }
    }

    private static bool IsUsageOnlyChunk(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("usage", out _))
            {
                return false;
            }
            return !root.TryGetProperty("choices", out var ch)
                   || ch.ValueKind != JsonValueKind.Array
                   || ch.GetArrayLength() == 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string RewriteModel(string json, string model)
    {
        if (string.IsNullOrEmpty(model))
        {
            return json;
        }
        try
        {
            var node = JsonNode.Parse(json);
            if (node is JsonObject obj && obj.ContainsKey("model"))
            {
                obj["model"] = model;
                return obj.ToJsonString();
            }
            return json;
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        // 读空闲超时：防上游静默挂死——客户端断连后若上游既不发数据也不断开，
        // 仅靠 ct 取消并不保证读操作立刻返回。
        _linked.CancelAfter(QoderHttp.IdleTimeout);
        return await _reader.ReadLineAsync(_linked.Token);
    }

    private enum EnvelopeKind { Skip, Chunk, Usage, Done, Error, Finish }

    private readonly record struct Envelope(
        EnvelopeKind Kind,
        string? InnerJson = null,
        int? HttpStatus = null,
        string RawBody = "",
        long FirstTokenMs = 0);

    private static Envelope TryParseEnvelope(string line)
    {
        line = line.Trim();
        if (line.Length == 0)
        {
            return new Envelope(EnvelopeKind.Skip);
        }

        // 私有收尾事件：event:finish 后面跟一行 data:{"firstTokenDuration":...}
        if (line.StartsWith("event:", StringComparison.Ordinal))
        {
            return new Envelope(EnvelopeKind.Skip);
        }

        if (!line.StartsWith("data:", StringComparison.Ordinal))
        {
            return new Envelope(EnvelopeKind.Skip); // 心跳/注释
        }

        string dataStr = line[5..].Trim();
        if (dataStr.Length == 0)
        {
            return new Envelope(EnvelopeKind.Skip);
        }

        // 有些实现会直接发裸 [DONE]（不在信封里）。
        if (dataStr == "[DONE]")
        {
            return new Envelope(EnvelopeKind.Done);
        }

        // 可能没有信封（裸 JSON）——上游偶发形态，兜底当作内层 body。
        if (!dataStr.StartsWith('{'))
        {
            return new Envelope(EnvelopeKind.Skip);
        }

        try
        {
            using var doc = JsonDocument.Parse(dataStr);
            var root = doc.RootElement;

            // 非信封形态的 finish 事件（含 firstTokenDuration 但没有 body）。
            if (!root.TryGetProperty("body", out _) && root.TryGetProperty("firstTokenDuration", out var ftd))
            {
                return new Envelope(EnvelopeKind.Finish, FirstTokenMs: ftd.TryGetInt64(out var v) ? v : 0);
            }

            int? status = null;
            if (root.TryGetProperty("statusCodeValue", out var sc) && sc.ValueKind == JsonValueKind.Number)
            {
                status = sc.TryGetInt32(out var s) ? s : null;
            }

            string? inner = null;
            if (root.TryGetProperty("body", out var bodyProp) && bodyProp.ValueKind == JsonValueKind.String)
            {
                inner = bodyProp.GetString();
            }

            if (status is not null && status != 200)
            {
                // 信封内错误：HTTP 本身是 200，错误信息在 body 里。
                return new Envelope(EnvelopeKind.Error, HttpStatus: status, RawBody: inner ?? dataStr);
            }

            if (string.IsNullOrWhiteSpace(inner))
            {
                return new Envelope(EnvelopeKind.Skip);
            }

            if (inner == "[DONE]")
            {
                return new Envelope(EnvelopeKind.Done);
            }

            // 内层也可能是 finish 事件。
            if (inner.Contains("firstTokenDuration", StringComparison.Ordinal))
            {
                try
                {
                    using var innerDoc = JsonDocument.Parse(inner);
                    if (innerDoc.RootElement.TryGetProperty("firstTokenDuration", out var iftd))
                    {
                        return new Envelope(EnvelopeKind.Finish, FirstTokenMs: iftd.TryGetInt64(out var iv) ? iv : 0);
                    }
                }
                catch (JsonException)
                {
                    // 落到下面当普通 chunk
                }
            }

            return new Envelope(EnvelopeKind.Chunk, InnerJson: inner);
        }
        catch (JsonException)
        {
            return new Envelope(EnvelopeKind.Skip);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _linked.Cancel();
        }
        catch
        {
            // 忽略：取消已释放的 CTS
        }
        try
        {
            _reader.Dispose();
        }
        catch
        {
            // 忽略
        }
        // 中止底层连接：ResponseHeadersRead 模式下这是立即断连，
        // 比"读完剩余 body"快得多，也是客户端断连时释放上游资源的正确姿势。
        _resp.Dispose();
        _linked.Dispose();
        await Task.CompletedTask;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}

public class QoderProxyService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly QoderAuthService _auth;

    public QoderProxyService(IHttpClientFactory httpFactory, QoderAuthService auth)
    {
        _httpFactory = httpFactory;
        _auth = auth;
    }

    public async Task<QoderStreamSession> OpenAsync(
        ChatCompletionRequest request,
        CosyCreds creds,
        string accountId,
        QoderRequestIds? ids = null,
        CancellationToken ct = default)
    {
        var modelDef = QoderConstants.ResolveModel(request.Model);
        var requestIds = ids ?? QoderRequestIds.New();
        var payload = BuildQoderPayload(request, modelDef, requestIds);

        byte[] plainBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        byte[] encodedBody = QoderEncoder.EncodeBody(plainBytes);

        var headers = CosySigner.BuildCosyHeaders(encodedBody, QoderConstants.ChatURLEncoded, creds);

        var req = new HttpRequestMessage(HttpMethod.Post, QoderConstants.ChatURLEncoded);
        foreach (var (k, v) in headers)
        {
            req.Headers.TryAddWithoutValidation(k, v);
        }
        req.Headers.TryAddWithoutValidation("X-Model-Key", modelDef.Key);
        req.Headers.TryAddWithoutValidation("X-Model-Source", "system");
        req.Headers.TryAddWithoutValidation("Cosy-Business-Product", "app");
        req.Headers.TryAddWithoutValidation("Cosy-Business-Type", "agent");
        req.Headers.TryAddWithoutValidation("Cosy-Scene", "app");
        req.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
        req.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        req.Content = new ByteArrayContent(encodedBody);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        var http = _httpFactory.CreateClient(QoderHttp.ClientName);

        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            req.Dispose();
            throw; // 客户端断连：交给上层，不算账号错误
        }
        catch (HttpRequestException ex)
        {
            req.Dispose();
            // 传输层失败（DNS/连接/TLS/超时）：归为可重试，喂连败计数，
            // 但**不计入账号成功率**——同一时刻换任何账号都一样连不上，
            // 那不是账号的锅（实测触发：SSL connection could not be established）。
            throw new QoderUpstreamException(
                QoderErrorKind.Transport, null, "", $"连接上游失败: {ex.Message}");
        }

        if (!resp.IsSuccessStatusCode)
        {
            string errBody = "";
            try
            {
                errBody = await resp.Content.ReadAsStringAsync(ct);
            }
            catch
            {
                // 读错误体失败不致命，用状态码分类即可
            }
            var status = (int)resp.StatusCode;
            var kind = QoderErrorClassifier.Classify(status, errBody);
            resp.Dispose();
            req.Dispose();
            throw new QoderUpstreamException(kind, status, errBody, $"Qoder 上游返回 HTTP {status}: {Truncate(errBody, 300)}");
        }

        var stream = await resp.Content.ReadAsStreamAsync(ct);
        var reader = new StreamReader(stream, Encoding.UTF8);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

        int promptEstimate = TokenEstimator.EstimatePrompt(
            request.Messages.Select(m => m.GetTextContent()),
            request.Tools?.Count ?? 0);

        var usage = new UsageCollector(promptEstimate);
        return new QoderStreamSession(resp, reader, linked, ct, accountId, request.Model,
            modelDef.Key, requestIds, usage);
    }

    public async IAsyncEnumerable<string> StreamChatAsync(
        ChatCompletionRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var creds = await _auth.GetValidCosyCredsAsync(ct);
        var acc = _auth.ActiveAccount;
        await using var session = await OpenAsync(request, creds, acc?.Id ?? "", null, ct);

        var prime = await session.PrimeAsync(ct);
        if (prime != PrimeResult.Committed)
        {
            throw session.Error ?? new QoderUpstreamException(QoderErrorKind.Server, null, "", "上游无响应");
        }

        await foreach (var ev in session.ReadEventsAsync(ct))
        {
            switch (ev.Kind)
            {
                case StreamEventKind.Chunk:
                    yield return ev.Json!;
                    break;
                case StreamEventKind.Done:
                    yield return "[DONE]";
                    yield break;
            }
        }
    }

    public async Task<string> NonStreamChatAsync(ChatCompletionRequest request, CancellationToken ct = default)
    {
        var creds = await _auth.GetValidCosyCredsAsync(ct);
        var acc = _auth.ActiveAccount;

        await using var session = await OpenAsync(request, creds, acc?.Id ?? "", null, ct);
        var prime = await session.PrimeAsync(ct);
        if (prime != PrimeResult.Committed)
        {
            throw session.Error ?? new QoderUpstreamException(QoderErrorKind.Server, null, "", "上游无响应");
        }

        string id = "chatcmpl-" + Guid.NewGuid().ToString("N");
        long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        StringBuilder contentSb = new();
        StringBuilder reasoningSb = new();
        string finishReason = "stop";

        await foreach (var ev in session.ReadEventsAsync(ct))
        {
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

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private static QoderPayload BuildQoderPayload( ChatCompletionRequest request, QoderConstants.QoderModelDefinition modelDef, QoderRequestIds ids)
    {
        StringBuilder sysSb = new();
        List<QoderMessageItem> qoderMsgs = [];
        string lastUserText = "";

        foreach (var m in request.Messages)
        {
            string text = m.GetTextContent();
            if (m.Role.Equals("system", StringComparison.OrdinalIgnoreCase) ||
                m.Role.Equals("developer", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(text))
                {
                    if (sysSb.Length > 0) sysSb.Append("\n\n");
                    sysSb.Append(text);
                }
                continue;
            }

            if (m.Role.Equals("user", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(text))
            {
                lastUserText = text;
            }

            qoderMsgs.Add(new QoderMessageItem
            {
                Role = m.Role.ToLowerInvariant(),
                Content = text,
                Name = m.Name,
                ToolCalls = m.ToolCalls,
                ToolCallId = m.ToolCallId
            });
        }

        return new QoderPayload
        {
            RequestId = ids.RequestId,
            RequestSetId = ids.RequestSetId,
            ChatRecordId = ids.ChatRecordId,
            SessionId = ids.SessionId,
            Stream = true,
            ChatTask = "FREE_INPUT",
            IsReply = true,
            IsRetry = false,
            Source = 1,
            Version = "3",
            SessionType = "qodercli",
            AgentId = "agent_common",
            TaskId = "common",
            System = sysSb.ToString(),
            Messages = qoderMsgs,
            Tools = request.Tools ?? [],
            Parameters = new QoderParameters
            {
                MaxTokens = request.EffectiveMaxTokens ?? 32768,
                Temperature = request.Temperature,
                TopP = request.TopP,
            },
            ChatContext = new QoderChatContext
            {
                ChatPrompt = "",
                Extra = new QoderChatExtra
                {
                    ModelConfig = new QoderModelRef
                    {
                        Key = modelDef.Key,
                        IsReasoning = modelDef.IsReasoning
                    },
                    OriginalContent = lastUserText
                },
                Text = lastUserText
            },
            ModelConfig = new QoderModelConfig
            {
                Key = modelDef.Key,
                DisplayName = modelDef.DisplayName,
                Model = "",
                Format = "openai",
                IsVl = modelDef.IsVl,
                IsReasoning = modelDef.IsReasoning,
                ApiKey = "",
                Url = "",
                Source = "system",
                MaxInputTokens = modelDef.MaxInputTokens
            },
            Business = new QoderBusiness
            {
                Product = "cli",
                Version = "1.0.0",
                Type = "agent",
                Stage = "start",
                Id = Guid.NewGuid().ToString(),
                Name = lastUserText.Length > 30 ? lastUserText[..30] : lastUserText,
                BeginAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            }
        };
    }
}
