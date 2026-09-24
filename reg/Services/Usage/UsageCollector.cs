using System.Text;
using System.Text.Json;
using reg.Models;

namespace reg.Services.Usage;

/// <summary>
/// 采集一次请求的用量，装配成 OpenAI 规范的 usage。优先级：上游 usage &gt; credits &gt; 本地估算。
/// 哨兵原则：没观测到留 null，绝不写 0（"测得 0" 与 "没观测到" 是两回事）。
/// 每请求一个实例，单消费循环使用，无需加锁。
/// </summary>
public sealed class UsageCollector
{
    private readonly int _promptEstimate;
    private readonly StringBuilder _content = new();
    private readonly StringBuilder _reasoning = new();
    private UsageInfo? _upstream;

    /// <param name="promptEstimate">
    /// prompt 的本地估算值（请求发出前算好）。只在拿不到上游 usage 时才用得上。
    /// </param>
    public UsageCollector(int promptEstimate)
    {
        _promptEstimate = promptEstimate;
    }

    /// <summary>是否拿到了上游的真实用量。</summary>
    public bool HasUpstream => _upstream is not null;

    /// <summary>上游给出的计费额（Qoder 私有字段），无则 null。</summary>
    public double? Credits => _upstream?.Credits;

    /// <summary>累计的输出字符数（用于观测，不参与计费）。</summary>
    public int OutputChars => _content.Length + _reasoning.Length;

    /// <summary>
    /// 观察一个已解析的上游 chunk（内层 body 的 JSON）。
    /// 同时承担两件事：累积内容（供估算兜底）与捕获 usage。
    /// </summary>
    public void ObserveChunk(JsonElement root)
    {
        // 1. usage 捕获：上游的用量 chunk 形如 {"choices":[],"usage":{...}}。
        if (root.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
        {
            _upstream = ParseUsage(usageEl);
        }

        // 2. 内容累积（估算兜底用）。
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
        {
            return;
        }
        foreach (var choice in choices.EnumerateArray())
        {
            if (!choice.TryGetProperty("delta", out var delta))
            {
                continue;
            }
            if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
            {
                _content.Append(c.GetString());
            }
            if (delta.TryGetProperty("reasoning_content", out var r) && r.ValueKind == JsonValueKind.String)
            {
                _reasoning.Append(r.GetString());
            }
        }
    }

    /// <summary>
    /// 直接注入一个上游 usage（非流式聚合路径用——那里已经把 usage 对象取出来了）。
    /// </summary>
    public void ObserveUpstream(UsageInfo usage) => _upstream = usage;

    /// <summary>装配最终的 usage。</summary>
    public UsageInfo Build()
    {
        if (_upstream is not null)
        {
            var u = _upstream;
            u.EnsureTotal();
            u.Source = "upstream";
            return u;
        }

        // 兜底估算：明确标注 estimated，绝不与实测值混淆。
        int prompt = _promptEstimate;
        int completion = TokenEstimator.EstimateText(_content.ToString())
                         + TokenEstimator.EstimateText(_reasoning.ToString());
        return new UsageInfo
        {
            PromptTokens = prompt,
            CompletionTokens = completion,
            TotalTokens = prompt + completion,
            CompletionTokensDetails = _reasoning.Length > 0
                ? new CompletionTokensDetails { ReasoningTokens = TokenEstimator.EstimateText(_reasoning.ToString()) }
                : null,
            Source = "estimated",
        };
    }

    /// <summary>
    /// 解析上游 usage。字段可能整体缺失（实测 9 条流有 4 条没有 completion_tokens_details），
    /// 缺失一律留 null。
    /// </summary>
    public static UsageInfo ParseUsage(JsonElement usage)
    {
        var info = new UsageInfo
        {
            PromptTokens = TryGetInt(usage, "prompt_tokens"),
            CompletionTokens = TryGetInt(usage, "completion_tokens"),
            TotalTokens = TryGetInt(usage, "total_tokens"),
            Credits = TryGetDouble(usage, "credits"),
            Billable = TryGetBool(usage, "billable"),
        };

        if (usage.TryGetProperty("prompt_tokens_details", out var pd) && pd.ValueKind == JsonValueKind.Object)
        {
            info.PromptTokensDetails = new PromptTokensDetails
            {
                CachedTokens = TryGetInt(pd, "cached_tokens"),
            };
        }

        if (usage.TryGetProperty("completion_tokens_details", out var cd) && cd.ValueKind == JsonValueKind.Object)
        {
            info.CompletionTokensDetails = new CompletionTokensDetails
            {
                ReasoningTokens = TryGetInt(cd, "reasoning_tokens"),
            };
        }

        info.EnsureTotal();
        return info;
    }

    private static int? TryGetInt(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v))
        {
            return null;
        }
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt32(out var i) => i,
            JsonValueKind.Number => (int)v.GetDouble(),
            _ => null,
        };
    }

    private static double? TryGetDouble(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number)
        {
            return null;
        }
        return v.GetDouble();
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
