using Qoder2Api.Models;
using Qoder2Api.Services.Database;
using Qoder2Api.Services.Qoder;

namespace Qoder2Api.Endpoints;

public static class AdminApiRoutes
{
    public static void MapAdminApiRoutes(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/accounts", (QoderAuthService auth) =>
        {
            return Results.Ok(auth.Database.GetAllAccounts());
        });

        /// 号池总览 + 各账号的池状态台账（脱敏：不含任何凭证字段）。
        app.MapGet("/api/pool/status", (QoderPool pool) => Results.Ok(pool.Snapshot()));

        /// 向上游拉取模型目录（倍率 / 是否免费 / 错峰折扣），合并进 models.xml 落盘。
        /// 手动同步会把上游有、XML 里没有的模型一并新增——后台定时同步不会。
        app.MapPost("/api/models/sync", async (ModelCatalogRefresher refresher, CancellationToken ct) =>
        {
            var outcome = await refresher.SyncManualAsync(ct);
            return outcome.Success
                ? Results.Ok(new
                  {
                      success = true,
                      added = outcome.Added,
                      updated = outcome.Updated,
                      fetchedAt = refresher.Catalog.Last.FetchedAt,
                      upstreamCount = refresher.Catalog.Last.Models.Count,
                      path = QoderConstants.ModelConfigPath,
                  })
                : Results.Ok(new { success = false, error = outcome.Error ?? "拉取失败" });
        });

        /// 解冻：清空该账号的全部惩罚状态（冷却/熔断/降权/自动禁用），立刻回池。
        app.MapPost("/api/pool/accounts/{id}/revive", (string id, QoderPool pool) =>
        {
            bool ok = pool.Revive(id);
            return ok
                ? Results.Ok(new { success = true })
                : Results.NotFound(new { success = false, error = "账号不在池中" });
        });

        app.MapPost("/api/accounts/{id}/default", (string id, QoderAuthService auth) =>
        {
            auth.Database.SetDefaultAccount(id);
            auth.NotifyChange();
            return Results.Ok(new { success = true });
        });

        app.MapPost("/api/accounts/{id}/toggle", (string id, QoderAuthService auth) =>
        {
            auth.Database.ToggleAccountStatus(id);
            auth.NotifyChange();
            return Results.Ok(new { success = true });
        });

        app.MapPost("/api/accounts/{id}/refresh", async (string id, QoderAuthService auth, CancellationToken ct) =>
        {
            try
            {
                var status = await auth.RefreshAccountStatusAsync(id, ct);
                return Results.Ok(new { success = true, status });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { success = false, error = ex.Message });
            }
        });

        /// 账号 Credits：余额 / 近一年消耗 / 活跃天数 / 每日福利可领取状态。
        /// force=true 绕过缓存强制刷新。
        app.MapGet("/api/accounts/{id}/credits", async (string id, bool? force, QoderCreditsService credits, CancellationToken ct) =>
        {
            return Results.Ok(await credits.GetAsync(id, force == true, ct));
        });

        /// 批量取全部账号的 Credits（账号列表页一次拿全），返回 accountId → 快照 的映射。
        app.MapGet("/api/credits", async (bool? force, QoderAuthService auth, QoderCreditsService credits, CancellationToken ct) =>
        {
            var ids = auth.Database.GetAllAccounts().Select(a => a.Id);
            return Results.Ok(await credits.GetAllAsync(ids, force == true, ct));
        });

        /// 领取活动福利（「每天领 100 Credits」）。
        /// 对应官方活动页的 POST /sash/api/v1/me/campaigns/{campaignId}/claim，无请求体。
        app.MapPost("/api/accounts/{id}/campaigns/{campaignId}/claim",
            async (string id, string campaignId, QoderCreditsService credits, CancellationToken ct) =>
            {
                var r = await credits.ClaimAsync(id, campaignId, ct);
                return r.Success
                    ? Results.Ok(new { success = true, credits = r.Snapshot })
                    : Results.BadRequest(new { success = false, error = r.Error });
            });

        app.MapDelete("/api/accounts/{id}", (string id, QoderAuthService auth, QoderPool pool, QoderCreditsService credits) =>
        {
            auth.Database.DeleteAccount(id);
            // 一并清理池状态与 Key 绑定：残留的池状态行会被下次同名 id 复用，
            // 而悬空的 Key 绑定会让 UI 显示"（默认活跃账户）"误导用户。
            auth.Database.DeletePoolState(id);
            auth.Database.ClearKeyBindings(id);
            credits.Forget(id); // Credits 缓存同理，否则同 id 复用会显示上一个账号的数据
            pool.SyncAccounts(auth.Database.GetAllAccounts());
            auth.NotifyChange();
            return Results.Ok(new { success = true });
        });

        // --- Models Management ---
        app.MapGet("/api/models", () =>
        {
            return Results.Ok(new
            {
                path = QoderConstants.ModelConfigPath,
                models = QoderConstants.OfficialModels
            });
        });

        app.MapPost("/api/models/reload", () =>
        {
            QoderConstants.ReloadModels();
            return Results.Ok(new
            {
                success = true,
                path = QoderConstants.ModelConfigPath,
                count = QoderConstants.OfficialModels.Count
            });
        });

        // --- OAuth PKCE Device Flow ---
        // region 可选："global"（国际版，默认）/ "cn"（国内版）。
        // 只影响登录页域名（qoder.com / qoder.cn），授权换出来的凭证本身是分区域的。
        app.MapPost("/api/oauth/start", (QoderAuthService auth, string? region) =>
        {
            var state = auth.InitiateDeviceFlow(QoderEndpoints.ParseRegion(region));
            return Results.Ok(new
            {
                verificationUrl = state.VerificationUrl,
                nonce = state.Nonce,
                verifier = state.Verifier,
                machineId = state.MachineId,
                region = QoderEndpoints.ToStorageValue(state.Region)
            });
        });

        app.MapGet("/api/oauth/poll", async (string nonce, string verifier, string machineId, string? region, QoderAuthService auth, CancellationToken ct) =>
        {
            var state = new DeviceFlowState
            {
                Nonce = nonce,
                Verifier = verifier,
                MachineId = machineId,
                Region = QoderEndpoints.ParseRegion(region)
            };
            try
            {
                bool success = await auth.PollDeviceFlowAsync(state, ct);
                return Results.Ok(new { success, pending = !success });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { success = false, error = ex.Message });
            }
        });

        // --- PAT Connect ---
        app.MapPost("/api/accounts/pat", async (ConnectPatRequest req, QoderAuthService auth, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Token))
            {
                return Results.BadRequest(new { error = "PAT 不能为空" });
            }
            try
            {
                // targetAccountId = null：面板新增账号，不是续期。
                await auth.ConnectPatAsync(req.Token, QoderEndpoints.ParseRegion(req.Region), null, ct);
                return Results.Ok(new { success = true });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // --- 导入 cockpit-tools 导出的账号 JSON ---
        // 凭证直给（jobToken 在 auth_user_info_raw.token），不走 /jobToken/exchange。
        // region 必须显式传：导出文件里没有区域字段，而凭证是分区域的，猜错会 401。
        app.MapPost("/api/accounts/import", (ImportAccountsRequest req, QoderAuthService auth) =>
        {
            if (string.IsNullOrWhiteSpace(req.Json))
            {
                return Results.BadRequest(new { error = "导入内容不能为空" });
            }

            QoderAccountImporter.ImportResult res;
            try
            {
                res = auth.ImportFromCockpitJson(req.Json, QoderEndpoints.ParseRegion(req.Region));
            }
            catch (System.Text.Json.JsonException ex)
            {
                return Results.BadRequest(new { error = "JSON 解析失败：" + ex.Message });
            }
            catch (FormatException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            return Results.Ok(new
            {
                success = true,
                res.Added,
                res.Updated,
                res.Skipped,
                res.Total,
                entries = res.Entries.Select(e => new
                {
                    e.UserId,
                    e.UserName,
                    e.Outcome,
                    e.Detail,
                }),
            });
        });

        // --- Proxy API Keys ---
        app.MapGet("/api/keys", (QoderAuthService auth) =>
        {
            return Results.Ok(auth.Database.GetAllKeys());
        });

        app.MapPost("/api/keys", (CreateKeyRequest req, QoderAuthService auth) =>
        {
            auth.Database.CreateKey(req.Name, req.AccountId, req.CustomKey);
            return Results.Ok(new { success = true });
        });

        app.MapPost("/api/keys/{id}/toggle", (string id, QoderAuthService auth) =>
        {
            auth.Database.ToggleKeyStatus(id);
            return Results.Ok(new { success = true });
        });

        app.MapDelete("/api/keys/{id}", (string id, QoderAuthService auth) =>
        {
            auth.Database.DeleteKey(id);
            return Results.Ok(new { success = true });
        });

        // --- Usage Records ---
        app.MapGet("/api/usage", (int? limit, QoderAuthService auth) =>
        {
            return Results.Ok(auth.Database.GetRecentUsage(limit ?? 100));
        });

        app.MapPost("/api/usage/clear", (QoderAuthService auth) =>
        {
            auth.Database.ClearUsageRecords();
            return Results.Ok(new { success = true });
        });

        // --- Settings ---
        app.MapGet("/api/settings", (QoderAuthService auth) =>
        {
            return Results.Ok(new
            {
                requireApiKey = auth.RequireApiKey,
                machineId = auth.MachineId
            });
        });

        app.MapPost("/api/settings", (UpdateSettingsRequest req, QoderAuthService auth) =>
        {
            auth.RequireApiKey = req.RequireApiKey;
            return Results.Ok(new { success = true });
        });
    }

    /// <param name="Region">"global"（国际版，默认）/ "cn"（国内版）。PAT 是分区域的。</param>
    public record ConnectPatRequest(string Token, string? Region = null);

    /// <summary>
    /// 导入 cockpit-tools 导出的账号文件内容（由前端读文件后原样 POST 上来，
    /// 不经过服务端文件系统——面板不该有任意文件读权限）。
    /// </summary>
    public record ImportAccountsRequest(string Json, string? Region = null);
    public record CreateKeyRequest(string Name, string? AccountId = null, string? CustomKey = null);
    public record UpdateSettingsRequest(bool RequireApiKey);
}
