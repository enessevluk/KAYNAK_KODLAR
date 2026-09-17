using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;

sealed class SteamOpenIdService(IHttpClientFactory httpFactory)
{
    private const string Endpoint = "https://steamcommunity.com/openid/login";
    public string BuildLoginUrl(string session, string publicBase)
    {
        publicBase = (publicBase ?? "").Trim().TrimEnd('/');
        if (!Uri.TryCreate(publicBase, UriKind.Absolute, out var publicUri) ||
            (publicUri.Scheme != Uri.UriSchemeHttp && publicUri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("Steam public base URL gecersiz.");

        var callback = $"{publicBase}/auth/steam/callback?session={Uri.EscapeDataString(session)}";
        var q = new Dictionary<string, string>
        {
            ["openid.ns"] = "http://specs.openid.net/auth/2.0",
            ["openid.mode"] = "checkid_setup",
            ["openid.return_to"] = callback,
            ["openid.realm"] = publicBase + "/",
            ["openid.identity"] = "http://specs.openid.net/auth/2.0/identifier_select",
            ["openid.claimed_id"] = "http://specs.openid.net/auth/2.0/identifier_select"
        };
        return Endpoint + "?" + string.Join("&", q.Select(x => Uri.EscapeDataString(x.Key) + "=" + Uri.EscapeDataString(x.Value)));
    }

    public async Task<string?> ValidateCallbackAsync(IQueryCollection query, CancellationToken ct)
    {
        var values = query.Where(x => x.Key.StartsWith("openid.", StringComparison.Ordinal)).ToDictionary(x => x.Key, x => x.Value.ToString());
        if (!values.TryGetValue("openid.claimed_id", out var claimed)) return null;
        values["openid.mode"] = "check_authentication";
        var response = await httpFactory.CreateClient("steam").PostAsync(Endpoint, new FormUrlEncodedContent(values), ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode || !body.Split('\n').Any(x => x.Trim().Equals("is_valid:true", StringComparison.OrdinalIgnoreCase))) return null;
        var match = Regex.Match(claimed, @"^https?://steamcommunity\.com/openid/id/(\d{17})/?$", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }
}

