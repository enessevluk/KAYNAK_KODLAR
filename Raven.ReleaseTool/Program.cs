using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

var argsMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
for (var i = 0; i < args.Length; i++)
{
    if (!args[i].StartsWith("--", StringComparison.Ordinal)) continue;
    argsMap[args[i]] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "";
}

if (argsMap.TryGetValue("--sign-file", out var fileToSign) && !string.IsNullOrWhiteSpace(fileToSign))
{
    var fullFile = Path.GetFullPath(fileToSign);
    if (!File.Exists(fullFile))
    {
        Console.Error.WriteLine("İmzalanacak dosya bulunamadı: " + fullFile);
        return 5;
    }
    var signKeyPath = argsMap.TryGetValue("--key", out var signKey) && !string.IsNullOrWhiteSpace(signKey)
        ? Path.GetFullPath(signKey)
        : Environment.GetEnvironmentVariable("RAVEN_UPDATE_PRIVATE_KEY");
    if (string.IsNullOrWhiteSpace(signKeyPath) || !File.Exists(signKeyPath))
    {
        Console.Error.WriteLine("Update private key bulunamadı. --key veya RAVEN_UPDATE_PRIVATE_KEY kullanın.");
        return 4;
    }

    byte[] fileHash;
    using (var fileStream = new FileStream(fullFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
        fileHash = SHA256.HashData(fileStream);
    using var signer = RSA.Create();
    signer.ImportFromPem(File.ReadAllText(signKeyPath));
    var fileSignature = signer.SignHash(fileHash, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    File.WriteAllText(fullFile + ".sig", Convert.ToBase64String(fileSignature));
    File.WriteAllText(fullFile + ".sha256", Convert.ToHexString(fileHash).ToLowerInvariant());
    Console.WriteLine("Dosya RSA-SHA256 ile imzalandı: " + fullFile);
    Console.WriteLine("SHA256: " + Convert.ToHexString(fileHash).ToLowerInvariant());
    return 0;
}

if (!argsMap.TryGetValue("--input", out var input) || !Directory.Exists(input) ||
    !argsMap.TryGetValue("--version", out var version) || string.IsNullOrWhiteSpace(version))
{
    Console.WriteLine("Kullanim: Raven.ReleaseTool --input <publish-klasoru> --version 1.1.2 --key <update_private.pem> [--output <klasor>]");
    return 2;
}

if (!Version.TryParse(version, out _))
{
    Console.Error.WriteLine("Gecersiz surum: " + version);
    return 3;
}

var output = argsMap.TryGetValue("--output", out var o) && !string.IsNullOrWhiteSpace(o)
    ? Path.GetFullPath(o)
    : Path.GetFullPath("Releases");
Directory.CreateDirectory(output);

var notes = "";

var keyPath = argsMap.TryGetValue("--key", out var kp) && !string.IsNullOrWhiteSpace(kp)
    ? Path.GetFullPath(kp)
    : Environment.GetEnvironmentVariable("RAVEN_UPDATE_PRIVATE_KEY");
if (string.IsNullOrWhiteSpace(keyPath) || !File.Exists(keyPath))
{
    Console.Error.WriteLine("Update private key bulunamadi. --key veya RAVEN_UPDATE_PRIVATE_KEY kullanin.");
    return 4;
}

var stage = Path.Combine(Path.GetTempPath(), "RavenRelease", Guid.NewGuid().ToString("N"));
var payload = Path.Combine(stage, "payload");
Directory.CreateDirectory(payload);
try
{
    foreach (var file in Directory.EnumerateFiles(input, "*", SearchOption.AllDirectories))
    {
        var rel = Path.GetRelativePath(input, file);
        if (IsForbiddenSecret(rel))
            throw new InvalidDataException(
                $"Güvenlik nedeniyle yayın paketine gizli anahtar eklenemez: {rel}");
        if (rel.StartsWith("Config" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            continue; // Kullanici/server ayarlari update ile ezilmez.
        if (rel.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
            continue;

        var dest = Path.Combine(payload, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(file, dest, true);
    }

    var manifestFiles = Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories)
        .Select(file => new ManifestFile(
            Path.GetRelativePath(payload, file).Replace('\\', '/'),
            new FileInfo(file).Length,
            HashFile(file)))
        .OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    var manifest = new ReleaseManifest("RavenMapPanel", version, DateTimeOffset.UtcNow, notes, manifestFiles);
    File.WriteAllText(Path.Combine(stage, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

    var pkg = Path.Combine(output, $"RavenMapPanel_Update_v{version}.ravenpkg");
    if (File.Exists(pkg)) File.Delete(pkg);
    ZipFile.CreateFromDirectory(stage, pkg, CompressionLevel.Optimal, false);

    byte[] hash;
    using (var packageStream = new FileStream(pkg, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
        hash = SHA256.HashData(packageStream);
    using var rsa = RSA.Create();
    rsa.ImportFromPem(File.ReadAllText(keyPath));
    var signature = rsa.SignHash(hash, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    var sigText = Convert.ToBase64String(signature);
    var shaText = Convert.ToHexString(hash).ToLowerInvariant();

    File.WriteAllText(pkg + ".sig", sigText);
    File.WriteAllText(pkg + ".sha256", shaText);
    File.Copy(Path.Combine(stage, "manifest.json"), Path.Combine(output, "manifest.json"), true);

    Console.WriteLine("Raven update paketi olusturuldu:");
    Console.WriteLine(pkg);
    Console.WriteLine("SHA256: " + shaText);
    Console.WriteLine("Imza: " + pkg + ".sig");
    return 0;
}
finally
{
    try { if (Directory.Exists(stage)) Directory.Delete(stage, true); } catch { }
}


static string HashFile(string path)
{
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
    return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
}

static bool IsForbiddenSecret(string relativePath)
{
    var normalized = relativePath.Replace('\\', '/');
    var fileName = Path.GetFileName(normalized);
    var extension = Path.GetExtension(fileName);
    return normalized.Contains("/Secrets/", StringComparison.OrdinalIgnoreCase)
        || normalized.StartsWith("Secrets/", StringComparison.OrdinalIgnoreCase)
        || fileName.Contains("private", StringComparison.OrdinalIgnoreCase)
        || fileName.Equals(".env", StringComparison.OrdinalIgnoreCase)
        || fileName.StartsWith(".env.", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".key", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".pfx", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".p12", StringComparison.OrdinalIgnoreCase);
}

record ManifestFile(string Path, long Size, string Sha256);
record ReleaseManifest(string Product, string Version, DateTimeOffset CreatedUtc, string Notes, ManifestFile[] Files);
