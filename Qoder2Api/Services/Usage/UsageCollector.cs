using System.Text;
using System.Text.Json;
using Qoder2Api.Models;

namespace Qoder2Api.Services.Usage;

public sealed class UsageCollector
{
    private readonly int _promptEstimate;
    private readonly StringBuilder _content = new();
    private readonly StringBuilder _reasoning = new();
    private UsageInfo? _upstream;

    public UsageCollector(int promptEstimate)
    {
        _promptEstimate = promptEstimate;
    }

    public bool HasUpstream => _upstream is not null;

    public double? Credits => _upstream?.Credits;

    public int OutputChars => _content.Length + _reasoning.Length;

    public void ObserveChunk(JsonElement root)
    {
        // usage 捕获：上游的用量 chunk 形如 {"choices":[],"usage":{...}}。
        if (root.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
        {
            _upstream = ParseUsage(usageEl);
        }

        // 内容累积（估算兜底用）。
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

    public void ObserveUpstream(UsageInfo usage) => _upstream = usage;

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
