namespace Qoder2Api.Services.Qoder;

public enum QoderRegion
{
    /// <summary>国际版（qoder.sh / qoder.com）。</summary>
    Global = 0,

    /// <summary>国内版（qoder.com.cn / qoder.cn）。</summary>
    Cn = 1,
}

public sealed record QoderEndpoints(
    QoderRegion Region,
    string DisplayName,
    string InferBase,
    string OpenApiBase,
    string WebBase)
{
    /// <summary>国际版端点。</summary>
    public static readonly QoderEndpoints Global = new(
        QoderRegion.Global,
        "国际版",
        InferBase: "https://api3.qoder.sh",
        OpenApiBase: "https://openapi.qoder.sh",
        WebBase: "https://qoder.com");

    /// <summary>国内版端点。</summary>
    public static readonly QoderEndpoints Cn = new(
        QoderRegion.Cn,
        "国内版",
        InferBase: "https://gateway.qoder.com.cn",
        OpenApiBase: "https://openapi.qoder.com.cn",
        WebBase: "https://qoder.cn");

    public static QoderEndpoints For(QoderRegion region) =>
        region == QoderRegion.Cn ? Cn : Global;

    /// <summary>把账号上存的区域字符串解析成枚举。空 / 无法识别一律按国际版处理——
    /// 老库里的账号没有这个字段，必须继续走原来的域名。</summary>
    public static QoderRegion ParseRegion(string? raw) =>
        string.Equals(raw?.Trim(), "cn", StringComparison.OrdinalIgnoreCase)
            ? QoderRegion.Cn
            : QoderRegion.Global;

    /// <summary>解析成枚举后取端点档案。</summary>
    public static QoderEndpoints ForRaw(string? raw) => For(ParseRegion(raw));

    /// <summary>可持久化的区域标识（写进账号记录）。</summary>
    public static string ToStorageValue(QoderRegion region) =>
        region == QoderRegion.Cn ? "cn" : "global";

    // ---------------------------------------------------------------------
    // 推理侧：走 InferBase，请求带 COSY 签名（CosySigner.BuildCosyHeaders）。
    // ---------------------------------------------------------------------

    private const string ChatPath =
        "/algo/api/v2/service/pro/sse/agent_chat_generation?FetchKeys=llm_model_result&AgentId=agent_common";

    /// <summary>聊天端点（WAF 绕过编码版，Encode=1）。</summary>
    public string ChatUrlEncoded => $"{InferBase}{ChatPath}&Encode=1";

    /// <summary>聊天端点（不编码）。</summary>
    public string ChatUrl => $"{InferBase}{ChatPath}";

    /// <summary>模型目录。与排队端点同坑：必须带 /algo 前缀与 Encode=1
    /// （客户端 SDK 常量是 "/api/v2/model/list?Encode=1"，前缀由 WASM 的
    /// prepareRequest 补）。</summary>
    public string ModelListUrl => $"{InferBase}/algo/api/v2/model/list?Encode=1";

    /// <summary>模型排队状态查询。</summary>
    public string QueueStatusUrl => $"{InferBase}/algo/api/v2/service/ask/queue/status";

    // ---------------------------------------------------------------------
    // openapi 侧：走 OpenApiBase，只要 Bearer token，不需要 COSY 签名。
    // ---------------------------------------------------------------------

    /// <summary>jobToken 交换。</summary>
    public string JobTokenExchangeUrl => $"{OpenApiBase}/api/v1/jobToken/exchange";

    /// <summary>设备 jobToken。</summary>
    public string DeviceJobTokenUrl => $"{OpenApiBase}/api/v1/me/jobToken";

    /// <summary>用户状态（额度 / 套餐）。</summary>
    public string UserStatusUrl => $"{OpenApiBase}/api/v3/user/status";

    /// <summary>用户信息。</summary>
    public string UserInfoUrl => $"{OpenApiBase}/api/v1/userinfo";

    /// <summary>设备码轮询。</summary>
    public string DeviceTokenPollUrl => $"{OpenApiBase}/api/v1/deviceToken/poll";

    // ---------------------------------------------------------------------
    // Web 侧：浏览器里打开的页面。
    // ---------------------------------------------------------------------

    /// <summary>设备码登录页（浏览器打开，用户在里面选账号授权）。</summary>
    public string DeviceLoginUrl => $"{WebBase}/device/selectAccounts";

    /// <summary>拼一个 openapi 路径（Credits 等 /sash 接口）。</summary>
    public string OpenApiUrl(string path) => $"{OpenApiBase}{path}";
}
