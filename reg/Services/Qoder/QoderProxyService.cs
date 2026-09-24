using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using reg.Models;
using reg.Services.Usage;

namespace reg.Services.Qoder;

/// <summary>流事件的种类。</summary>
public enum StreamEventKind
{
    /// <summary>常规内容 chunk（已改写 model 字段，可直接转发）。</summary>
    Chunk,

    /// <summary>纯用量 chunk（<c>choices:[]</c> + <c>usage</c>）。是否转发给客户端由 include_usage 决定。</summary>
    Usage,

    /// <summary>上游的 [DONE]。</summary>
    Done,

    /// <summary>上游的 event:finish（含 firstTokenDuration，用于记 TTFT）。</summary>
    Finish,
}

/// <summary>一个流事件。</summary>
public readonly record struct StreamEvent(StreamEventKind Kind, string? Json = null, long FirstTokenMs = 0);

/// <summary>探测首个事件的结果。</summary>
public enum PrimeResult
{
    /// <summary>拿到有效业务内容，可以提交给客户端了。</summary>
    Committed,

    /// <summary>首包就是可重试的错误（换号可能成功）。</summary>
    RetryableError,

    /// <summary>首包是请求级错误（换号也没用，应直接把原文返回给客户端）。</summary>
    FatalError,
}

/// <summary>
/// 一次上游 SSE 会话。持有 HTTP 连接，负责把上游的「信封 + 内层 JSON」解包成
/// <see cref="StreamEvent"/>，并顺带采集用量。
///
/// **生命周期**：必须在 finally 里 Dispose。Dispose 会中止底层 HTTP 连接——
/// 在 <c>ResponseHeadersRead</c> 模式下这是立即断连，比"读完剩余 body"快得多，
/// 也是客户端断连时及时释放上游资源的正确姿势。
/// </summary>
public sealed class QoderStreamSession : IAsyncDisposable
{
    private readonly HttpResponseMessage _resp;
    private readonly StreamReader _reader;
    private readonly CancellationTokenSource _linked;
    private readonly CancellationToken _outer;
    private readonly string _requestedModel;
    private readonly string _accountId;

    private string? _primedJson;

    /// <summary>上游返回的错误（仅当 <see cref="PrimeAsync"/> 返回非 Committed 时非空）。</summary>
    public QoderUpstreamException? Error { get; private set; }

    /// <summary>本次会话的用量采集器。</summary>
    public UsageCollector Usage { get; }

    /// <summary>上游 event:finish 给出的首 token 耗时（毫秒），未收到则为 0。</summary>
    public long FirstTokenMs { get; private set; }

    internal QoderStreamSession(
        HttpResponseMessage resp,
        StreamReader reader,
        CancellationTokenSource linked,
        CancellationToken outer,
        string accountId,
        string requestedModel,
        UsageCollector usage)
    {
        _resp = resp;
        _reader = reader;
        _linked = linked;
        _outer = outer;
        _accountId = accountId;
        _requestedModel = requestedModel;
        Usage = usage;
    }

    public string AccountId => _accountId;

    /// <summary>
    /// 探测首个**有内容的业务事件**。
    ///
    /// 语义要点：不是"第一行"，也不是"第一个 data:"，而是第一个真正携带业务内容的
    /// 事件——心跳行、注释行、空 body 的信封、以及事件前的空壳都要跳过。
    ///
    /// 为什么要有这一步：流式响应**一旦写出字节就无法再换号**（客户端会收到两段
    /// 拼接的内容且无法察觉）。把提交点推迟到"确认首包不是错误"之后，就能在
    /// 绝大多数账号级故障（401/403/429/模型无权限，都发生在首包）上安全地换号重试。
    /// </summary>
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

    /// <summary>
    /// 继续读取剩余事件。**必须先发 <see cref="PrimeAsync"/> 缓存的那一帧**，
    /// 否则首帧内容会丢。
    /// </summary>
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
                    throw new QoderUpstreamException(kind, parsed.HttpStatus, parsed.RawBody,
                        $"上游流中错误（{kind}）: {Truncate(parsed.RawBody, 300)}");
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

    /// <summary>
    /// 判断是否"纯用量 chunk"：<c>choices</c> 为空数组且带 <c>usage</c>。
    /// 这是上游固定的收尾形态（finish_reason 之后、[DONE] 之前）。
    /// </summary>
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

    /// <summary>
    /// 把 chunk 顶层的 <c>model</c> 改成客户端请求的模型名。
    ///
    /// 用 JSON 解析而非正则替换：正则 <c>"model"\s*:\s*"[^"]*"</c> 在实践中是安全的
    /// （正文里的引号必然转义成 <c>\"</c>，而 <c>\s*:\s*</c> 跨不过反斜杠），
    /// 但那是依赖转义规则的隐式假设；既然本来就要解析每个 chunk 采 usage，
    /// 顺手结构化改写，成本几乎为零且不依赖假设。
    /// </summary>
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

    /// <summary>读一行，带读空闲超时（每读到一行就重置计时）。</summary>
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

    /// <summary>
    /// 解析上游的一行。上游格式（抓包实测）：
    /// <code>
    /// data:{"headers":{...},"body":"&lt;内层 JSON 字符串&gt;","statusCodeValue":200,"statusCode":"OK"}
    /// </code>
    /// 信封固定 4 键，<c>body</c> 是**字符串**需二次解析；另有私有的
    /// <c>event:finish</c> 事件携带 firstTokenDuration。
    /// </summary>
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

/// <summary>
/// Qoder 上游代理：构造请求体、COSY 签名、发起 SSE 连接。
///
/// 与旧版的区别：不再"发出去就开始 yield"，而是先返回一个
/// <see cref="QoderStreamSession"/> 让调用方探测首包——只有确认首包不是错误，
/// 才把响应提交给客户端。这样流式请求也能在账号级故障时安全换号。
/// </summary>
public class QoderProxyService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly QoderAuthService _auth;

    public QoderProxyService(IHttpClientFactory httpFactory, QoderAuthService auth)
    {
        _httpFactory = httpFactory;
        _auth = auth;
    }

    /// <summary>
    /// 用指定账号发起一次 chat 请求，返回可探测的流会话。
    /// HTTP 层错误（非 2xx）在这里就抛出带分类的异常——此时还没有任何响应字节写出。
    /// </summary>
    public async Task<QoderStreamSession> OpenAsync(
        ChatCompletionRequest request,
        CosyCreds creds,
        string accountId,
        CancellationToken ct = default)
    {
        var modelDef = QoderConstants.ResolveModel(request.Model);
        var payload = BuildQoderPayload(request, modelDef);

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
            throw new QoderUpstreamException(kind, status, errBody,
                $"Qoder 上游返回 HTTP {status}: {Truncate(errBody, 300)}");
        }

        var stream = await resp.Content.ReadAsStreamAsync(ct);
        var reader = new StreamReader(stream, Encoding.UTF8);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

        int promptEstimate = TokenEstimator.EstimatePrompt(
            request.Messages.Select(m => m.GetTextContent()),
            request.Tools?.Count ?? 0);

        var usage = new UsageCollector(promptEstimate);
        return new QoderStreamSession(resp, reader, linked, ct, accountId, request.Model, usage);
    }

    /// <summary>
    /// 兼容旧调用方的薄封装：单账号、不做首包探测。新代码请用
    /// <see cref="OpenAsync"/> + 轮换循环。
    /// </summary>
    public async IAsyncEnumerable<string> StreamChatAsync(
        ChatCompletionRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var creds = await _auth.GetValidCosyCredsAsync(ct);
        var acc = _auth.ActiveAccount;
        await using var session = await OpenAsync(request, creds, acc?.Id ?? "", ct);

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

    /// <summary>
    /// 非流式：把流式结果聚合成一个完整的 chat.completion 响应。
    /// usage 直接采用上游真值（抓包证实上游会给出），不再像旧版那样写死 0。
    /// </summary>
    public async Task<string> NonStreamChatAsync(ChatCompletionRequest request, CancellationToken ct = default)
    {
        var creds = await _auth.GetValidCosyCredsAsync(ct);
        var acc = _auth.ActiveAccount;

        await using var session = await OpenAsync(request, creds, acc?.Id ?? "", ct);
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

    /// <summary>
    /// 构造上游请求体。
    ///
    /// 注意：**每次调用都生成全新的 RequestId/SessionId/ChatRecordId**。多账号重试时
    /// 每次尝试都必须是独立会话，否则上游可能把两次尝试串进同一个会话。
    /// 不要把这段提到重试循环外面"复用"。
    /// </summary>
    private static QoderPayload BuildQoderPayload(ChatCompletionRequest request, QoderConstants.QoderModelDefinition modelDef)
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

        string recordId = Guid.NewGuid().ToString("N")[..16];
        string sessionId = Guid.NewGuid().ToString("N")[..16];

        return new QoderPayload
        {
            RequestId = Guid.NewGuid().ToString(),
            RequestSetId = recordId,
            ChatRecordId = recordId,
            SessionId = sessionId,
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
