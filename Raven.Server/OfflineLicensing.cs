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

sealed class OfflineTokenSigner
{
    private readonly RSA rsa = RSA.Create();
    private readonly object gate = new();
    public OfflineTokenSigner(string privateKeyPath) => rsa.ImportFromPem(File.ReadAllText(privateKeyPath));
    public string Create(LicenseRecord license, string deviceHash, DateTimeOffset validUntil)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new OfflinePayload(license.Id, license.SteamId ?? "", deviceHash, EntitlementPlans.Normalize(license.Plan), validUntil.ToUnixTimeSeconds()));
        byte[] sig;
        lock (gate) sig = rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return Base64Url(payload) + "." + Base64Url(sig);
    }
    public string Sign(byte[] payload)
    {
        lock (gate) return Convert.ToBase64String(
            rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    record OfflinePayload(long LicenseId, string SteamId, string DeviceHash, string Plan, long Exp);
}

static class LicenseKey
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    public static string Generate()
    {
        string Segment() => new string(Enumerable.Range(0, 4).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray());
        return $"RAVEN-{Segment()}-{Segment()}-{Segment()}-{Segment()}";
    }
}
