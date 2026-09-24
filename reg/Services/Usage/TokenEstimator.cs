namespace reg.Services.Usage;

/// <summary>
/// 本地 token 估算器，仅作兜底（上游实测优先，见 <see cref="UsageCollector"/>）。
/// 口径：CJK 约 1 token/字，其余约 4 字符/token，每条消息 +3，整体再 +3。
/// 不用真分词器是因为 Qoder 全是国产模型，cl100k 词表本就不匹配。
/// </summary>
public static class TokenEstimator
{
    /// <summary>估算一段文本的 token 数。</summary>
    public static int EstimateText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        int cjk = 0;
        int other = 0;
        foreach (var ch in text)
        {
            if (IsCjk(ch))
            {
                cjk++;
            }
            else
            {
                other++;
            }
        }

        // 非 CJK 按 4 字符 1 token 向上取整——短文本（1~3 字符）也应算 1 token 而非 0。
        int otherTokens = (other + 3) / 4;
        return cjk + otherTokens;
    }

    /// <summary>
    /// 估算一次请求的 prompt 总量：逐条消息文本 + 每条的消息开销 + 工具定义 + 引导开销。
    /// </summary>
    public static int EstimatePrompt(IEnumerable<string> messageTexts, int toolCount = 0)
    {
        int total = 3; // 回复引导开销
        foreach (var t in messageTexts)
        {
            total += 3 + EstimateText(t); // 每条消息的角色/分隔符开销
        }
        if (toolCount > 0)
        {
            total += toolCount * 8; // 工具定义的粗略开销（名称+描述+schema）
        }
        return total;
    }

    /// <summary>
    /// 判定是否 CJK（中日韩）字符。这些字符在主流词表里普遍接近"一字一 token"，
    /// 与拉丁文本的"四字符一 token"差一个量级，必须分开计。
    /// </summary>
    private static bool IsCjk(char ch) => ch switch
    {
        >= '一' and <= '鿿' => true,   // CJK 统一表意文字
        >= '㐀' and <= '䶿' => true,   // CJK 扩展 A
        >= '　' and <= '〿' => true,   // CJK 标点
        >= '぀' and <= 'ヿ' => true,   // 日文假名
        >= '가' and <= '힯' => true,   // 韩文音节
        >= '豈' and <= '﫿' => true,   // CJK 兼容表意文字
        _ => false,
    };
}
