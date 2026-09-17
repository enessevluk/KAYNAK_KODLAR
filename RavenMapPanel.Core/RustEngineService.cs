using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace RavenMapPanel;

internal sealed record RustEngineCheckResult(
    string RustServerPath,
    string SteamCmdPath,
    bool RustDedicatedExists,
    bool HarmonyExists,
    bool RustHarmonyExists,
    IReadOnlyList<string> MissingFiles)
{
    public bool IsReady => RustDedicatedExists && HarmonyExists && RustHarmonyExists;
    public string StatusText => IsReady ? "Hazır" : "Kurulum gerekli";
}

internal sealed record RustEngineInstallProgress(
    double Percent,
    string Title,
    string Detail,
    bool IsIndeterminate = false);

internal sealed record RustEngineUpdateStatus(
    bool CheckSucceeded,
    bool UpdateAvailable,
    string InstalledBuildId,
    string CurrentBuildId,
    string Detail);

internal sealed class RustEngineService
{
    public const string SteamCmdDownloadUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip";
    public const string DefaultRoot = @"C:\RavenMapServer";
    public static string DefaultSteamCmdPath => Path.Combine(DefaultRoot, "SteamCMD");
    public static string DefaultRustServerPath => Path.Combine(DefaultRoot, "RustServer");

    private static readonly Regex SteamProgressRegex = new(
        @"progress:\s*(?<value>\d+(?:[\.,]\d+)?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BracketProgressRegex = new(
        @"\[\s*(?<value>\d{1,3})%\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public RustEngineCheckResult Check(string? rustServerPath)
    {
        var serverPath = ResolveExistingServerPath(rustServerPath);
        var steamCmdPath = ResolveSteamCmdPath(serverPath);
        var managed = Path.Combine(serverPath, "RustDedicated_Data", "Managed");

        var rustDedicated = File.Exists(Path.Combine(serverPath, "RustDedicated.exe"));
        var harmony = File.Exists(Path.Combine(managed, "0Harmony.dll"));
        var rustHarmony = File.Exists(Path.Combine(managed, "Rust.Harmony.dll"));
        var missing = new List<string>();
        if (!rustDedicated) missing.Add("RustDedicated.exe");
        if (!harmony) missing.Add(@"RustDedicated_Data\Managed\0Harmony.dll");
        if (!rustHarmony) missing.Add(@"RustDedicated_Data\Managed\Rust.Harmony.dll");

        return new(serverPath, steamCmdPath, rustDedicated, harmony, rustHarmony, missing);
    }

    public async Task<RustEngineCheckResult> InstallOrRepairAsync(
        string? rustServerPath,
        IProgress<RustEngineInstallProgress>? progress,
        CancellationToken cancellationToken,
        bool forceValidate = false)
    {
        var serverPath = ResolveExistingServerPath(rustServerPath);
        var steamCmdPath = ResolveSteamCmdPath(serverPath);
        var initialState = Check(serverPath);

        EnsureWritableDirectory(serverPath);
        EnsureWritableDirectory(steamCmdPath);

        progress?.Report(new(3, "Sistem hazırlanıyor", $"Kurulum konumu: {serverPath}"));
        var steamWasFreshlyInstalled = await EnsureSteamCmdAsync(steamCmdPath, progress, cancellationToken);

        // A brand-new SteamCMD executable normally self-updates on first launch.
        // Do that as a separate lightweight step so a self-update/restart cannot be
        // mistaken for a failed multi-GB Rust installation.
        if (steamWasFreshlyInstalled || !File.Exists(Path.Combine(steamCmdPath, "steamclient.dll")))
        {
            progress?.Report(new(25, "SteamCMD hazırlanıyor", "SteamCMD ilk çalıştırma güncellemesi yapılıyor…", true));
            await BootstrapSteamCmdAsync(steamCmdPath, serverPath, progress, cancellationToken);
        }

        // Fresh installs intentionally skip `validate`: SteamCMD already verifies
        // downloaded chunks and this avoids an unnecessary full-file validation pass.
        // Repairs/explicit verification keep `validate` enabled.
        var useValidate = forceValidate || initialState.RustDedicatedExists;
        var actionText = useValidate ? "indiriyor ve dosyaları doğruluyor" : "indiriyor";
        progress?.Report(new(30, "Rust Dedicated Server kuruluyor", $"SteamCMD Rust dosyalarını {actionText}…", true));
        await RunSteamCmdAsync(steamCmdPath, serverPath, useValidate, progress, cancellationToken);

        progress?.Report(new(94, "Dosyalar doğrulanıyor", "Raven harita motoru için gerekli Rust ve Harmony dosyaları kontrol ediliyor…"));
        var result = Check(serverPath);
        if (!result.IsReady)
        {
            throw new InvalidOperationException(
                "Rust Dedicated kurulumu tamamlandı ancak Raven için gereken dosyalardan bazıları eksik: " +
                string.Join(", ", result.MissingFiles));
        }

        progress?.Report(new(100, "Harita motoru hazır", "Rust Dedicated Server ve Harmony çalışma dosyaları doğrulandı."));
        return result;
    }

    public async Task<RustEngineUpdateStatus> CheckForUpdateAsync(
        string? rustServerPath,
        CancellationToken cancellationToken)
    {
        var state = Check(rustServerPath);
        if (!state.IsReady)
            return new(false, false, "", "", "Harita motoru henüz kurulu değil.");

        var installedBuildId = ReadInstalledBuildId(state.RustServerPath);
        if (string.IsNullOrWhiteSpace(installedBuildId))
            return new(false, false, "", "", "Kurulu Rust sürüm bilgisi okunamadı.");

        var steamExe = Path.Combine(state.SteamCmdPath, "steamcmd.exe");
        if (!File.Exists(steamExe))
            return new(false, false, installedBuildId, "", "SteamCMD bulunamadığı için güncelleme denetlenemedi.");

        try
        {
            var output = await CaptureSteamCmdAsync(
                steamExe,
                state.SteamCmdPath,
                ["+login", "anonymous", "+app_info_update", "1", "+app_info_print", "258550", "+quit"],
                cancellationToken);
            var currentBuildId = ParsePublicBuildId(output);
            if (string.IsNullOrWhiteSpace(currentBuildId))
                return new(false, false, installedBuildId, "", "Steam'deki güncel Rust sürüm bilgisi okunamadı.");

            var updateAvailable = !string.Equals(installedBuildId, currentBuildId, StringComparison.Ordinal);
            return new(
                true,
                updateAvailable,
                installedBuildId,
                currentBuildId,
                updateAvailable ? "Yeni bir Rust Dedicated Server güncellemesi hazır." : "Rust Dedicated Server güncel.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, false, installedBuildId, "", "Rust güncelleme kontrolü zaman aşımına uğradı.");
        }
        catch (Exception ex)
        {
            return new(false, false, installedBuildId, "", "Rust güncellemesi denetlenemedi: " + ex.Message);
        }
    }

    public async Task<RustEngineCheckResult> UpdateAsync(
        string? rustServerPath,
        IProgress<RustEngineInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var serverPath = ResolveExistingServerPath(rustServerPath);
        var steamCmdPath = ResolveSteamCmdPath(serverPath);
        EnsureWritableDirectory(serverPath);
        EnsureWritableDirectory(steamCmdPath);

        progress?.Report(new(5, "Güncelleme hazırlanıyor", $"Rust sunucu klasörü: {serverPath}"));
        var steamWasFreshlyInstalled = await EnsureSteamCmdAsync(steamCmdPath, progress, cancellationToken);
        if (steamWasFreshlyInstalled || !File.Exists(Path.Combine(steamCmdPath, "steamclient.dll")))
        {
            progress?.Report(new(25, "SteamCMD hazırlanıyor", "SteamCMD güncelleniyor…", true));
            await BootstrapSteamCmdAsync(steamCmdPath, serverPath, progress, cancellationToken);
        }

        progress?.Report(new(30, "Rust güncelleniyor", "SteamCMD yalnızca değişen Rust Dedicated dosyalarını indiriyor…", true));
        await RunSteamCmdAsync(steamCmdPath, serverPath, validate: false, progress, cancellationToken);

        var result = Check(serverPath);
        if (!result.IsReady)
        {
            throw new InvalidOperationException(
                "Rust güncellemesi tamamlandı ancak Raven için gereken dosyalardan bazıları eksik: " +
                string.Join(", ", result.MissingFiles));
        }

        progress?.Report(new(100, "Rust güncel", "Rust Dedicated Server güncellemesi tamamlandı. Raven modları ilk üretimde otomatik yenilenecek."));
        return result;
    }

    internal static string ReadInstalledBuildId(string serverPath)
    {
        var candidates = new[]
        {
            Path.Combine(serverPath, "steamapps", "appmanifest_258550.acf"),
            Path.Combine(Directory.GetParent(serverPath)?.FullName ?? serverPath, "SteamCMD", "steamapps", "appmanifest_258550.acf")
        };

        foreach (var manifest in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!File.Exists(manifest)) continue;
                var match = Regex.Match(
                    File.ReadAllText(manifest),
                    @"(?im)^\s*""buildid""\s*""(?<id>\d+)""");
                if (match.Success) return match.Groups["id"].Value;
            }
            catch
            {
                // Diğer olası manifest konumu denenir.
            }
        }

        return "";
    }

    internal static string ParsePublicBuildId(string appInfoOutput)
    {
        if (string.IsNullOrWhiteSpace(appInfoOutput)) return "";
        var match = Regex.Match(
            appInfoOutput,
            @"(?is)""branches""\s*\{.*?""public""\s*\{.*?""buildid""\s*""(?<id>\d+)""");
        return match.Success ? match.Groups["id"].Value : "";
    }

    public long? GetAvailableDiskSpaceBytes(string? rustServerPath)
    {
        try
        {
            var path = NormalizeServerPath(rustServerPath);
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrWhiteSpace(root)) return null;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            return null;
        }
    }

    internal static string ResolveExistingServerPath(string? rustServerPath)
    {
        var root = NormalizeServerPath(rustServerPath);
        if (File.Exists(Path.Combine(root, "RustDedicated.exe"))) return root;

        // Kullanıcı VDS/PC üzerinde RavenMapServer gibi bir üst klasörü seçerse
        // yaygın alt klasörleri otomatik bul. Böylece EXE tam olarak RustServer
        // klasörü seçilmedi diye "RustDedicated.exe yok" hatası verilmez.
        foreach (var childName in new[] { "RustServer", "rustserver", "Server", "server", "Rust", "rust" })
        {
            var candidate = Path.Combine(root, childName);
            if (File.Exists(Path.Combine(candidate, "RustDedicated.exe")))
                return Path.GetFullPath(candidate);
        }

        try
        {
            if (Directory.Exists(root))
            {
                var matches = Directory.EnumerateDirectories(root)
                    .Where(x => File.Exists(Path.Combine(x, "RustDedicated.exe")))
                    .Take(2)
                    .ToList();
                if (matches.Count == 1) return Path.GetFullPath(matches[0]);
            }
        }
        catch
        {
            // Yetki/IO sorunu asıl Check sonucunda eksik dosya olarak gösterilecek.
        }

        return root;
    }

    private static string NormalizeServerPath(string? rustServerPath)
    {
        var value = string.IsNullOrWhiteSpace(rustServerPath) ? DefaultRustServerPath : rustServerPath.Trim();
        return Path.GetFullPath(value);
    }

    private static string ResolveSteamCmdPath(string serverPath)
    {
        if (serverPath.Equals(DefaultRustServerPath, StringComparison.OrdinalIgnoreCase))
            return DefaultSteamCmdPath;

        var parent = Directory.GetParent(serverPath)?.FullName;
        if (!string.IsNullOrWhiteSpace(parent) &&
            !parent.Equals(Path.GetPathRoot(parent), StringComparison.OrdinalIgnoreCase))
            return Path.Combine(parent, "SteamCMD");

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RavenMapPanel", "SteamCMD");
    }

    private static string ResolveLogPath(string serverPath)
    {
        var parent = Directory.GetParent(serverPath)?.FullName;
        if (string.IsNullOrWhiteSpace(parent)) parent = DefaultRoot;
        var logDir = Path.Combine(parent, "Logs");
        Directory.CreateDirectory(logDir);
        return Path.Combine(logDir, $"steamcmd_{DateTime.Now:yyyyMMdd_HHmmss}.log");
    }

    private static void EnsureWritableDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, $".raven_write_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "raven");
            File.Delete(probe);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException(
                $"'{path}' klasörüne yazma izni yok. Raven Map Panel'i yönetici olarak çalıştırın veya farklı bir kurulum konumu seçin.", ex);
        }
    }

    private static async Task<bool> EnsureSteamCmdAsync(
        string steamCmdPath,
        IProgress<RustEngineInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var steamExe = Path.Combine(steamCmdPath, "steamcmd.exe");
        if (File.Exists(steamExe))
        {
            progress?.Report(new(24, "SteamCMD hazır", "Mevcut SteamCMD kurulumu kullanılacak."));
            return false;
        }

        progress?.Report(new(8, "SteamCMD indiriliyor", "Valve SteamCMD paketi indiriliyor…"));
        var tempZip = Path.Combine(Path.GetTempPath(), $"raven_steamcmd_{Guid.NewGuid():N}.zip");

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            using (var response = await http.GetAsync(SteamCmdDownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;

                await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
                await using (var target = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true))
                {
                    var buffer = new byte[128 * 1024];
                    long received = 0;
                    var watch = Stopwatch.StartNew();
                    long previousReceived = 0;
                    var previousSample = TimeSpan.Zero;

                    while (true)
                    {
                        var read = await source.ReadAsync(buffer, cancellationToken);
                        if (read <= 0) break;
                        await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        received += read;

                        if (total is > 0)
                        {
                            var ratio = Math.Clamp(received / (double)total.Value, 0, 1);
                            var now = watch.Elapsed;
                            var speedText = string.Empty;
                            if ((now - previousSample).TotalSeconds >= 0.75)
                            {
                                var seconds = Math.Max(0.001, (now - previousSample).TotalSeconds);
                                var bytesPerSecond = (received - previousReceived) / seconds;
                                speedText = $" • {FormatBytes((long)bytesPerSecond)}/sn";
                                previousReceived = received;
                                previousSample = now;
                            }

                            progress?.Report(new(
                                8 + ratio * 10,
                                "SteamCMD indiriliyor",
                                $"{FormatBytes(received)} / {FormatBytes(total.Value)}{speedText}"));
                        }
                    }

                    await target.FlushAsync(cancellationToken);
                }
            }

            progress?.Report(new(20, "SteamCMD kuruluyor", "İndirilen paket açılıyor…", true));
            ZipFile.ExtractToDirectory(tempZip, steamCmdPath, true);
            if (!File.Exists(steamExe))
                throw new FileNotFoundException("steamcmd.exe arşiv açıldıktan sonra bulunamadı.", steamExe);

            progress?.Report(new(24, "SteamCMD hazır", "SteamCMD başarıyla kuruldu."));
            return true;
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    private static async Task BootstrapSteamCmdAsync(
        string steamCmdPath,
        string serverPath,
        IProgress<RustEngineInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var steamExe = Path.Combine(steamCmdPath, "steamcmd.exe");
        var logPath = ResolveLogPath(serverPath);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new(25, "SteamCMD hazırlanıyor", $"SteamCMD ilk çalıştırma kontrolü ({attempt}/3)…", true));

            var result = await ExecuteSteamCmdAsync(
                steamExe,
                steamCmdPath,
                ["+quit"],
                progress,
                logPath,
                isRustInstall: false,
                cancellationToken);

            if (result.ExitCode == 0)
                return;

            if (attempt < 3 && IsTransientSteamCmdFailure(result))
            {
                progress?.Report(new(25, "SteamCMD yeniden deneniyor", "Steam bağlantısı/ilk güncelleme tamamlanamadı; birkaç saniye sonra tekrar denenecek…", true));
                await Task.Delay(TimeSpan.FromSeconds(2 + attempt), cancellationToken);
                continue;
            }

            throw BuildSteamCmdException("SteamCMD ilk çalıştırma güncellemesini tamamlayamadı", result, logPath);
        }
    }

    private static async Task RunSteamCmdAsync(
        string steamCmdPath,
        string serverPath,
        bool validate,
        IProgress<RustEngineInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var steamExe = Path.Combine(steamCmdPath, "steamcmd.exe");
        if (!File.Exists(steamExe)) throw new FileNotFoundException("steamcmd.exe bulunamadı.", steamExe);

        var logPath = ResolveLogPath(serverPath);
        var args = new List<string>
        {
            "+force_install_dir", serverPath,
            "+login", "anonymous",
            "+app_update", "258550"
        };
        if (validate) args.Add("validate");
        args.Add("+quit");

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new(
                30,
                validate ? "Rust Dedicated doğrulanıyor" : "Rust Dedicated indiriliyor",
                attempt == 1
                    ? (validate ? "SteamCMD Rust dosyalarını indiriyor ve doğruluyor…" : "SteamCMD Rust Dedicated Server dosyalarını indiriyor…")
                    : $"SteamCMD indirmeye kaldığı yerden devam ediyor (deneme {attempt}/3)…",
                true));

            var result = await ExecuteSteamCmdAsync(
                steamExe,
                steamCmdPath,
                args,
                progress,
                logPath,
                isRustInstall: true,
                cancellationToken);

            if (result.ExitCode == 0)
                return;

            // SteamCMD occasionally returns a non-zero updater/network code even
            // after the app files are already complete. Never throw away a valid
            // installation solely because of the process exit code.
            if (CheckStatic(serverPath).IsReady)
            {
                progress?.Report(new(92, "Rust Dedicated Server hazır", "SteamCMD beklenmeyen bir çıkış kodu verdi ancak gerekli Rust dosyaları eksiksiz; kurulum kabul edildi."));
                return;
            }

            if (attempt < 3 && IsTransientSteamCmdFailure(result))
            {
                progress?.Report(new(
                    30,
                    "SteamCMD yeniden deneniyor",
                    $"SteamCMD geçici bir bağlantı/güncelleme hatası verdi (kod {result.ExitCode}). İndirme silinmeden kaldığı yerden devam edecek…",
                    true));
                await Task.Delay(TimeSpan.FromSeconds(3 + attempt * 2), cancellationToken);
                continue;
            }

            throw BuildSteamCmdException("SteamCMD Rust Dedicated Server kurulumunu tamamlayamadı", result, logPath);
        }
    }

    private static RustEngineCheckResult CheckStatic(string serverPath)
    {
        var managed = Path.Combine(serverPath, "RustDedicated_Data", "Managed");
        var rustDedicated = File.Exists(Path.Combine(serverPath, "RustDedicated.exe"));
        var harmony = File.Exists(Path.Combine(managed, "0Harmony.dll"));
        var rustHarmony = File.Exists(Path.Combine(managed, "Rust.Harmony.dll"));
        var missing = new List<string>();
        if (!rustDedicated) missing.Add("RustDedicated.exe");
        if (!harmony) missing.Add(@"RustDedicated_Data\Managed\0Harmony.dll");
        if (!rustHarmony) missing.Add(@"RustDedicated_Data\Managed\Rust.Harmony.dll");
        return new(serverPath, string.Empty, rustDedicated, harmony, rustHarmony, missing);
    }

    private sealed record SteamCmdProcessResult(int ExitCode, IReadOnlyList<string> TailLines);

    private static async Task<string> CaptureSteamCmdAsync(
        string steamExe,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = steamExe,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            ErrorDialog = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in arguments)
            start.ArgumentList.Add(arg);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException("SteamCMD güncelleme kontrolü başlatılamadı.");

        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = await stdout;
            var error = await stderr;
            if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(output))
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? $"SteamCMD çıkış kodu: {process.ExitCode}" : error.Trim());
            return output + Environment.NewLine + error;
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
            throw;
        }
    }

    private static async Task<SteamCmdProcessResult> ExecuteSteamCmdAsync(
        string steamExe,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        IProgress<RustEngineInstallProgress>? progress,
        string logPath,
        bool isRustInstall,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = steamExe,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            ErrorDialog = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in arguments)
            start.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException("SteamCMD başlatılamadı.");

        var gate = new object();
        var tail = new Queue<string>();
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        using var log = new StreamWriter(logPath, true, new UTF8Encoding(false));
        log.WriteLine($"===== {DateTime.Now:O} SteamCMD start =====");
        log.WriteLine($"EXE: {steamExe}");
        log.WriteLine("ARGS: " + string.Join(" ", arguments.Select(QuoteForLog)));

        void HandleLine(string line, bool isError)
        {
            lock (gate)
            {
                log.WriteLine((isError ? "ERR | " : "OUT | ") + line);
                tail.Enqueue(line);
                while (tail.Count > 40) tail.Dequeue();
            }

            ReportSteamLine(line, isError, isRustInstall, progress);
        }

        try
        {
            var stdout = PumpOutputAsync(process.StandardOutput, line => HandleLine(line, false), cancellationToken);
            var stderr = PumpOutputAsync(process.StandardError, line => HandleLine(line, true), cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(stdout, stderr);

            lock (gate)
            {
                log.WriteLine($"EXIT: {process.ExitCode}");
                log.WriteLine("===== SteamCMD end =====");
                log.Flush();
            }

            return new(process.ExitCode, tail.ToArray());
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
            throw;
        }
    }

    private static async Task PumpOutputAsync(
        StreamReader reader,
        Action<string> onLine,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            onLine(line.Trim());
        }
    }

    private static void ReportSteamLine(
        string line,
        bool isError,
        bool isRustInstall,
        IProgress<RustEngineInstallProgress>? progress)
    {
        if (progress is null) return;

        if (isRustInstall)
        {
            var match = SteamProgressRegex.Match(line);
            if (match.Success && double.TryParse(
                    match.Groups["value"].Value.Replace(',', '.'),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var steamPercent))
            {
                steamPercent = Math.Clamp(steamPercent, 0, 100);
                var mapped = 30 + steamPercent * 0.62;
                progress.Report(new(
                    mapped,
                    "Rust Dedicated Server indiriliyor",
                    $"Steam indirme ilerlemesi: %{steamPercent:0.0}"));
                return;
            }
        }

        var bracket = BracketProgressRegex.Match(line);
        if (bracket.Success && double.TryParse(bracket.Groups["value"].Value, out var bracketPercent))
        {
            if (isRustInstall)
            {
                var mapped = 30 + Math.Clamp(bracketPercent, 0, 100) * 0.62;
                progress.Report(new(mapped, "Rust Dedicated Server indiriliyor", line));
            }
            else
            {
                progress.Report(new(25, "SteamCMD hazırlanıyor", line, true));
            }
            return;
        }

        if (line.Contains("Success! App '258550' fully installed", StringComparison.OrdinalIgnoreCase))
        {
            progress.Report(new(92, "Rust Dedicated Server hazır", "SteamCMD Rust kurulumunu başarıyla tamamladı."));
            return;
        }

        if (line.Contains("Connecting anonymously", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Waiting for client config", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Waiting for user info", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Downloading update", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Verifying installation", StringComparison.OrdinalIgnoreCase))
        {
            progress.Report(new(isRustInstall ? 30 : 25, isRustInstall ? "Rust Dedicated Server indiriliyor" : "SteamCMD hazırlanıyor", line, true));
            return;
        }

        if (isError ||
            line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("download", StringComparison.OrdinalIgnoreCase) && line.Contains("fail", StringComparison.OrdinalIgnoreCase))
        {
            progress.Report(new(isRustInstall ? 30 : 25, isRustInstall ? "SteamCMD mesajı" : "SteamCMD hazırlanıyor", line, true));
        }
    }

    private static bool IsTransientSteamCmdFailure(SteamCmdProcessResult result)
    {
        if (result.ExitCode == 7) return true;

        var joined = string.Join("\n", result.TailLines);
        return joined.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
               joined.Contains("needs to be online", StringComparison.OrdinalIgnoreCase) ||
               joined.Contains("download failed", StringComparison.OrdinalIgnoreCase) ||
               joined.Contains("http error", StringComparison.OrdinalIgnoreCase) ||
               joined.Contains("connection", StringComparison.OrdinalIgnoreCase) &&
               joined.Contains("failed", StringComparison.OrdinalIgnoreCase);
    }

    private static InvalidOperationException BuildSteamCmdException(
        string prefix,
        SteamCmdProcessResult result,
        string logPath)
    {
        var useful = result.TailLines
            .Where(line =>
                line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("fatal", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("state is", StringComparison.OrdinalIgnoreCase))
            .TakeLast(8)
            .ToArray();

        var detail = useful.Length > 0
            ? Environment.NewLine + Environment.NewLine + "SteamCMD:" + Environment.NewLine + string.Join(Environment.NewLine, useful)
            : string.Empty;

        return new InvalidOperationException(
            $"{prefix}. Çıkış kodu: {result.ExitCode}.{detail}{Environment.NewLine}{Environment.NewLine}Ayrıntılı günlük: {logPath}");
    }

    private static string QuoteForLog(string value)
    {
        if (value.Length == 0) return "\"\"";
        var escaped = value.Replace("\"", "\\\"");
        return value.Any(char.IsWhiteSpace) ? "\"" + escaped + "\"" : escaped;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }
}
