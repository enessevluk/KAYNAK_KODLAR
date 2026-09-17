using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RavenMapPanel;

public sealed class UpdateService(LicenseService licenseService)
{
    private const long MaximumUpdateBytes = 512L * 1024 * 1024;
    private static string UpdatePublicKeyPem => SecurityKeys.UpdatePublicKeyPem;

    public async Task DownloadVerifyAndStageAsync(UpdateInfo info, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var cache = licenseService.GetCacheSnapshot() ?? throw new InvalidOperationException("Aktif lisans bulunamadı.");
        var root = ResolveSafeUpdateDirectory(Path.GetTempPath(), info.LatestVersion);
        var parsedVersion = Version.Parse(info.LatestVersion);
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);
        var package = Path.Combine(root, $"RavenMapPanel_{parsedVersion}.ravenpkg");

        using var client = new HttpClient { BaseAddress = licenseService.ServerConfig.GetBaseUri(), Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.Add("X-Raven-Activation", cache.ActivationToken);
        client.DefaultRequestHeaders.Add("X-Raven-Device", licenseService.DeviceHash);
        using var response = await client.GetAsync($"api/update/download/{Uri.EscapeDataString(info.LatestVersion)}", HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        if (total is > MaximumUpdateBytes)
            throw new InvalidDataException("Güncelleme paketi güvenli boyut sınırını aşıyor.");
        await using (var input = await response.Content.ReadAsStreamAsync(ct))
        await using (var output = new FileStream(package, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, true))
        {
            var buffer = new byte[1024 * 128]; long readTotal = 0; int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                if (readTotal + read > MaximumUpdateBytes)
                    throw new InvalidDataException("Güncelleme paketi güvenli boyut sınırını aşıyor.");
                await output.WriteAsync(buffer.AsMemory(0, read), ct); readTotal += read;
                if (total > 0) progress?.Report(readTotal * 100d / total.Value);
            }
        }

        byte[] hashBytes;
        await using (var packageStream = new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            hashBytes = await SHA256.HashDataAsync(packageStream, ct);
        var actual = Convert.ToHexString(hashBytes).ToLowerInvariant();
        if (!actual.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Güncelleme SHA-256 doğrulaması başarısız.");
        var signature = Convert.FromBase64String(info.Signature.Trim());
        using (var rsa = RSA.Create())
        {
            rsa.ImportFromPem(UpdatePublicKeyPem);
            if (!rsa.VerifyHash(hashBytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                throw new CryptographicException("Güncelleme dijital imzası geçersiz.");
        }

        var extract = Path.Combine(root, "extract");
        ZipFile.ExtractToDirectory(package, extract, true);
        VerifyManifest(extract, info.LatestVersion);
        var payload = Path.Combine(extract, "payload");
        if (!Directory.Exists(payload)) throw new InvalidDataException("Güncelleme paketinde payload klasörü bulunamadı.");
        WriteAndLaunchUpdateScript(payload, AppContext.BaseDirectory, Environment.ProcessId, info.LatestVersion);
    }

    internal static string ResolveSafeUpdateDirectory(string temporaryRoot, string version)
    {
        if (!Version.TryParse(version, out var parsedVersion) ||
            !string.Equals(parsedVersion.ToString(), version, StringComparison.Ordinal))
            throw new InvalidDataException("Güncelleme sürüm bilgisi güvenli bir sayısal sürüm değil.");

        var updateRoot = Path.GetFullPath(Path.Combine(temporaryRoot, "RavenMapPanel", "Update"));
        var resolved = Path.GetFullPath(Path.Combine(updateRoot, parsedVersion.ToString()));
        var requiredPrefix = updateRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Güncelleme çalışma klasörü güvenli alanın dışında.");
        return resolved;
    }


    private static void VerifyManifest(string extractRoot, string expectedVersion)
    {
        var manifestPath = Path.Combine(extractRoot, "manifest.json");
        if (!File.Exists(manifestPath)) throw new InvalidDataException("Güncelleme manifest.json dosyası bulunamadı.");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var root = doc.RootElement;
        var product = root.TryGetProperty("product", out var p) ? p.GetString() : null;
        var version = root.TryGetProperty("version", out var v) ? v.GetString() : null;
        if (!string.Equals(product, "RavenMapPanel", StringComparison.Ordinal))
            throw new InvalidDataException("Güncelleme paketi Raven Map Panel ürünü değil.");
        if (!string.Equals(version, expectedVersion, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Güncelleme paketi sürümü uyuşmuyor. Beklenen: {expectedVersion}, Paket: {version ?? "?"}");
    }

    private static void WriteAndLaunchUpdateScript(string payload, string installDir, int currentPid, string targetVersion)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"raven_update_{Guid.NewGuid():N}.ps1");
        var exeName = Path.GetFileName(Environment.ProcessPath ?? "RavenMapPanel.exe");
        var script = BuildUpdateScript(payload, installDir, currentPid, targetVersion, exeName, Guid.NewGuid().ToString("N"));
        File.WriteAllText(scriptPath, script, new UTF8Encoding(false));
        Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
    }

    internal static string BuildUpdateScript(string payload, string installDir, int currentPid, string targetVersion, string exeName, string nonce)
    {
        static string Ps(string value) => value.Replace("'", "''");
        return $@"
$ErrorActionPreference='Stop'
$pidToWait={currentPid}
$payload='{Ps(payload)}'
$install='{Ps(installDir.TrimEnd(Path.DirectorySeparatorChar))}'
$exe='{Ps(exeName)}'
$targetVersion='{Ps(targetVersion)}'
$parent=Split-Path -Parent $install
$name=Split-Path -Leaf $install
$stage=Join-Path $parent ('.' + $name + '.raven-new-{nonce}')
$backup=Join-Path $parent ('.' + $name + '.raven-backup-{nonce}')
$errorFile=Join-Path $env:TEMP 'RavenMapPanel_UpdateError.txt'
$committed=$false
$installedFiles=[Collections.Generic.List[string]]::new()
$backedUpFiles=[Collections.Generic.List[object]]::new()
function Copy-DirectoryContents([string]$source,[string]$destination) {{
    if (-not (Test-Path -LiteralPath $source)) {{ return }}
    if (-not (Test-Path -LiteralPath $destination)) {{ New-Item -ItemType Directory -Path $destination -Force | Out-Null }}
    Get-ChildItem -LiteralPath $source -Force | ForEach-Object {{
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $destination $_.Name) -Recurse -Force
    }}
}}
function Invoke-WithRetry([scriptblock]$operation,[string]$label) {{
    for($attempt=1; $attempt -le 60; $attempt++) {{
        try {{ & $operation; return }}
        catch {{
            if($attempt -eq 60) {{ throw ($label + ': ' + $_.Exception.Message) }}
            Start-Sleep -Milliseconds 500
        }}
    }}
}}
try {{
    Remove-Item -LiteralPath $errorFile -Force -ErrorAction SilentlyContinue
    try {{ Wait-Process -Id $pidToWait -Timeout 120 -ErrorAction SilentlyContinue }} catch {{}}
    Start-Sleep -Seconds 2
    Set-Location -LiteralPath $parent
    if (Get-Process -Id $pidToWait -ErrorAction SilentlyContinue) {{ throw 'Raven Map Panel kapanmadığı için güncelleme güvenli biçimde uygulanamadı.' }}
    if (-not (Test-Path -LiteralPath $payload)) {{ throw 'Güncelleme payload klasörü bulunamadı.' }}
    if (-not (Test-Path -LiteralPath $install)) {{ throw 'Mevcut Raven kurulum klasörü bulunamadı.' }}
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    Copy-DirectoryContents $payload $stage
    if (-not (Test-Path -LiteralPath (Join-Path $stage $exe))) {{ throw 'Yeni güncelleme içinde ana uygulama dosyası bulunamadı.' }}

    # Ana kurulum klasörünü taşımak Rust/harita motoru gibi alt işlemler yüzünden
    # Windows'ta kilitlenebilir. Yalnız imzalı payload dosyalarını yerinde değiştir.
    $stageRoot=[IO.Path]::GetFullPath($stage).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    foreach($sourceFile in Get-ChildItem -LiteralPath $stage -File -Recurse) {{
        $relative=$sourceFile.FullName.Substring($stageRoot.Length)
        if ($relative.StartsWith('Config' + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or
            $relative.StartsWith('Maps' + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or
            $relative.StartsWith('CustomPrefabs' + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {{
            continue
        }}
        $destination=Join-Path $install $relative
        $backupFile=Join-Path $backup $relative
        $destinationDir=Split-Path -Parent $destination
        $backupDir=Split-Path -Parent $backupFile
        if (-not (Test-Path -LiteralPath $destinationDir)) {{ New-Item -ItemType Directory -Path $destinationDir -Force | Out-Null }}
        if (Test-Path -LiteralPath $destination) {{
            if (-not (Test-Path -LiteralPath $backupDir)) {{ New-Item -ItemType Directory -Path $backupDir -Force | Out-Null }}
            Invoke-WithRetry {{ Move-Item -LiteralPath $destination -Destination $backupFile -Force }} ('Eski dosya yedeklenemedi: ' + $relative)
            $backedUpFiles.Add([pscustomobject]@{{ Destination=$destination; Backup=$backupFile }})
        }}
        Invoke-WithRetry {{ Move-Item -LiteralPath $sourceFile.FullName -Destination $destination -Force }} ('Yeni dosya etkinleştirilemedi: ' + $relative)
        $installedFiles.Add($destination)
    }}

    # Windows Explorer uygulama ikonlarını dosya yoluna göre önbelleğe alır.
    # Yeni EXE etkinleştirildikten sonra Shell'e ikonları yeniden okumasını bildir.
    $iconRefresh=Join-Path $env:SystemRoot 'System32\ie4uinit.exe'
    if (Test-Path -LiteralPath $iconRefresh) {{
        try {{ Start-Process -FilePath $iconRefresh -ArgumentList '-show' -WindowStyle Hidden -Wait | Out-Null }} catch {{}}
    }}

    $started=Start-Process -FilePath (Join-Path $install $exe) -PassThru
    if ($null -eq $started) {{ throw 'Güncellenen Raven Map Panel başlatılamadı.' }}
    # EXE'nin yalnız process oluşturması yeterli değildir; eksik DLL/runtime gibi bir
    # sorunla hemen kapanırsa backup hâlâ dururken rollback yap.
    Start-Sleep -Seconds 5
    $started.Refresh()
    if ($started.HasExited) {{ throw ('Güncellenen Raven Map Panel açıldıktan hemen sonra kapandı. ExitCode=' + $started.ExitCode) }}
    # Bu noktadan sonra yeni sürüm çalışıyor ve kurulum başarılıdır.
    # Eski yedeğin Windows/antivirüs nedeniyle hemen silinememesi kurulumu başarısız yapmaz.
    $committed=$true
    Remove-Item -LiteralPath $errorFile -Force -ErrorAction SilentlyContinue
    try {{ Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction Stop }} catch {{}}
}}
catch {{
    $message=$_.Exception.Message
    if ($committed) {{
        Remove-Item -LiteralPath $errorFile -Force -ErrorAction SilentlyContinue
    }} else {{
        try {{
            foreach($installed in $installedFiles) {{
                if (Test-Path -LiteralPath $installed) {{ Remove-Item -LiteralPath $installed -Force -ErrorAction SilentlyContinue }}
            }}
            foreach($item in $backedUpFiles) {{
                if (Test-Path -LiteralPath $item.Backup) {{
                    $restoreDir=Split-Path -Parent $item.Destination
                    if (-not (Test-Path -LiteralPath $restoreDir)) {{ New-Item -ItemType Directory -Path $restoreDir -Force | Out-Null }}
                    Move-Item -LiteralPath $item.Backup -Destination $item.Destination -Force
                }}
            }}
            if (Test-Path -LiteralPath (Join-Path $install $exe)) {{ Start-Process -FilePath (Join-Path $install $exe) | Out-Null }}
        }} catch {{}}
        try {{ [IO.File]::WriteAllText($errorFile,('TARGET_VERSION=' + $targetVersion + [Environment]::NewLine + $message)) }} catch {{}}
    }}
}}
finally {{
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue
}}
";
    }

}
