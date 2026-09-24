using Qoder2Api.Models;
using Qoder2Api.Services.Qoder;

namespace Qoder2Api.Endpoints;

public static class ApiRoutes
{
    public static void MapQoderApiRoutes(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/models", (QoderAuthService auth, HttpContext context) =>
        {
            if (!ValidateApiKey(auth, context))
            {
                return OpenAiErrors.Unauthorized("API Key 无效或缺失。");
            }

            var resp = new ModelListResponse
            {
                Data = QoderConstants.DefaultModels.Select(m => new ModelItem { Id = m }).ToList()
            };
            return Results.Ok(resp);
        });

        app.MapGet("/api/status", (QoderAuthService auth, QoderPool pool) =>
        {
            var active = auth.ActiveAccount;
            var snap = pool.Snapshot();
            return Results.Ok(new
            {
                isConnected = pool.HasUsableAccount(),
                activeAccount = active == null ? null : new
                {
                    id = active.Id,
                    userName = active.UserName,
                    userEmail = active.UserEmail,
                    plan = active.PlanName,
                    authMethod = active.AuthMethod,
                    quota = active.Quota,
                    isQuotaExceeded = active.IsQuotaExceeded,
                    expiresAt = active.ExpiresAt
                },
                requireApiKey = auth.RequireApiKey,
                totalAccounts = auth.Accounts.Count,
                pool = new
                {
                    total = snap.Total,
                    ready = snap.Ready,
                    cooling = snap.Cooling,
                    breaker = snap.Breaker,
                    degraded = snap.Degraded,
                    needsRelogin = snap.NeedsRelogin,
                    disabled = snap.Disabled,
                    servable = snap.Servable
                }
            });
        });
    }

    private static bool ValidateApiKey(QoderAuthService auth, HttpContext context)
    {
        if (!auth.RequireApiKey)
        {
            return true;
        }

        string? token = null;
        string? authHeader = context.Request.Headers.Authorization;
        if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            token = authHeader[7..].Trim();
        }
        else if (context.Request.Headers.TryGetValue("x-api-key", out var xKey))
        {
            token = xKey.ToString().Trim();
        }

        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        return auth.Database.ValidateKey(token);
    }
}
