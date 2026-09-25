namespace Qoder2Api.Services.Usage;

public static class TokenEstimator
{
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

    public static int EstimatePrompt(IEnumerable<string> messageTexts, int toolCount = 0)
    {
        int total = 3; // 回复引导开销
        foreach (var t in messageTexts)
        {
            total += 3 + EstimateText(t); // 每条消息的角色/分隔符开销
        }
        if (toolCount > 0)
        {
            total += toolCount * 300; // 工具定义的粗略开销（名称+描述+schema）
        }
        return total;
    }

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
