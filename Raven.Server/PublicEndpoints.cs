using System.Text;
using static ServerHelpers;

static class PublicEndpoints
{
    public static void Map(WebApplication app, ServerRuntime r)
    {
        app.MapGet("/", () => Results.Redirect("/admin"));
        app.MapGet("/health", (HttpContext ctx) => Results.Ok(new
        {
            ok = true,
            service = "Raven.Server",
            instanceId = r.ServerInstanceId,
            version = r.ServerVersion,
            buildId = BuildStamp.Id,
            builtUtc = BuildStamp.BuiltUtc,
            environment = r.IsDevelopment ? "Development" : "Production",
            authBaseUrl = GetExternalBaseUrl(ctx, r),
            utc = DateTimeOffset.UtcNow
        }));

        var steam = app.MapGroup("/auth/steam");
        steam.MapGet("", (HttpContext ctx, string session) =>
        {
            var auth = r.Db.GetAuthSession(session);
            if (auth is null || auth.ExpiresUtc < DateTimeOffset.UtcNow || auth.UsedUtc is not null)
                return Results.BadRequest("Raven Steam oturumu bulunamadı, kullanıldı veya süresi doldu.");
            var externalBase = GetExternalBaseUrl(ctx, r);
            return Results.Redirect(r.Steam.BuildLoginUrl(session, externalBase));
        });

        steam.MapGet("/callback", async (HttpContext ctx, string session, CancellationToken ct) =>
        {
            var auth = r.Db.GetAuthSession(session);
            if (auth is null || auth.ExpiresUtc < DateTimeOffset.UtcNow || auth.UsedUtc is not null)
                return Results.Content(SteamResultPage(false, "Oturum kullanılmış veya süresi dolmuş. Raven Map Panel'e dönüp tekrar deneyin."), "text/html", Encoding.UTF8);
            var steamId = await r.Steam.ValidateCallbackAsync(ctx.Request.Query, ct);
            if (steamId is null)
                return Results.Content(SteamResultPage(false, "Steam doğrulaması başarısız oldu."), "text/html", Encoding.UTF8);
            r.Db.MarkAuthSessionVerified(session, steamId);
            return Results.Content(SteamResultPage(true, $"Steam hesabı doğrulandı. SteamID64: {steamId}. Raven Map Panel otomatik olarak devam ediyor."), "text/html", Encoding.UTF8);
        });
    }
}
