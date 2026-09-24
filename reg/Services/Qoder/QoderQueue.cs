using System.Text.Json;
using reg.Models;

namespace reg.Services.Qoder;

public sealed class QoderQueueContext
{
    public bool? IsQueued { get; set; }

    public string? ModelKey { get; set; }

    public int? QueueCount { get; set; }

    public string? QueueType { get; set; }

    public bool? ServiceAvailable { get; set; }

    public int? RetryAfterSeconds { get; set; }

    public int? WaitTime { get; set; }

    public bool HasAny =>
        IsQueued is not null || ServiceAvailable is not null || ModelKey is not null
        || QueueCount is not null || QueueType is not null || RetryAfterSeconds is not null;
}

public static class QoderQueueParser
{
    // Qoder 用于表达「模型排队」的业务码。
    public const string ModelQueuedCode = "10605";

    private static readonly string[] DrillKeys = ["error", "data", "body", "message", "cause", "result"];

    public static QoderQueueContext? Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }
        var nodes = CollectNodes(body);
        return FindQueue(nodes);
    }

    public static bool HasModelQueuedCode(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }
        foreach (var node in CollectNodes(body))
        {
            if (TryGetString(node, "code") == ModelQueuedCode)
            {
                return true;
            }
        }
        return false;
    }

    public static int PollIntervalSeconds(QoderQueueContext? ctx, int fallbackSeconds) => ctx?.RetryAfterSeconds is > 0 ? ctx.RetryAfterSeconds.Value : fallbackSeconds;

    private static List<JsonElement> CollectNodes(string body)
    {
        var result = new List<JsonElement>();
        var docs = new List<JsonDocument>(); // 保持存活，避免 JsonElement 悬空
        try
        {
            var root = TryParse(body, docs);
            if (root is null)
            {
                return result;
            }

            var seen = new HashSet<JsonElement>();
            var queue = new Queue<JsonElement>();
            queue.Enqueue(root.Value);

            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                if (cur.ValueKind != JsonValueKind.Object || !seen.Add(cur))
                {
                    continue;
                }
                result.Add(cur);

                foreach (var key in DrillKeys)
                {
                    if (!cur.TryGetProperty(key, out var child))
                    {
                        continue;
                    }
                    if (child.ValueKind == JsonValueKind.Object)
                    {
                        queue.Enqueue(child);
                    }
                    else if (child.ValueKind == JsonValueKind.String)
                    {
                        var nested = TryParse(child.GetString(), docs);
                        if (nested is not null)
                        {
                            queue.Enqueue(nested.Value);
                        }
                    }
                }
            }
        }
        finally
        {
            // JsonElement 是结构体、指向 JsonDocument 的缓冲区，所以文档必须活到解析结束。
            // 这里把清理交给 GC 前先完成所有读取——调用方拿到的 QoderQueueContext 已是纯托管值。
            foreach (var d in docs)
            {
                // 不 Dispose：上面的 result 里还引用着这些文档的缓冲区。
                // 每次请求的文档数量有限（个位数），交给 GC 即可。
                _ = d;
            }
        }
        return result;
    }

    private static JsonElement? TryParse(string? text, List<JsonDocument> keepAlive)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var t = text.Trim();
        if (t.Length == 0 || (t[0] != '{' && t[0] != '['))
        {
            return null;
        }
        try
        {
            var doc = JsonDocument.Parse(t);
            keepAlive.Add(doc);
            return doc.RootElement;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static QoderQueueContext? FindQueue(List<JsonElement> nodes)
    {
        foreach (var node in nodes)
        {
            var isQueued = TryGetBool(node, "isQueued");
            var serviceAvailable = TryGetBool(node, "serviceAvailable");
            if (isQueued is null && serviceAvailable is null)
            {
                continue;
            }
            return new QoderQueueContext
            {
                IsQueued = isQueued,
                ServiceAvailable = serviceAvailable,
                ModelKey = TryGetString(node, "modelKey"),
                QueueCount = TryGetInt(node, "queueCount"),
                QueueType = TryGetString(node, "queueType"),
                RetryAfterSeconds = TryGetInt(node, "retryAfterSeconds"),
                WaitTime = TryGetInt(node, "waitTime"),
            };
        }
        return null;
    }

    private static string? TryGetString(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v))
        {
            return null;
        }
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() is { Length: > 0 } s ? s : null,
            JsonValueKind.Number => v.ToString(),
            _ => null,
        };
    }

    private static int? TryGetInt(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v))
        {
            return null;
        }
        if (v.ValueKind == JsonValueKind.Number)
        {
            return v.TryGetInt32(out var i) ? i : (int)v.GetDouble();
        }
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s))
        {
            return s;
        }
        return null;
    }

    private static bool? TryGetBool(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v))
        {
            return null;
        }
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }
}
