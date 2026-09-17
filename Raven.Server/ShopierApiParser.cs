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

static class ShopierApiParser
{
    public static List<JsonElement> ExtractOrders(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        JsonElement array = default;
        if (root.ValueKind == JsonValueKind.Array) array = root;
        else if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "orders", "data", "items", "results" })
            {
                if (TryGetProperty(root, name, out var candidate) && candidate.ValueKind == JsonValueKind.Array) { array = candidate; break; }
            }
            if (array.ValueKind == JsonValueKind.Undefined && TryGetProperty(root, "id", out _)) return new List<JsonElement> { root.Clone() };
        }
        if (array.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("Shopier sipariş listesi bulunamadı.");
        return array.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).Select(x => x.Clone()).ToList();
    }

    public static (JsonElement LineItem, string ProductId, string Title)? FindConfiguredProduct(JsonElement order, string configuredProductId, string configuredTitle)
    {
        if (!TryGetProperty(order, "lineItems", out var lineItems) || lineItems.ValueKind != JsonValueKind.Array) return null;
        (JsonElement, string, string)? titleFallback = null;
        foreach (var item in lineItems.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var pid = GetString(item, "productId") ?? "";
            var title = GetString(item, "title") ?? "";
            if (!string.IsNullOrWhiteSpace(configuredProductId) && pid.Equals(configuredProductId, StringComparison.OrdinalIgnoreCase))
                return (item.Clone(), pid, title);
            if (string.IsNullOrWhiteSpace(configuredProductId) && !string.IsNullOrWhiteSpace(configuredTitle) && title.Equals(configuredTitle, StringComparison.OrdinalIgnoreCase))
                titleFallback = (item.Clone(), pid, title);
        }
        return titleFallback;
    }

    public static string? GetString(JsonElement element, string name)
        => TryGetProperty(element, name, out var value) ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString() : null;
    public static int GetInt(JsonElement element, string name, int defaultValue)
        => int.TryParse(GetString(element, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : defaultValue;
    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var p in element.EnumerateObject())
                if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = p.Value; return true; }
        value = default;
        return false;
    }
}

