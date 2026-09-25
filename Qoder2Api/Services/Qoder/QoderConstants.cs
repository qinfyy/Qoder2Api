using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace Qoder2Api.Services.Qoder;

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
    // 模型目录。与排队端点同坑：必须带 /algo 前缀与 Encode=1
    //（客户端 SDK 常量 YNA = "/api/v2/model/list?Encode=1"，前缀由 WASM 的 prepareRequest 补）。
    public const string ModelListURL = "https://api3.qoder.sh/algo/api/v2/model/list?Encode=1";
    public const string JobTokenExchangeURL = "https://openapi.qoder.sh/api/v1/jobToken/exchange";
    public const string DeviceJobTokenURL = "https://openapi.qoder.sh/api/v1/me/jobToken";
    /// <summary>
    /// openapi 基址。Credits / 用量 / 活动等 /sash 接口都挂在这个域下。
    /// 这些接口只要 Bearer token，不需要 api3.qoder.sh 那套 COSY 签名。
    /// </summary>
    public const string OpenApiBaseUrl = "https://openapi.qoder.sh";
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

        /// <summary>
        /// 上游下发的计费倍率（/api/v2/model/list 的 price_factor）。
        /// 1 = 标准，&lt;1 = 更便宜。由 /api/models/sync 写入，models.xml 里可手改。
        /// </summary>
        [JsonPropertyName("price_factor")]
        public double? PriceFactor { get; set; }

        /// <summary>上游标记是否免费（is_free）。</summary>
        [JsonPropertyName("is_free")]
        public bool? IsFree { get; set; }

        /// <summary>错峰折扣徽标，如「错峰 4 折」。有值即表示折扣进行中。</summary>
        [JsonPropertyName("promotion_label")]
        public string? PromotionLabel { get; set; }

        /// <summary>折扣时段，如「22:00-08:00」。</summary>
        [JsonPropertyName("promotion_window")]
        public string? PromotionWindow { get; set; }

        /// <summary>最近一次从上游同步的时间（Unix 毫秒），供 UI 展示新鲜度。</summary>
        [JsonPropertyName("synced_at_ms")]
        public long? SyncedAtMs { get; set; }

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

    /// <summary>
    /// 从 models.xml 加载模型表。
    ///
    /// 文件缺失或解析不出内容时**保持为空**，不再回落到硬编码的默认模型表
    /// （那等于把同一份模型清单在 C# 和 XML 里各维护一遍）。空表时所有请求都 404，
    /// 需要管理员在设置页手动「同步上游模型目录」生成。
    /// </summary>
    public static void ReloadModels(ILogger? logger = null)
    {
        lock (ModelLock)
        {
            string path = ModelConfigPath;
            if (!File.Exists(path))
            {
                _officialModels = [];
                logger?.LogWarning("{Path} 不存在，模型表为空——请在设置页手动同步上游模型目录", path);
                return;
            }

            try
            {
                var list = LoadFromXml(path);
                _officialModels = list;
                if (list.Count > 0)
                {
                    logger?.LogInformation("已从 {Path} 加载 {Count} 个模型", path, list.Count);
                }
                else
                {
                    logger?.LogWarning("{Path} 中没有解析出任何模型，模型表为空", path);
                }
            }
            catch (Exception ex)
            {
                _officialModels = [];
                logger?.LogError(ex, "解析 {Path} 失败，模型表为空", path);
            }
        }
    }

    private static List<QoderModelDefinition> LoadFromXml(string path)
    {
        var doc = XDocument.Load(path);
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
                PriceFactor = ParseDouble((string?)el.Element("priceFactor")),
                IsFree = (string?)el.Element("isFree") is { } f ? ParseBool(f) : null,
                PromotionLabel = (string?)el.Element("promotionLabel"),
                PromotionWindow = (string?)el.Element("promotionWindow"),
                SyncedAtMs = ParseLong((string?)el.Element("syncedAtMs")),
            });
        }
        return list;
    }

    /// <summary>
    /// 把上游模型目录合并进 models.xml。两种调用方行为不同：
    ///
    /// - <b>后台定时同步</b>（allowAdd = false）：只刷新已有模型的倍率 / 是否免费 / 错峰折扣。
    ///   模型集合、以及人工维护的 displayName / 描述 / 别名一律不动——自动流程不该
    ///   悄悄改动这个文件的内容。
    /// - <b>管理员手动同步</b>（allowAdd = true）：上游有、XML 里没有的模型一并新增，
    ///   用于首次生成 models.xml，或补上上游新上的模型。描述上游不提供，留空由人工补。
    /// </summary>
    /// <returns>新增与更新的模型数。</returns>
    public static CatalogMergeResult MergeUpstreamCatalog(
        IReadOnlyList<ModelCatalogEntry> upstream, bool allowAdd = false, ILogger? log = null)
    {
        if (upstream.Count == 0)
        {
            return new CatalogMergeResult(0, 0);
        }

        lock (ModelLock)
        {
            var current = OfficialModels;
            var byKey = current.ToDictionary(m => m.Key, StringComparer.Ordinal);
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            int added = 0, updated = 0;
            var skipped = new List<string>();

            foreach (var u in upstream)
            {
                if (string.IsNullOrWhiteSpace(u.Key))
                {
                    continue;
                }

                if (!byKey.TryGetValue(u.Key, out var target))
                {
                    if (!allowAdd)
                    {
                        skipped.Add(u.Key);
                        continue;
                    }

                    // 新增：静态信息取自上游目录；描述上游没有，留空由人工补。
                    target = new QoderModelDefinition
                    {
                        Key = u.Key,
                        DisplayName = string.IsNullOrWhiteSpace(u.DisplayName) ? u.Key : u.DisplayName!,
                        IsVl = u.IsVl ?? false,
                        IsReasoning = u.IsReasoning ?? false,
                        MaxInputTokens = u.MaxInputTokens is > 0 ? u.MaxInputTokens.Value : 128000,
                    };
                    current.Add(target);
                    byKey[u.Key] = target;
                    added++;
                }
                else
                {
                    updated++;
                }

                target.PriceFactor = u.PriceFactor;
                target.IsFree = u.IsFree;
                target.PromotionLabel = u.PromotionLabel;
                target.PromotionWindow = u.PromotionWindow;
                target.SyncedAtMs = now;
            }

            if (skipped.Count > 0)
            {
                log?.LogInformation("上游有 {Count} 个模型不在 models.xml 中，自动同步不新增：{Keys}",
                    skipped.Count, string.Join(", ", skipped));
            }

            try
            {
                SaveToXml(ModelConfigPath, current);
            }
            catch (Exception ex)
            {
                log?.LogWarning(ex, "写回 models.xml 失败（内存已更新，重启后会丢失）");
            }

            return new CatalogMergeResult(added, updated);
        }
    }

    private static double? ParseDouble(string? s) =>
        double.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;

    private static long? ParseLong(string? s) =>
        long.TryParse(s, out var v) && v > 0 ? v : null;

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
                    // 别名默认留空（displayName 已是官方名），空列表不写元素，避免生成 <aliases />
                    m.Aliases.Count > 0
                        ? new XElement("aliases", m.Aliases.Select(a => new XElement("alias", a)))
                        : null,
                    // 动态信息（由 /api/models/sync 从上游写入），无值时不写元素
                    m.PriceFactor is { } pf ? new XElement("priceFactor",
                        pf.ToString(System.Globalization.CultureInfo.InvariantCulture)) : null,
                    m.IsFree is { } free ? new XElement("isFree", free ? "true" : "false") : null,
                    string.IsNullOrWhiteSpace(m.PromotionLabel) ? null : new XElement("promotionLabel", m.PromotionLabel),
                    string.IsNullOrWhiteSpace(m.PromotionWindow) ? null : new XElement("promotionWindow", m.PromotionWindow),
                    m.SyncedAtMs is { } sync ? new XElement("syncedAtMs", sync) : null))));

        doc.Save(path);
    }

    private static bool ParseBool(string? s) =>
        bool.TryParse(s, out var v) && v;

    private static int ParseInt(string? s, int fallback) =>
        int.TryParse(s, out var v) && v > 0 ? v : fallback;

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

    /// <summary>
    /// 把下游传来的模型名解析为已登记的模型定义。只做**精确匹配**
    /// （key / displayName / alias，大小写不敏感），不做任何猜测性兜底：
    /// 匹配不到就是没登记，返回 null，由调用方回 404。
    /// </summary>
    public static QoderModelDefinition? ResolveModel(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return null;
        }

        string raw = modelName.Trim();

        return OfficialModels.FirstOrDefault(m =>
            m.Key.Equals(raw, StringComparison.OrdinalIgnoreCase) ||
            m.DisplayName.Equals(raw, StringComparison.OrdinalIgnoreCase) ||
            m.Aliases.Any(a => a.Equals(raw, StringComparison.OrdinalIgnoreCase)));
    }
}

/// <summary>上游目录合并结果：新增了几个、刷新了几个。</summary>
public readonly record struct CatalogMergeResult(int Added, int Updated)
{
    public int Total => Added + Updated;
}
