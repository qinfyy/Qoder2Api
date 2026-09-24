using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using reg.Models;
using reg.Services.Database;

namespace reg.Services.Qoder;

public class DeviceFlowState
{
    public string Nonce { get; set; } = "";
    public string Verifier { get; set; } = "";
    public string MachineId { get; set; } = "";
    public string VerificationUrl { get; set; } = "";
}

public class QoderAuthService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly SqliteDbService _db;
    private readonly Lock _lock = new();

    public event Action? OnAuthStateChanged;

    public QoderAuthService(IHttpClientFactory httpFactory, SqliteDbService db)
    {
        _httpFactory = httpFactory;
        _db = db;
    }

    /// <summary>
    /// 每次取用都向工厂要一个新的 HttpClient 包装（底层 handler 由工厂池化复用）。
    /// 本服务是 Singleton，**不能**在构造时捕获 HttpClient 实例——那会把一个
    /// Transient 的 HttpClient 连同其 handler 永久捕获，导致 handler 轮换失效、
    /// DNS 变更不生效。
    /// </summary>
    private HttpClient Http => _httpFactory.CreateClient(QoderHttp.ClientName);

    public SqliteDbService Database => _db;

    public bool IsAuthenticated
    {
        get
        {
            var active = _db.GetActiveAccount();
            return active != null && !string.IsNullOrEmpty(active.UserId) && !string.IsNullOrEmpty(active.JobToken);
        }
    }

    public AccountRecord? ActiveAccount => _db.GetActiveAccount();

    public List<AccountRecord> Accounts => _db.GetAllAccounts();

    public string MachineId
    {
        get
        {
            string? m = _db.GetSetting("machine_id");
            if (string.IsNullOrEmpty(m))
            {
                m = Guid.NewGuid().ToString();
                _db.SetSetting("machine_id", m);
            }
            return m;
        }
    }

    public bool RequireApiKey
    {
        get => _db.GetSetting("require_api_key") == "true";
        set => _db.SetSetting("require_api_key", value ? "true" : "false");
    }

    public void NotifyChange()
    {
        OnAuthStateChanged?.Invoke();
    }

    // --- OAuth PKCE Device Flow ---

    public DeviceFlowState InitiateDeviceFlow()
    {
        byte[] verifierBytes = RandomNumberGenerator.GetBytes(32);
        string verifier = Convert.ToBase64String(verifierBytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        byte[] challengeHash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        string challenge = Convert.ToBase64String(challengeHash)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        string nonce = Guid.NewGuid().ToString();
        string machineId = MachineId;

        string url = $"{QoderConstants.DeviceLoginURL}?challenge={challenge}&challenge_method=S256&machine_id={machineId}&nonce={nonce}";

        return new DeviceFlowState
        {
            Nonce = nonce,
            Verifier = verifier,
            MachineId = machineId,
            VerificationUrl = url
        };
    }

    public async Task<bool> PollDeviceFlowAsync(DeviceFlowState state, CancellationToken ct = default)
    {
        string pollUrl = $"{QoderConstants.DeviceTokenPollURL}?nonce={state.Nonce}&verifier={state.Verifier}&challenge_method=S256";
        using var req = new HttpRequestMessage(HttpMethod.Get, pollUrl);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");

        using var resp = await Http.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound || resp.StatusCode == System.Net.HttpStatusCode.Accepted)
        {
            return false; // Still pending
        }

        if (!resp.IsSuccessStatusCode)
        {
            string err = await resp.Content.ReadAsStringAsync(ct);
            throw new Exception($"Qoder device poll failed ({resp.StatusCode}): {err}");
        }

        string body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        string deviceToken = root.GetProperty("token").GetString()!;
        string userId = root.GetProperty("user_id").GetString()!;
        string? refreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;

        string activeJobToken = deviceToken;
        DateTimeOffset expiresAt = DateTimeOffset.UtcNow.AddDays(30);

        // Fetch User Info
        string? name = null;
        string? email = null;
        try
        {
            using var ureq = new HttpRequestMessage(HttpMethod.Get, QoderConstants.UserInfoURL);
            ureq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", activeJobToken);
            using var uresp = await Http.SendAsync(ureq, ct);
            if (uresp.IsSuccessStatusCode)
            {
                var udoc = JsonDocument.Parse(await uresp.Content.ReadAsStringAsync(ct));
                name = udoc.RootElement.TryGetProperty("name", out var np) ? np.GetString() : null;
                email = udoc.RootElement.TryGetProperty("email", out var ep) ? ep.GetString() : null;
            }
        }
        catch { }

        // Fetch User Status (plan, quota)
        string plan = "Pro";
        double quota = 0;
        bool exceeded = false;
        try
        {
            using var sreq = new HttpRequestMessage(HttpMethod.Get, QoderConstants.UserStatusURL);
            sreq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", activeJobToken);
            using var sresp = await Http.SendAsync(sreq, ct);
            if (sresp.IsSuccessStatusCode)
            {
                var sdoc = JsonDocument.Parse(await sresp.Content.ReadAsStringAsync(ct));
                var sroot = sdoc.RootElement;
                plan = sroot.TryGetProperty("plan", out var pp) ? pp.GetString() ?? "Pro" : "Pro";
                quota = sroot.TryGetProperty("quota", out var qp) ? qp.GetDouble() : 0;
                exceeded = sroot.TryGetProperty("isQuotaExceeded", out var exp2) && exp2.GetBoolean();
            }
        }
        catch { }

        var acc = new AccountRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = userId,
            UserName = name ?? "Qoder User",
            UserEmail = email ?? "",
            PlanName = plan,
            AuthMethod = "device",
            JobToken = activeJobToken,
            DeviceToken = deviceToken,
            RefreshToken = refreshToken,
            ExpiresAt = expiresAt,
            Status = "active",
            Quota = quota,
            IsQuotaExceeded = exceeded,
            // 仅首个账号自动成为首选（号池模式下所有 active 账号都参与选号，
            // 见 ConnectPatAsync 的同款说明）。
            IsDefault = !_db.GetAllAccounts().Any(a => a.IsDefault),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.UpsertAccount(acc);
        if (acc.IsDefault)
        {
            _db.SetDefaultAccount(acc.Id);
        }

        OnAuthStateChanged?.Invoke();
        return true;
    }

    // --- PAT Flow ---

    /// <summary>
    /// 用 PAT 换取 JobToken 并落库。
    ///
    /// <paramref name="targetAccountId"/> 是**续期**路径的关键：非空时表示"更新这条
    /// 已有账号"，为空才是"新增账号"。
    ///
    /// 旧实现在这里有个严重缺陷（已修）：无论新增还是续期，它都
    /// <c>Id = Guid.NewGuid()</c> 造一条新记录，于是
    ///   1. 每续期一次数据库就多一条僵尸账号（带着过期 token，号池还会选中它）；
    ///   2. 调用方紧接着 <c>GetAccountById(旧Id)</c> 拿回的是**旧行、旧 token**，
    ///      本次请求继续用过期凭证——续期等于没做。
    /// 现在改为按 targetAccountId 就地 upsert，并把更新后的记录**返回**给调用方
    /// （调用方不该再查库）。
    /// </summary>
    public async Task<AccountRecord> ConnectPatAsync(string pat, string? targetAccountId = null, CancellationToken ct = default)
    {
        pat = pat.Trim();
        if (string.IsNullOrWhiteSpace(pat))
            throw new ArgumentException("PAT cannot be empty.");

        // 1. Exchange PAT for JobToken
        using var req = new HttpRequestMessage(HttpMethod.Post, QoderConstants.JobTokenExchangeURL);
        req.Content = new StringContent(
            JsonSerializer.Serialize(new { personal_token = pat }),
            Encoding.UTF8,
            "application/json"
        );

        using var resp = await Http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            string err = await resp.Content.ReadAsStringAsync(ct);
            throw new Exception($"PAT exchange failed ({resp.StatusCode}): {err}");
        }

        string body = await resp.Content.ReadAsStringAsync(ct);
        var (jobToken, expiresAt) = ParseJobTokenResponse(body);

        // 2. Fetch User Status with JobToken
        using var sreq = new HttpRequestMessage(HttpMethod.Get, QoderConstants.UserStatusURL);
        sreq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jobToken);
        using var sresp = await Http.SendAsync(sreq, ct);
        sresp.EnsureSuccessStatusCode();

        string sbody = await sresp.Content.ReadAsStringAsync(ct);
        using var sdoc = JsonDocument.Parse(sbody);
        var sroot = sdoc.RootElement;

        string uid = sroot.GetProperty("id").GetString()!;
        string name = sroot.TryGetProperty("name", out var np) ? np.GetString() ?? "" : "";
        string email = sroot.TryGetProperty("email", out var ep) ? ep.GetString() ?? "" : "";
        string plan = sroot.TryGetProperty("plan", out var pp) ? pp.GetString() ?? "Pro" : "Pro";
        double quota = sroot.TryGetProperty("quota", out var qp) ? qp.GetDouble() : 0;
        bool exceeded = sroot.TryGetProperty("isQuotaExceeded", out var exp2) && exp2.GetBoolean();

        // 续期：就地更新已有账号（保留 Id / CreatedAt / IsDefault，不清 LastUsedAt）。
        // 新增：造一条新记录。
        bool isNew = true;
        AccountRecord acc;
        if (!string.IsNullOrEmpty(targetAccountId) && _db.GetAccountById(targetAccountId) is { } existing)
        {
            acc = existing;
            isNew = false;
        }
        else
        {
            acc = new AccountRecord { Id = Guid.NewGuid().ToString("N") };
        }

        acc.UserId = uid;
        acc.UserName = string.IsNullOrWhiteSpace(name) ? (acc.UserName ?? "Qoder PAT User") : name;
        acc.UserEmail = email;
        acc.PlanName = plan;
        acc.AuthMethod = "pat";
        acc.PatToken = pat;
        acc.JobToken = jobToken;
        acc.ExpiresAt = expiresAt;
        acc.Status = "active";
        acc.Quota = quota;
        acc.IsQuotaExceeded = exceeded;
        acc.UpdatedAt = DateTime.UtcNow;

        if (isNew)
        {
            acc.CreatedAt = DateTime.UtcNow;
            // 首个账号才自动成为首选。号池模式下所有 active 账号都参与选号，
            // 「每加一个号就抢走默认标记」只会让 UI 的"当前活跃"来回跳。
            acc.IsDefault = !_db.GetAllAccounts().Any(a => a.IsDefault);
        }

        _db.UpsertAccount(acc);
        if (isNew && acc.IsDefault)
        {
            _db.SetDefaultAccount(acc.Id);
        }

        OnAuthStateChanged?.Invoke();
        return acc;
    }

    private static (string jobToken, DateTimeOffset expiresAt) ParseJobTokenResponse(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        string? token = null;
        if (root.TryGetProperty("token", out var tp)) token = tp.GetString();
        else if (root.TryGetProperty("job_token", out var jtp)) token = jtp.GetString();
        else if (root.TryGetProperty("data", out var dp))
        {
            if (dp.TryGetProperty("token", out var dtp)) token = dtp.GetString();
            else if (dp.TryGetProperty("job_token", out var djtp)) token = djtp.GetString();
        }

        if (string.IsNullOrEmpty(token))
            throw new Exception("Could not find job token in exchange response.");

        long expiresInMs = 86400000;
        if (root.TryGetProperty("expires_in", out var exp)) expiresInMs = exp.GetInt64();
        else if (root.TryGetProperty("data", out var dp2) && dp2.TryGetProperty("expires_in", out var dexp)) expiresInMs = dexp.GetInt64();

        return (token, DateTimeOffset.UtcNow.AddMilliseconds(expiresInMs));
    }

    // --- Credential Resolution ---

    /// <summary>
    /// 取指定账号的签名凭证（号池选完号后调用）。PAT 临近过期时顺带续期。
    ///
    /// 刻意**不做** <c>TouchAccountUsage</c>（更新 accounts.LastUsedAt）：那是每个请求
    /// 一次 SQLite 写事务，在号池的高频路径上是纯负担。最近使用时间由号池在内存里
    /// 跟踪并透出（见 PoolAccountStatus.LastUsedMs），不再写库。
    /// </summary>
    public async Task<CosyCreds> GetCredsForAccountAsync(AccountRecord acc, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(acc.UserId) || string.IsNullOrEmpty(acc.JobToken))
        {
            throw new InvalidOperationException($"账号 {acc.UserName ?? acc.Id} 缺少有效凭证。");
        }

        // PAT 临近过期（10 分钟内）→ 续期。
        // 设备流账号没有续期手段（凭证 30 天过期且上游无 refresh 端点），
        // 过期后由号池的 NeedsRelogin 终态接管，不在这里反复尝试。
        if (acc.ExpiresAt.HasValue && acc.ExpiresAt.Value <= DateTimeOffset.UtcNow.AddMinutes(10))
        {
            if (acc.AuthMethod == "pat" && !string.IsNullOrEmpty(acc.PatToken))
            {
                try
                {
                    // 直接接住返回值——旧实现这里又去 GetAccountById(acc.Id) 查了一次，
                    // 而当时的 ConnectPatAsync 把新 token 写进了**另一条新记录**，
                    // 于是查回来的是旧行、旧 token，续期等于没做。
                    acc = await ConnectPatAsync(acc.PatToken, acc.Id, ct);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[QoderAuthService] PAT refresh failed: {ex.Message}");
                }
            }
            else if (acc.ExpiresAt.Value <= DateTimeOffset.UtcNow)
            {
                throw new InvalidOperationException(
                    $"账号 {acc.UserName ?? acc.Id} 的凭证已过期且无法自动续期，请在管理页重新登录。");
            }
        }

        return new CosyCreds(
            UserID: acc.UserId!,
            AuthToken: acc.JobToken!,
            Name: acc.UserName,
            Email: acc.UserEmail,
            MachineID: MachineId
        );
    }

    /// <summary>
    /// 兼容入口：取「首选账号」的凭证。号池接管路由后，请求路径不再用它
    /// （改走 <see cref="GetCredsForAccountAsync"/>），保留供单账号/管理场景使用。
    /// </summary>
    public async Task<CosyCreds> GetValidCosyCredsAsync(CancellationToken ct = default)
    {
        var acc = _db.GetActiveAccount();
        if (acc == null)
        {
            throw new InvalidOperationException("没有处于活动状态的 Qoder 账户。请前往账户管理页面添加或启用账户。");
        }
        return await GetCredsForAccountAsync(acc, ct);
    }

    // --- Quota & Status Inspection ---

    public async Task<QoderUserStatusResponse> RefreshAccountStatusAsync(string? accountId = null, CancellationToken ct = default)
    {
        var acc = string.IsNullOrEmpty(accountId)
            ? _db.GetActiveAccount()
            : _db.GetAccountById(accountId);

        if (acc == null)
        {
            throw new InvalidOperationException("未找到指定的账户。");
        }

        string? token = acc.JobToken;
        if (string.IsNullOrEmpty(token)) token = acc.DeviceToken;

        if (string.IsNullOrEmpty(token) && acc.AuthMethod == "pat" && !string.IsNullOrEmpty(acc.PatToken))
        {
            acc = await ConnectPatAsync(acc.PatToken, acc.Id, ct);
            token = acc.JobToken;
        }

        if (string.IsNullOrEmpty(token))
        {
            throw new InvalidOperationException("账户未持有有效的凭证令牌。");
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, QoderConstants.UserStatusURL);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await Http.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized && acc.AuthMethod == "pat" && !string.IsNullOrEmpty(acc.PatToken))
        {
            // Try refreshing PAT jobToken
            acc = await ConnectPatAsync(acc.PatToken, acc.Id, ct);
            token = acc.JobToken;

            using var retryReq = new HttpRequestMessage(HttpMethod.Get, QoderConstants.UserStatusURL);
            retryReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            retryReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var retryResp = await Http.SendAsync(retryReq, ct);
            if (retryResp.IsSuccessStatusCode)
            {
                return await ApplyStatusResponseAsync(retryResp, acc, ct);
            }
        }

        if (!resp.IsSuccessStatusCode)
        {
            string err = await resp.Content.ReadAsStringAsync(ct);
            throw new Exception($"刷新账户额度失败 ({resp.StatusCode}): {err}");
        }

        return await ApplyStatusResponseAsync(resp, acc, ct);
    }

    private async Task<QoderUserStatusResponse> ApplyStatusResponseAsync(HttpResponseMessage resp, AccountRecord acc, CancellationToken ct)
    {
        string body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var sroot = doc.RootElement;

        string id = sroot.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? acc.UserId ?? "" : acc.UserId ?? "";
        string name = sroot.TryGetProperty("name", out var nProp) ? nProp.GetString() ?? acc.UserName ?? "" : acc.UserName ?? "";
        string email = sroot.TryGetProperty("email", out var eProp) ? eProp.GetString() ?? acc.UserEmail ?? "" : acc.UserEmail ?? "";
        string userTag = sroot.TryGetProperty("userTag", out var utProp) ? utProp.GetString() ?? "" : "";
        string plan = sroot.TryGetProperty("plan", out var pProp) ? pProp.GetString() ?? acc.PlanName : acc.PlanName;
        double quota = sroot.TryGetProperty("quota", out var qProp) ? qProp.GetDouble() : 0;
        bool exceeded = sroot.TryGetProperty("isQuotaExceeded", out var exProp) && exProp.GetBoolean();
        long nextResetAt = sroot.TryGetProperty("nextResetAt", out var nrProp) ? nrProp.GetInt64() : 0;
        string userType = sroot.TryGetProperty("userType", out var uTypeProp) ? uTypeProp.GetString() ?? "" : "";
        string whitelistStatus = sroot.TryGetProperty("whitelistStatus", out var wProp) ? wProp.GetString() ?? "" : "";

        acc.UserId = id;
        acc.UserName = string.IsNullOrWhiteSpace(name) ? acc.UserName : name;
        acc.UserEmail = string.IsNullOrWhiteSpace(email) ? acc.UserEmail : email;
        acc.PlanName = !string.IsNullOrWhiteSpace(userTag) ? userTag : plan;
        acc.Quota = quota;
        acc.IsQuotaExceeded = exceeded;
        acc.UpdatedAt = DateTime.UtcNow;

        _db.UpsertAccount(acc);
        NotifyChange();

        return new QoderUserStatusResponse
        {
            Id = id,
            Name = name,
            Email = email,
            UserTag = userTag,
            Plan = plan,
            Quota = quota,
            IsQuotaExceeded = exceeded,
            NextResetAt = nextResetAt,
            UserType = userType,
            WhitelistStatus = whitelistStatus
        };
    }
}
