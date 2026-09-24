namespace reg.Services.Qoder;

/// <summary>出站 HTTP 客户端的共享约定。</summary>
public static class QoderHttp
{
    /// <summary>
    /// 具名 HttpClient 的名称（Program.cs 注册、各服务 CreateClient 时引用）。
    /// 该客户端的 Timeout 是 <see cref="Timeout.InfiniteTimeSpan"/>，超时一律由
    /// QoderStreamSession 的首字节/读空闲超时管理——SSE 流不能被总时长限制。
    /// </summary>
    public const string ClientName = "qoder";

    /// <summary>首字节超时：连首个事件都拿不到就换号（账号级故障多在首包暴露）。</summary>
    public static readonly TimeSpan PrimeTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 读空闲超时：连续这么久没有任何数据即放弃。防上游静默挂死——客户端断连后
    /// 若上游既不发数据也不断开，仅靠 ct 取消不保证读操作立刻返回。
    /// </summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(120);
}

public static class QoderConstants
{
    // Endpoints
    public const string ChatURLEncoded = "https://api3.qoder.sh/algo/api/v2/service/pro/sse/agent_chat_generation?FetchKeys=llm_model_result&AgentId=agent_common&Encode=1";
    public const string ChatURL = "https://api3.qoder.sh/algo/api/v2/service/pro/sse/agent_chat_generation?FetchKeys=llm_model_result&AgentId=agent_common";
    public const string ModelListURL = "https://api3.qoder.sh/algo/api/v2/model/list";
    public const string JobTokenExchangeURL = "https://openapi.qoder.sh/api/v1/jobToken/exchange";
    public const string DeviceJobTokenURL = "https://openapi.qoder.sh/api/v1/me/jobToken";
    public const string UserStatusURL = "https://openapi.qoder.sh/api/v3/user/status";
    public const string UserInfoURL = "https://openapi.qoder.sh/api/v1/userinfo";
    public const string DeviceLoginURL = "https://qoder.com/device/selectAccounts";
    public const string DeviceTokenPollURL = "https://openapi.qoder.sh/api/v1/deviceToken/poll";

    // COSY header fingerprint constants
    public const string IDEVersion = "1.0.0";
    public const string ClientType = "5";
    public const string DataPolicy = "disagree";
    public const string LoginVersion = "v2";
    public const string MachineOS = "x86_64_windows";
    public const string MachineType = "5";

    // RSA Public Key for COSY encryption (Extracted from official client)
    public const string RSAPublicKeyPEM = """
    -----BEGIN PUBLIC KEY-----
    MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQDA8iMH5c02LilrsERw9t6Pv5Nc
    4k6Pz1EaDicBMpdpxKduSZu5OANqUq8er4GM95omAGIOPOh+Nx0spthYA2BqGz+l
    6HRkPJ7S236FZz73In/KVuLnwI8JJ2CbuJap8kvheCCZpmAWpb/cPx/3Vr/J6I17
    XcW+ML9FoCI6AOvOzwIDAQAB
    -----END PUBLIC KEY-----
    """;

    // WAF Bypass Alphabets
    public const string StdAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    public const string CustomAlphabet = "_doRTgHZBKcGVjlvpC,@aFSx#DPuNJme&i*MzLOEn)sUrthbf%Y^w.(kIQyXqWA!";

    // Default Client ID used in official OAuth handshake
    public const string OfficialClientId = "732aef47-9cf2-46a2-95fe-4cebb5d0d1fa";

    public class QoderModelDefinition
    {
        [System.Text.Json.Serialization.JsonPropertyName("key")]
        public string Key { get; set; } = "auto";

        [System.Text.Json.Serialization.JsonPropertyName("display_name")]
        public string DisplayName { get; set; } = "Auto";

        [System.Text.Json.Serialization.JsonPropertyName("description")]
        public string Description { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("is_reasoning")]
        public bool IsReasoning { get; set; } = false;

        [System.Text.Json.Serialization.JsonPropertyName("is_vl")]
        public bool IsVl { get; set; } = false;

        [System.Text.Json.Serialization.JsonPropertyName("max_input_tokens")]
        public int MaxInputTokens { get; set; } = 128000;

        [System.Text.Json.Serialization.JsonPropertyName("aliases")]
        public List<string> Aliases { get; set; } = [];

        public QoderModelDefinition() { }

        public QoderModelDefinition(string key, string displayName, string description, bool isReasoning, bool isVl, int maxInputTokens, List<string> aliases)
        {
            Key = key;
            DisplayName = displayName;
            Description = description;
            IsReasoning = isReasoning;
            IsVl = isVl;
            MaxInputTokens = maxInputTokens;
            Aliases = aliases;
        }
    }

    private static readonly Lock ModelLock = new();
    private static List<QoderModelDefinition>? _officialModels;

    public static List<QoderModelDefinition> OfficialModels
    {
        get
        {
            if (_officialModels == null)
            {
                ReloadModels();
            }
            return _officialModels ?? [];
        }
    }

    public static string ModelConfigPath
    {
        get
        {
            string cwdJson = Path.Combine(Directory.GetCurrentDirectory(), "models.json");
            if (File.Exists(cwdJson)) return cwdJson;

            string saveJson = Path.Combine(Directory.GetCurrentDirectory(), "save", "models.json");
            if (File.Exists(saveJson)) return saveJson;

            string baseJson = Path.Combine(AppContext.BaseDirectory, "models.json");
            if (File.Exists(baseJson)) return baseJson;

            return cwdJson;
        }
    }

    public static void ReloadModels()
    {
        lock (ModelLock)
        {
            string path = ModelConfigPath;
            if (File.Exists(path))
            {
                try
                {
                    string json = File.ReadAllText(path, System.Text.Encoding.UTF8);
                    var list = System.Text.Json.JsonSerializer.Deserialize<List<QoderModelDefinition>>(json, new System.Text.Json.JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                    if (list != null && list.Count > 0)
                    {
                        _officialModels = list;
                        Console.WriteLine($"[QoderConstants] Loaded {_officialModels.Count} models from {path}");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[QoderConstants] Error loading models from {path}: {ex.Message}");
                }
            }

            // Fallback to builtin defaults and write out to path
            _officialModels = GetBuiltinDefaults();
            try
            {
                string dir = Path.GetDirectoryName(path) ?? "";
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                string formatted = System.Text.Json.JsonSerializer.Serialize(_officialModels, new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });
                File.WriteAllText(path, formatted, System.Text.Encoding.UTF8);
            }
            catch { }
        }
    }

    public static List<QoderModelDefinition> GetBuiltinDefaults() =>
    [
        new("qmodel_38max", "Qwen3.8-Max", "千问最新一代基座模型，2.4 万亿参数，在代码工程、专业办公、深度推理等核心场景全面领先", true, true, 180000, ["qwen3.8-max", "qwen-3.8-max", "qwen3.8max", "qwen-max", "qwen-max-latest"]),
        new("qfmodel", "Qwen3.8-Flash", "千问开源权重的多模态 MoE 模型，在能力、延迟与成本间取得出色平衡", true, true, 180000, ["qwen3.8-flash", "qwen-3.8-flash", "qwen3.8flash", "qwen-flash", "qwen-flash-latest"]),
        new("qmodel_latest", "Qwen3.7-Max", "千问旗舰模型，具备顶尖智能体执行能力，可自主完成长达 35 小时的复杂任务", true, true, 180000, ["qwen3.7-max", "qwen-3.7-max", "qwen3.7max"]),
        new("qmodel", "Qwen3.7-Plus", "千问旗舰模型，增强推理和智能体能力，擅长编程与复杂问题解决", true, true, 180000, ["qwen3.7-plus", "qwen-3.7-plus", "qwen3.7plus", "qwen-plus", "qwen"]),
        new("dmodel", "DeepSeek-V4-Pro", "深度求索正式版模型（DeepSeek-V4-Pro-0813），Agent 能力、世界知识与推理性能全面领先。", true, false, 128000, ["deepseek-v4-pro", "deepseek-r1", "deepseek-reasoner", "deepseek-v4", "r1"]),
        new("dfmodel", "DeepSeek-V4-Flash", "深度求索正式版模型（DeepSeek-V4-Flash-0731），Agent 能力、世界知识与推理性能全面领先。", false, false, 128000, ["deepseek-v4-flash", "deepseek-v3", "deepseek-chat", "v3"]),
        new("kmodel_latest", "Kimi-K3", "Kimi 迄今最强模型：2.8 万亿参数，面向软件工程、知识工作与深度推理而生", true, false, 200000, ["kimi-k3", "kimi-latest"]),
        new("kmodel", "Kimi-K2.7-Code", "专为长上下文编程打造：精准遵循指令，可靠执行长链路任务", false, false, 200000, ["kimi-k2.7-code", "kimi-k2.7", "kimi-k2.5", "kimi"]),
        new("gmodel", "GLM-5.3", "智谱旗舰模型，擅长复杂系统工程与长程任务", true, false, 128000, ["glm-5.3", "glm-5", "glm-4", "glm"]),
        new("gfmodel", "GLM-5.3-Flash", "智谱全新原生多模态模型，深度理解图像与视频，自主完成研究分析、文档制作等复杂任务", false, true, 128000, ["glm-5.3-flash", "glm-flash"]),
        new("mmodel", "MiniMax-M3", "原生多模态感知、前沿编码能力与 1M 上下文，驾驭高复杂度工作流", true, true, 1000000, ["minimax-m3", "minimax", "minimax-m2.5"]),
        new("cmodel", "Cantus", "尝鲜体验全球顶级模型，擅长超长自主任务执行", true, false, 128000, ["cantus", "cmodel"]),
        new("auto", "Auto", "智能选择最适合的模型，平衡性能与成本", false, true, 128000, ["default", "qoder"]),
        new("ultimate", "Ultimate", "专家级深度推理与思考能力，极致输出质量。", true, false, 128000, ["极致", "qoder-ultimate"]),
        new("performance", "Performance", "高级推理能力，高质量输出", false, false, 128000, ["性能", "qoder-performance"]),
        new("efficient", "Efficient", "标准推理能力，高性价比", false, false, 128000, ["经济", "qoder-efficient"]),
        new("lite", "Lite", "基础推理能力，免费使用（高峰期可能响应较慢）", false, false, 128000, ["轻量", "qoder-lite"])
    ];

    public static string[] DefaultModels => GetAllModelIds().ToArray();

    public static List<string> GetAllModelIds()
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? id)
        {
            if (!string.IsNullOrWhiteSpace(id) && seen.Add(id))
            {
                list.Add(id);
            }
        }

        // 1. Official Display Names
        foreach (var m in OfficialModels) Add(m.DisplayName);
        // 2. Official Keys
        foreach (var m in OfficialModels) Add(m.Key);
        // 3. Lowercase aliases
        foreach (var m in OfficialModels)
        {
            foreach (var alias in m.Aliases) Add(alias);
        }

        return list;
    }

    public static QoderModelDefinition ResolveModel(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return OfficialModels.First(m => m.Key == "auto");
        }

        string raw = modelName.Trim();
        if (raw.StartsWith("qoder/", StringComparison.OrdinalIgnoreCase))
        {
            raw = raw[6..].Trim();
        }

        // 1. Direct match with Key or DisplayName
        var match = OfficialModels.FirstOrDefault(m =>
            m.Key.Equals(raw, StringComparison.OrdinalIgnoreCase) ||
            m.DisplayName.Equals(raw, StringComparison.OrdinalIgnoreCase)
        );
        if (match != null) return match;

        // 2. Direct match with Aliases
        match = OfficialModels.FirstOrDefault(m =>
            m.Aliases.Any(a => a.Equals(raw, StringComparison.OrdinalIgnoreCase))
        );
        if (match != null) return match;

        // 3. Normalized alphanumeric match (ignore '-', '_', '.', space)
        string norm = NormalizeIdentifier(raw);
        match = OfficialModels.FirstOrDefault(m =>
            NormalizeIdentifier(m.Key) == norm ||
            NormalizeIdentifier(m.DisplayName) == norm ||
            m.Aliases.Any(a => NormalizeIdentifier(a) == norm)
        );
        if (match != null) return match;

        // 4. Prefix & heuristic mapping
        string lower = raw.ToLowerInvariant();
        if (lower.Contains("3.8-max") || lower.Contains("38max") || lower.Contains("3.8_max"))
            return OfficialModels.First(m => m.Key == "qmodel_38max");

        if (lower.Contains("3.8-flash") || lower.Contains("38flash") || lower.Contains("3.8_flash"))
            return OfficialModels.First(m => m.Key == "qfmodel");

        if (lower.Contains("3.7-max") || lower.Contains("37max") || lower.Contains("3.7_max"))
            return OfficialModels.First(m => m.Key == "qmodel_latest");

        if (lower.Contains("3.7-plus") || lower.Contains("37plus") || lower.Contains("3.7_plus"))
            return OfficialModels.First(m => m.Key == "qmodel");

        if (lower.StartsWith("qwen") || lower.StartsWith("qwq"))
            return OfficialModels.First(m => m.Key == "qmodel_38max");

        if (lower.Contains("r1") || lower.Contains("reasoner") || lower.Contains("v4-pro") || lower.Contains("v4pro"))
            return OfficialModels.First(m => m.Key == "dmodel");

        if (lower.Contains("v4-flash") || lower.Contains("v4flash") || lower.Contains("v3") || lower.Contains("chat"))
            return OfficialModels.First(m => m.Key == "dfmodel");

        if (lower.StartsWith("deepseek"))
            return OfficialModels.First(m => m.Key == "dmodel");

        if (lower.Contains("k3"))
            return OfficialModels.First(m => m.Key == "kmodel_latest");

        if (lower.StartsWith("kimi") || lower.StartsWith("moonshot"))
            return OfficialModels.First(m => m.Key == "kmodel");

        if (lower.Contains("flash") && (lower.StartsWith("glm") || lower.Contains("zhipu")))
            return OfficialModels.First(m => m.Key == "gfmodel");

        if (lower.StartsWith("glm") || lower.Contains("chatglm") || lower.Contains("zhipu"))
            return OfficialModels.First(m => m.Key == "gmodel");

        if (lower.StartsWith("minimax"))
            return OfficialModels.First(m => m.Key == "mmodel");

        if (lower.Contains("cantus"))
            return OfficialModels.First(m => m.Key == "cmodel");

        if (lower.Contains("claude") || lower.Contains("gpt") || lower.Contains("o1") || lower.Contains("o3"))
            return OfficialModels.First(m => m.Key == "qmodel_38max");

        return OfficialModels.First(m => m.Key == "auto");
    }

    private static string NormalizeIdentifier(string input)
    {
        return new string(input.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }
}
