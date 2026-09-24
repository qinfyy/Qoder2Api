using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace reg.Services.Qoder;

public static class QoderHttp
{

    public const string ClientName = "qoder";

    public static readonly TimeSpan PrimeTimeout = TimeSpan.FromSeconds(30);

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
    // 模型排队状态查询
    public const string QueueStatusURL = "https://api3.qoder.sh/algo/api/v2/service/ask/queue/status";

    public const string IDEVersion = "1.0.0";
    public const string ClientType = "5";
    public const string DataPolicy = "disagree";
    public const string LoginVersion = "v2";
    public const string MachineOS = "x86_64_windows";
    public const string MachineType = "5";

    public const string RSAPublicKeyXml = """
    <RSAKeyValue>
        <Modulus>wPIjB+XNNi4pa7BEcPbej7+TXOJOj89RGg4nATKXacSnbkmbuTgDalKvHq+BjPeaJgBiDjzofjcdLKbYWANgahs/peh0ZDye0tt+hWc+9yJ/ylbi58CPCSdgm7iWqfJL4XggmaZgFqW/3D8f91a/yeiNe13FvjC/RaAiOgDrzs8=</Modulus>
        <Exponent>AQAB</Exponent>
    </RSAKeyValue>
    """;

    // WAF Bypass Alphabets
    public const string StdAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    public const string CustomAlphabet = "_doRTgHZBKcGVjlvpC,@aFSx#DPuNJme&i*MzLOEn)sUrthbf%Y^w.(kIQyXqWA!";

    // Default Client ID used in official OAuth handshake
    public const string OfficialClientId = "732aef47-9cf2-46a2-95fe-4cebb5d0d1fa";

    public class QoderModelDefinition
    {
        [JsonPropertyName("key")]
        public string Key { get; set; } = "auto";

        [JsonPropertyName("display_name")]
        public string DisplayName { get; set; } = "Auto";

        [JsonPropertyName("description")]
        public string Description { get; set; } = "";

        [JsonPropertyName("is_reasoning")]
        public bool IsReasoning { get; set; } = false;

        [JsonPropertyName("is_vl")]
        public bool IsVl { get; set; } = false;

        [JsonPropertyName("max_input_tokens")]
        public int MaxInputTokens { get; set; } = 128000;

        [JsonPropertyName("aliases")]
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

    private const string ModelConfigFileName = "models.xml";

    public static string ModelConfigPath
    {
        get
        {
            string cwd = Path.Combine(Directory.GetCurrentDirectory(), ModelConfigFileName);
            if (File.Exists(cwd))
                return cwd;

            string save = Path.Combine(Directory.GetCurrentDirectory(), "save", ModelConfigFileName);
            if (File.Exists(save))
                return save;

            string baseDir = Path.Combine(AppContext.BaseDirectory, ModelConfigFileName);
            if (File.Exists(baseDir))
                return baseDir;

            return cwd;
        }
    }

    public static void ReloadModels(ILogger? logger = null)
    {
        lock (ModelLock)
        {
            string path = ModelConfigPath;
            if (File.Exists(path))
            {
                try
                {
                    var list = LoadFromXml(path);
                    if (list is { Count: > 0 })
                    {
                        _officialModels = list;
                        logger?.LogInformation("已从 {Path} 加载 {Count} 个模型", path, list.Count);
                        return;
                    }
                    logger?.LogWarning("{Path} 中没有解析出任何模型，将使用内置默认值", path);
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "解析 {Path} 失败，将使用内置默认值", path);
                }
            }
            else
            {
                logger?.LogInformation("{Path} 不存在，写入内置默认模型表供编辑", path);
            }

            // 回落到内置默认值，并写出一份模板。
            _officialModels = GetBuiltinDefaults();
            try
            {
                string dir = Path.GetDirectoryName(path) ?? "";
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                SaveToXml(path, _officialModels);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "写入 {Path} 失败（不影响运行，仅无法生成模板）", path);
            }
        }
    }

    private static List<QoderModelDefinition> LoadFromXml(string path)
    {
        var doc = System.Xml.Linq.XDocument.Load(path);
        var root = doc.Root;
        if (root is null)
        {
            return [];
        }

        var list = new List<QoderModelDefinition>();
        foreach (var el in root.Elements("model"))
        {
            string key = (string?)el.Attribute("key") ?? "";
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            list.Add(new QoderModelDefinition
            {
                Key = key.Trim(),
                DisplayName = ((string?)el.Element("displayName"))?.Trim() ?? key,
                Description = ((string?)el.Element("description"))?.Trim() ?? "",
                IsReasoning = ParseBool((string?)el.Element("isReasoning")),
                IsVl = ParseBool((string?)el.Element("isVl")),
                MaxInputTokens = ParseInt((string?)el.Element("maxInputTokens"), 128000),
                Aliases = el.Element("aliases")?.Elements("alias")
                    .Select(a => a.Value.Trim())
                    .Where(a => a.Length > 0)
                    .ToList() ?? [],
            });
        }
        return list;
    }

    private static void SaveToXml(string path, List<QoderModelDefinition> models)
    {
        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("models",
                models.Select(m => new XElement("model",
                    new XAttribute("key", m.Key),
                    new XElement("displayName", m.DisplayName),
                    new XElement("description", m.Description),
                    new XElement("isReasoning", m.IsReasoning ? "true" : "false"),
                    new XElement("isVl", m.IsVl ? "true" : "false"),
                    new XElement("maxInputTokens", m.MaxInputTokens),
                    new XElement("aliases", m.Aliases.Select(a => new XElement("alias", a)))))));

        doc.Save(path);
    }

    private static bool ParseBool(string? s) =>
        bool.TryParse(s, out var v) && v;

    private static int ParseInt(string? s, int fallback) =>
        int.TryParse(s, out var v) && v > 0 ? v : fallback;

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

        foreach (var m in OfficialModels)
            Add(m.DisplayName);

        foreach (var m in OfficialModels)
            Add(m.Key);

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

        var match = OfficialModels.FirstOrDefault(m =>
            m.Key.Equals(raw, StringComparison.OrdinalIgnoreCase) ||
            m.DisplayName.Equals(raw, StringComparison.OrdinalIgnoreCase)
        );

        if (match != null)
            return match;

        match = OfficialModels.FirstOrDefault(m =>
            m.Aliases.Any(a => a.Equals(raw, StringComparison.OrdinalIgnoreCase))
        );

        if (match != null)
            return match;

        string norm = NormalizeIdentifier(raw);
        match = OfficialModels.FirstOrDefault(m =>
            NormalizeIdentifier(m.Key) == norm ||
            NormalizeIdentifier(m.DisplayName) == norm ||
            m.Aliases.Any(a => NormalizeIdentifier(a) == norm)
        );

        if (match != null)
            return match;

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
