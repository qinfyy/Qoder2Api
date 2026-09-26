using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Qoder2Api.Models;
using Qoder2Api.Services.Database;

namespace Qoder2Api.Services.Qoder;

public class DeviceFlowState
{
    public string Nonce { get; set; } = "";
    public string Verifier { get; set; } = "";
    public string MachineId { get; set; } = "";
    public string VerificationUrl { get; set; } = "";

    /// <summary>本次登录针对的区域。轮询与落库都按它走（国际版 / 国内版域名不同）。</summary>
    public QoderRegion Region { get; set; } = QoderRegion.Global;
}

public class QoderAuthService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly SqliteDbService _db;
    private readonly ILogger<QoderAuthService> _log;
    private readonly Lock _lock = new();

    public event Action? OnAuthStateChanged;

    public QoderAuthService(IHttpClientFactory httpFactory, SqliteDbService db, ILogger<QoderAuthService> log)
    {
        _httpFactory = httpFactory;
        _db = db;
        _log = log;
    }
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

    public DeviceFlowState InitiateDeviceFlow(QoderRegion region = QoderRegion.Global)
    {
        var ep = QoderEndpoints.For(region);

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

        string url = $"{ep.DeviceLoginUrl}?challenge={challenge}&challenge_method=S256&machine_id={machineId}&nonce={nonce}";

        return new DeviceFlowState
        {
            Nonce = nonce,
            Verifier = verifier,
            MachineId = machineId,
            VerificationUrl = url,
            Region = region
        };
    }

    public async Task<bool> PollDeviceFlowAsync(DeviceFlowState state, CancellationToken ct = default)
    {
        var endpoints = QoderEndpoints.For(state.Region);
        string pollUrl = $"{endpoints.DeviceTokenPollUrl}?nonce={state.Nonce}&verifier={state.Verifier}&challenge_method=S256";
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
        string? phone = null;
        try
        {
            using var ureq = new HttpRequestMessage(HttpMethod.Get, endpoints.UserInfoUrl);
            ureq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", activeJobToken);
            using var uresp = await Http.SendAsync(ureq, ct);
            if (uresp.IsSuccessStatusCode)
            {
                var udoc = JsonDocument.Parse(await uresp.Content.ReadAsStringAsync(ct));
                var uroot = udoc.RootElement;
                name = FirstString(uroot, "name", "username", "user_name");
                email = FirstString(uroot, "email");
                // 手机号就在同一个响应里（字段名 security_mobile，官方客户端 fetchUser 同源）。
                // 国内版账号多为手机号注册、没有邮箱，列表页靠这个字段兜底显示。
                phone = FirstString(uroot, "security_mobile");
            }
        }
        catch { }

        // Fetch User Status (plan, quota)
        string plan = QoderConstants.UnknownPlan;
        double quota = 0;
        bool exceeded = false;
        try
        {
            using var sreq = new HttpRequestMessage(HttpMethod.Get, endpoints.UserStatusUrl);
            sreq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", activeJobToken);
            using var sresp = await Http.SendAsync(sreq, ct);
            if (sresp.IsSuccessStatusCode)
            {
                var sdoc = JsonDocument.Parse(await sresp.Content.ReadAsStringAsync(ct));
                var sroot = sdoc.RootElement;
                plan = sroot.TryGetProperty("plan", out var pp) ? NonEmpty(pp.GetString()) : QoderConstants.UnknownPlan;
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
            UserPhone = phone,
            PlanName = plan,
            AuthMethod = "device",
            Region = QoderEndpoints.ToStorageValue(state.Region),
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

    public async Task<AccountRecord> ConnectPatAsync(string pat, QoderRegion region = QoderRegion.Global, string? targetAccountId = null, CancellationToken ct = default)
    {
        pat = pat.Trim();
        if (string.IsNullOrWhiteSpace(pat))
            throw new ArgumentException("PAT cannot be empty.");

        var endpoints = QoderEndpoints.For(region);

        // 1. Exchange PAT for JobToken
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoints.JobTokenExchangeUrl);
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
        using var sreq = new HttpRequestMessage(HttpMethod.Get, endpoints.UserStatusUrl);
        sreq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jobToken);
        using var sresp = await Http.SendAsync(sreq, ct);
        sresp.EnsureSuccessStatusCode();

        string sbody = await sresp.Content.ReadAsStringAsync(ct);
        using var sdoc = JsonDocument.Parse(sbody);
        var sroot = sdoc.RootElement;

        string uid = sroot.GetProperty("id").GetString()!;
        string name = sroot.TryGetProperty("name", out var np) ? np.GetString() ?? "" : "";
        string email = sroot.TryGetProperty("email", out var ep) ? ep.GetString() ?? "" : "";
        string plan = sroot.TryGetProperty("plan", out var pp) ? NonEmpty(pp.GetString()) : QoderConstants.UnknownPlan;
        double quota = sroot.TryGetProperty("quota", out var qp) ? qp.GetDouble() : 0;
        bool exceeded = sroot.TryGetProperty("isQuotaExceeded", out var exp2) && exp2.GetBoolean();

        // 手机号只在 /api/v1/userinfo 里（user/status 不返回），单独取一次。
        // 失败不影响登录：国内版靠它兜底显示，国际版本来也没有这个字段。
        string? phone = null;
        try
        {
            using var ureq = new HttpRequestMessage(HttpMethod.Get, endpoints.UserInfoUrl);
            ureq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jobToken);
            using var uresp = await Http.SendAsync(ureq, ct);
            if (uresp.IsSuccessStatusCode)
            {
                using var udoc = JsonDocument.Parse(await uresp.Content.ReadAsStringAsync(ct));
                phone = FirstString(udoc.RootElement, "security_mobile");
            }
        }
        catch { }

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
        // 只在取到新值时覆盖，避免上游临时不给该字段时把已存的手机号抹掉。
        if (!string.IsNullOrWhiteSpace(phone))
        {
            acc.UserPhone = phone;
        }
        acc.PlanName = plan;
        acc.AuthMethod = "pat";
        acc.Region = QoderEndpoints.ToStorageValue(region);
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

    private static string? FirstString(JsonElement root, params string[] keys)
    {
        foreach (string key in keys)
        {
            if (root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            {
                string? s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    return s.Trim();
                }
            }
        }
        return null;
    }

    private static string NonEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? QoderConstants.UnknownPlan : value;

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

    public QoderAccountImporter.ImportResult ImportFromCockpitJson(string json, QoderRegion region)
    {
        var parsed = QoderAccountImporter.Parse(json, region);
        var entries = new List<QoderAccountImporter.EntryResult>();
        int added = 0, updated = 0, skipped = 0;

        foreach (var acc in parsed)
        {
            if (string.IsNullOrWhiteSpace(acc.UserId))
            {
                skipped++;
                entries.Add(new("", acc.UserName, "跳过", "缺少 user_id，无法去重"));
                continue;
            }

            var existing = _db.GetAllAccounts().FirstOrDefault(a =>
                string.Equals(a.UserId, acc.UserId, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                existing.UserName = acc.UserName;
                existing.UserEmail = acc.UserEmail;
                existing.UserPhone = acc.UserPhone;
                existing.PlanName = acc.PlanName;
                existing.JobToken = acc.JobToken;
                existing.RefreshToken = acc.RefreshToken;
                existing.ExpiresAt = acc.ExpiresAt;
                existing.Quota = acc.Quota;
                existing.IsQuotaExceeded = acc.IsQuotaExceeded;
                existing.Region = acc.Region;
                existing.UpdatedAt = DateTime.UtcNow;
                _db.UpsertAccount(existing);
                updated++;
                entries.Add(new(acc.UserId, acc.UserName, "更新", "已刷新该账号的凭证"));
            }
            else
            {
                acc.IsDefault = !_db.GetAllAccounts().Any(a => a.IsDefault);
                _db.UpsertAccount(acc);
                if (acc.IsDefault)
                {
                    _db.SetDefaultAccount(acc.Id);
                }
                added++;
                entries.Add(new(acc.UserId, acc.UserName, "新增", acc.Region));
            }
        }

        NotifyChange();
        return new QoderAccountImporter.ImportResult(added, updated, skipped, entries);
    }

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
                    acc = await ConnectPatAsync(acc.PatToken, QoderEndpoints.ParseRegion(acc.Region), acc.Id, ct);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "PAT 续期失败，本次继续用旧凭证");
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
            MachineID: MachineId,
            // 区域随凭证一起传递：聊天 / 模型目录 / 排队三处都从 creds.Endpoints 取域名。
            Region: QoderEndpoints.ParseRegion(acc.Region)
        );
    }

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
            acc = await ConnectPatAsync(acc.PatToken, QoderEndpoints.ParseRegion(acc.Region), acc.Id, ct);
            token = acc.JobToken;
        }

        if (string.IsNullOrEmpty(token))
        {
            throw new InvalidOperationException("账户未持有有效的凭证令牌。");
        }

        // 用户状态接口按账号区域取（国际版 openapi.qoder.sh / 国内版 openapi.qoder.com.cn）。
        var endpoints = QoderEndpoints.ForRaw(acc.Region);

        using var req = new HttpRequestMessage(HttpMethod.Get, endpoints.UserStatusUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await Http.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized && acc.AuthMethod == "pat" && !string.IsNullOrEmpty(acc.PatToken))
        {
            // Try refreshing PAT jobToken
            acc = await ConnectPatAsync(acc.PatToken, QoderEndpoints.ParseRegion(acc.Region), acc.Id, ct);
            token = acc.JobToken;

            using var retryReq = new HttpRequestMessage(HttpMethod.Get, endpoints.UserStatusUrl);
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
