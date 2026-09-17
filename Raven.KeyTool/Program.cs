using System.Security.Cryptography;

if (args.Length < 2 || !args[0].Equals("generate", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("Kullanim: Raven.KeyTool generate <Raven source root> [--rotate]");
    return 2;
}

var sourceRoot = Path.GetFullPath(args[1]);
var rotate = args.Skip(2).Any(x => x.Equals("--rotate", StringComparison.OrdinalIgnoreCase));
var packageRoot = Directory.GetParent(sourceRoot)?.FullName ?? sourceRoot;
var ownerRoot = Path.Combine(packageRoot, "OWNER_SECRETS");
var serverSecrets = Path.Combine(sourceRoot, "Raven.Server", "Secrets");
var releaseSecrets = Path.Combine(sourceRoot, "Raven.ReleaseTool", "Secrets");
var clientSecurity = Path.Combine(sourceRoot, "RavenMapPanel.Wpf", "Security");
Directory.CreateDirectory(ownerRoot);
Directory.CreateDirectory(serverSecrets);
Directory.CreateDirectory(releaseSecrets);
Directory.CreateDirectory(clientSecurity);

var licensePrivate = Path.Combine(ownerRoot, "license_private.pem");
var updatePrivate = Path.Combine(ownerRoot, "update_private.pem");
var publicFiles = new[]
{
    Path.Combine(serverSecrets, "license_public.pem"),
    Path.Combine(releaseSecrets, "update_public.pem"),
    Path.Combine(clientSecurity, "license_public.pem"),
    Path.Combine(clientSecurity, "update_public.pem"),
    Path.Combine(serverSecrets, "update_public.pem")
};
var legacyPrivateFiles = new[]
{
    Path.Combine(ownerRoot, "CURRENT", "license_private.pem"),
    Path.Combine(ownerRoot, "CURRENT", "update_private.pem"),
    Path.Combine(ownerRoot, "CURRENT_NEW", "license_private.pem"),
    Path.Combine(ownerRoot, "CURRENT_NEW", "update_private.pem")
};
var currentPrivateFiles = new[] { licensePrivate, updatePrivate };
var existingPrivate = currentPrivateFiles.Concat(legacyPrivateFiles).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

if (existingPrivate.Length > 0 && !rotate)
{
    Console.Error.WriteLine("MEVCUT PRIVATE ANAHTARLAR KORUNDU. Yeni key URETILMEDI.");
    foreach (var file in existingPrivate) Console.Error.WriteLine($"- {file}");
    Console.Error.WriteLine("Rotasyon gerekiyorsa acikca --rotate kullanin.");
    return 3;
}

if (rotate)
{
    var existing = existingPrivate.Concat(publicFiles.Where(File.Exists)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    if (existing.Length > 0)
    {
        var backup = Path.Combine(ownerRoot, "KeyBackups", DateTime.UtcNow.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(backup);
        foreach (var file in existing)
        {
            var relative = Path.GetFileName(file);
            var isPrivate = existingPrivate.Contains(file, StringComparer.OrdinalIgnoreCase);
            File.Copy(file, Path.Combine(backup, (isPrivate ? "PRIVATE_" : "PUBLIC_") + relative), overwrite: true);
        }
        Console.WriteLine($"Mevcut anahtarlar owner-only backup'a alindi: {backup}");
    }
}

var licensePublic = GeneratePair(licensePrivate);
WritePublic(licensePublic,
    Path.Combine(serverSecrets, "license_public.pem"),
    Path.Combine(clientSecurity, "license_public.pem"));

var updatePublic = GeneratePair(updatePrivate);
WritePublic(updatePublic,
    Path.Combine(releaseSecrets, "update_public.pem"),
    Path.Combine(clientSecurity, "update_public.pem"),
    Path.Combine(serverSecrets, "update_public.pem"));

Console.WriteLine();
Console.WriteLine("YENI PRODUCTION ANAHTARLARI OLUSTURULDU.");
Console.WriteLine($"- Private keyler kaynak kodun DISINDA: {ownerRoot}");
Console.WriteLine("- Tercih edilen sade yapi: OWNER_SECRETS\\license_private.pem + update_private.pem");
Console.WriteLine("- Kaynak agacinda sadece public keyler bulunur.");
Console.WriteLine("- OWNER_SECRETS klasorunu musteriye/gitre/arsive ASLA verme.");
Console.WriteLine();
Console.WriteLine("ONEMLI: Mevcut musteriler varsa update key rotasyonunu KEY_ROTATION_MIGRATION.txt sirasi ile uygula.");
return 0;

static string GeneratePair(string privatePath)
{
    using var rsa = RSA.Create();
    rsa.KeySize = 3072;
    File.WriteAllText(privatePath, rsa.ExportPkcs8PrivateKeyPem());
    return rsa.ExportSubjectPublicKeyInfoPem();
}

static void WritePublic(string pem, params string[] paths)
{
    foreach (var path in paths)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, pem);
    }
}
