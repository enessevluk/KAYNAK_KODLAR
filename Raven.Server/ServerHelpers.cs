using System.Net;
using System.Security.Cryptography;
using System.Text;

static class ServerHelpers
{
    public static string ResolveRavenPath(string root, string? configured, string fallback)
    {
        var value = string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim();
        return Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(root, value));
    }

    public static string GetExternalBaseUrl(HttpContext context, ServerRuntime runtime)
    {
        // Development/test modunda sabit config yerine gercek istegin scheme+host bilgisini kullan.
        // Boylece Kestrel HTTP dinlerken stale bir https:// PublicBaseUrl Steam login/callback'i bozmaz.
        if (runtime.IsDevelopment)
        {
            var scheme = context.Request.Scheme;
            var host = context.Request.Host.Value;
            if ((scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                 scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) &&
                !string.IsNullOrWhiteSpace(host))
                return $"{scheme}://{host}".TrimEnd('/');
        }

        return runtime.PublicBaseUrl.TrimEnd('/');
    }

    public static object ApiError(string code, string message) => new { ok = false, code, message };
    public static ApiProblemResponse ApiError(ServerRuntime runtime, string code, LicenseRecord? license = null, string? message = null) =>
        UserIssueCatalog.Create(runtime, code, license, message);
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static int CompareVersions(string a, string b)
    {
        if (!Version.TryParse(NormalizeVersion(a), out var av)) av = new Version(0, 0);
        if (!Version.TryParse(NormalizeVersion(b), out var bv)) bv = new Version(0, 0);
        return av.CompareTo(bv);
    }

    public static string NormalizeVersion(string value)
    {
        var clean = new string((value ?? "").TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()).Trim('.');
        return string.IsNullOrWhiteSpace(clean) ? "0.0" : clean;
    }

    public static string SteamResultPage(bool ok, string message)
    {
        var html = """
<!doctype html>
<html lang='tr'>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width,initial-scale=1'>
<title>Raven Steam</title>
<style>
*{box-sizing:border-box}body{font-family:Segoe UI,Arial,sans-serif;background:#080b10;color:#eef2f5;display:grid;place-items:center;min-height:100vh;margin:0;padding:24px}.c{width:min(560px,100%);background:linear-gradient(180deg,#121922,#0e141c);border:1px solid #273440;border-radius:16px;padding:30px;box-shadow:0 24px 80px rgba(0,0,0,.35)}.mark{width:44px;height:44px;border-radius:10px;background:#e65a2d;display:grid;place-items:center;font-weight:800;margin-bottom:18px}.eyebrow{font-size:11px;color:#f29a48;font-weight:700;letter-spacing:.08em}h1{color:__COLOR__;font-size:27px;margin:7px 0 10px}p{color:#b9c4cf;line-height:1.65;margin:0}.hint{margin-top:18px;padding:11px 12px;border-radius:9px;background:#0b1219;border:1px solid #24313d;color:#7f91a0;font-size:12px}
</style>
</head>
<body>
<div class='c'><div class='mark'>R</div><div class='eyebrow'>RAVEN MAP PANEL</div><h1>__HEADING__</h1><p>__MESSAGE__</p><div class='hint'>Raven uygulaması doğrulamayı otomatik algılar. Tarayıcı sekmesi kapanmazsa güvenle kapatabilirsiniz.</div></div>
<script>setTimeout(function(){try{window.open('','_self');window.close();}catch(e){}},900);</script>
</body>
</html>
""";
        return html
            .Replace("__COLOR__", ok ? "#5fd39a" : "#f06464", StringComparison.Ordinal)
            .Replace("__HEADING__", ok ? "Steam doğrulandı" : "Doğrulama başarısız", StringComparison.Ordinal)
            .Replace("__MESSAGE__", WebUtility.HtmlEncode(message), StringComparison.Ordinal);
    }
}
