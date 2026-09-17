using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RavenMapPanel;

internal sealed class GenerationService(List<MonumentRule> catalog, LicenseService licenseService)
{
    private static readonly SemaphoreSlim GenerationGate = new(1, 1);
    private readonly CustomPrefabService customPrefabService = new();

    public event Action<string>? Log;

    public async Task<GenerationResult> GenerateAsync(AppSettings settings, CancellationToken cancellation)
    {
        await GenerationGate.WaitAsync(cancellation);
        try
        {
            return await GenerateCoreAsync(settings, cancellation);
        }
        finally
        {
            GenerationGate.Release();
        }
    }

    public async Task<GenerationResult> AnalyzeImportedMapAsync(string sourceMapPath, AppSettings settings, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(sourceMapPath) || !File.Exists(sourceMapPath))
            throw new FileNotFoundException("İçe aktarılacak MAP dosyası bulunamadı.", sourceMapPath);
        if (!Path.GetExtension(sourceMapPath).Equals(".map", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Yalnızca Rust .map dosyaları içe aktarılabilir.");
        if (new FileInfo(sourceMapPath).Length < 1024)
            throw new InvalidDataException("Seçilen MAP dosyası boş veya geçersiz görünüyor.");

        await GenerationGate.WaitAsync(cancellation);
        try
        {
            return await AnalyzeImportedMapCoreAsync(sourceMapPath, settings, cancellation);
        }
        finally
        {
            GenerationGate.Release();
        }
    }

    private async Task<GenerationResult> GenerateCoreAsync(AppSettings settings, CancellationToken cancellation)
    {
        Log?.Invoke("Üretim ayarları hazırlanıyor…");
        GenerationPreflight.ValidateOrThrow(settings, catalog);
        var job = PrepareJob(settings);
        if (!string.IsNullOrWhiteSpace(job.BackupPath))
            Log?.Invoke("Önceki sürüm otomatik yedeklendi.");

        Log?.Invoke("Harmony bileşenleri ve kurallar hazırlanıyor…");
        AssetStore.ExtractHarmonyMods(job.Root);
        ResetExpectedOutputs(job);

        using var prefabSession = await customPrefabService.PrepareSessionAsync(job.Root, settings, catalog, licenseService, Log, cancellation);
        WriteCustomGenerator(job.Root, settings, prefabSession.Applied.Count > 0);
        if (prefabSession.Applied.Count > 0)
            Log?.Invoke($"Özel monument prefab sistemi aktif: {prefabSession.Applied.Count} monument / {prefabSession.InstalledMapCount} target.");
        else if (prefabSession.SelectedMonumentCount > 0)
            Log?.Invoke("Seçili özel prefablar uygulanamadı veya monument engelli. İlgili monumentler vanilla kalacak.");

        PrepareGeneratorConfiguration(job, settings);
        Log?.Invoke("Monument kuralları ve dünya ayarları Rust harita motoruna aktarılıyor…");
        Log?.Invoke("Kural politikası: God Rock, Rust terrain anchor zinciri içinde kesin adet; kayıt sonrası prefab/terrain değişikliği yok.");

        Log?.Invoke("Dünya ve monumentler oluşturuluyor…");
        await RunRustGenerationAsync(job, settings, cancellation);
        ValidateGeneratedRules(job, settings);
        var delivered = DeliverOutputs(job, settings);
        CleanupWorkingOutputs(job);
        return delivered;
    }

    private async Task<GenerationResult> AnalyzeImportedMapCoreAsync(string sourceMapPath, AppSettings settings, CancellationToken cancellation)
    {
        Log?.Invoke("İçe aktarılan MAP dosyası analiz için hazırlanıyor…");
        var job = PrepareJob(settings);
        if (!string.IsNullOrWhiteSpace(job.BackupPath))
            Log?.Invoke("Aynı isimdeki önceki içe aktarım güvenli şekilde yedeklendi.");

        AssetStore.ExtractHarmonyMods(job.Root);
        ResetExpectedOutputs(job);

        var customGeneratorPath = Path.Combine(job.Root, "HarmonyConfig", "CustomGenerator.json");
        var placementPath = Path.Combine(job.Root, "HarmonyConfig", "RavenGuaranteedPlacement.json");
        var worldConfigPath = Path.Combine(job.Root, "server", settings.Identity, "harita_ayarlari.json");
        var originalCustomGenerator = File.Exists(customGeneratorPath) ? File.ReadAllBytes(customGeneratorPath) : null;
        var originalPlacement = File.Exists(placementPath) ? File.ReadAllBytes(placementPath) : null;
        var originalWorldConfig = File.Exists(worldConfigPath) ? File.ReadAllBytes(worldConfigPath) : null;

        try
        {
            PrepareImportConfiguration(job.Root, settings);
            Directory.CreateDirectory(Path.GetDirectoryName(job.MapExpected)!);
            File.Copy(sourceMapPath, job.MapExpected, true);
            Log?.Invoke("MAP dosyası değişiklik uygulanmadan Raven analiz motoruna yüklendi.");
            await RunRustGenerationAsync(job, settings, cancellation, importMode: true);
            var delivered = DeliverOutputs(job, settings);
            CleanupWorkingOutputs(job);
            return delivered;
        }
        finally
        {
            RestoreConfiguration(customGeneratorPath, originalCustomGenerator);
            RestoreConfiguration(placementPath, originalPlacement);
            RestoreConfiguration(worldConfigPath, originalWorldConfig);
        }
    }

    private GenerationJob PrepareJob(AppSettings settings)
    {
        var root = RustEngineService.ResolveExistingServerPath(settings.RustServerPath);
        var exe = Path.Combine(root, "RustDedicated.exe");
        if (!File.Exists(exe))
            throw new FileNotFoundException($"RustDedicated.exe bulunamadı. Seçilen yol ve yaygın RustServer alt klasörleri kontrol edildi: {settings.RustServerPath}", exe);
        ValidateHarmonyRuntime(root);

        var safeName = SettingsStore.SafeName(settings.MapName, $"Raven_{settings.WorldSize}_{settings.Seed}");
        var output = Path.Combine(SettingsStore.OutputRoot(settings), $"{safeName}_{settings.WorldSize}_{settings.Seed}");
        var backupPath = BackupExistingDelivery(settings, output, safeName);
        var reports = Path.Combine(root, "HarmonyConfig", "RavenMapReports");
        Directory.CreateDirectory(reports);

        return new GenerationJob(
            root, exe, safeName, output, backupPath, reports,
            Path.Combine(reports, $"RavenMapReport_{settings.WorldSize}_{settings.Seed}.json"),
            Path.Combine(reports, $"RavenWorldObjectReport_{settings.WorldSize}_{settings.Seed}.json"),
            Path.Combine(root, "maps", $"CustomGenerator{settings.WorldSize}_{settings.Seed}.map"),
            Path.Combine(root, "mapimages", $"CustomGenerator{settings.WorldSize}_{settings.Seed}.png"));
    }

    private void PrepareGeneratorConfiguration(GenerationJob job, AppSettings settings)
    {
        var identityDir = Path.Combine(job.Root, "server", settings.Identity);
        Directory.CreateDirectory(identityDir);
        WriteWorldConfig(Path.Combine(identityDir, "harita_ayarlari.json"), settings);
        WritePlacementConfig(job.Root, settings);
        WriteTerrainDraftConfig(job.Root, settings);
    }

    private void ValidateGeneratedRules(GenerationJob job, AppSettings settings)
    {
        var main = MapReportReader.Read(job.MapReport, settings.WorldSize);
        var world = MapReportReader.Read(job.WorldReport, settings.WorldSize);
        var entries = new MapEntryResolver(catalog).Merge(main.Entries, world.Entries);
        var invalid = MapValidationService.Evaluate(entries.Select(x => x.Category), catalog)
            .Where(x => !x.IsValid)
            .ToList();

        if (invalid.Count == 0)
        {
            Log?.Invoke("Monument teslim kontrolü başarılı: etkin kurallar karşılandı.");
            return;
        }

        var details = invalid.Select(x => $"{x.Rule.Name}: bulunan {x.Count}, hedef {x.Mode}").ToList();
        if (settings.StrictMonumentRules)
        {
            throw new InvalidOperationException(
                "Harita üretildi ancak katı monument kontrolünü geçemedi; mevcut teslimin üzerine yazılmadı:" +
                Environment.NewLine + string.Join(Environment.NewLine, details.Take(8).Select(x => "• " + x)) +
                (details.Count > 8 ? $"{Environment.NewLine}• …ve {details.Count - 8} sorun daha" : ""));
        }

        Log?.Invoke($"UYARI: Monument teslim kontrolünde {invalid.Count} kural karşılanmadı. Uyarı modu açık olduğu için harita teslim ediliyor.");
        foreach (var detail in details.Take(8))
            Log?.Invoke("UYARI: " + detail);
    }

    private static void ResetExpectedOutputs(GenerationJob job)
    {
        DeleteOldReport(job.MapReport);
        DeleteOldReport(job.WorldReport);
        foreach (var name in new[]
        {
            $"RavenGodRockExactCount_{PathSeed(job.MapReport)}.json",
            "RavenGodRockExactCount_latest.json",
            $"RavenRequiredMonumentsValidation_{PathSeed(job.MapReport)}.json",
            "RavenRequiredMonumentsValidation_latest.json",
            $"RavenCustomPrefabRuntimeAudit_{PathSeed(job.MapReport)}.json",
            "RavenCustomPrefabRuntimeAudit_latest.json"
        })
            DeleteOldReport(Path.Combine(job.Reports, name));

        DeleteOldReport(job.MapExpected);
        DeleteOldReport(job.ImageExpected);
        DeleteOldReport(job.MapExpected + ".raven_before_guarantee.bak");
    }

    private static string PathSeed(string mapReportPath)
    {
        var file = Path.GetFileNameWithoutExtension(mapReportPath);
        const string prefix = "RavenMapReport_";
        return file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? file[prefix.Length..] : file;
    }

    private async Task RunRustGenerationAsync(GenerationJob job, AppSettings settings, CancellationToken cancellation, bool importMode = false)
    {
        var start = CreateRustStartInfo(job, settings);
        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data) && IsUseful(e.Data)) Log?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) Log?.Invoke(e.Data);
        };

        Log?.Invoke(importMode
            ? $"İçe aktarılan harita analiz ediliyor — Seed {settings.Seed}, Boyut {settings.WorldSize}…"
            : $"Harita üretiliyor — Seed {settings.Seed}, Boyut {settings.WorldSize}…");
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await WaitForOutputsAsync(process,
                new[] { job.MapExpected, job.ImageExpected, job.MapReport, job.WorldReport }, cancellation);
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(true); } catch { }
            }
        }
    }

    private static ProcessStartInfo CreateRustStartInfo(GenerationJob job, AppSettings settings)
    {
        var serverPort = GetAvailableTcpPort();
        var rconPort = GetAvailableTcpPort(serverPort);
        var rconPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var start = new ProcessStartInfo
        {
            FileName = job.Exe,
            WorkingDirectory = job.Root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            ErrorDialog = false
        };
        foreach (var arg in new[]
        {
            "-batchmode", "-nographics", "+server.identity", settings.Identity,
            "+server.level", "Procedural Map", "+server.seed", settings.Seed.ToString(),
            "+server.worldsize", settings.WorldSize.ToString(), "+server.ip", "127.0.0.1",
            "+server.port", serverPort.ToString(), "+rcon.port", rconPort.ToString(),
            "+rcon.password", rconPassword, "+rcon.web", "false",
            "+server.hostname", "Raven Native Generator", "+world.configfile", "harita_ayarlari.json"
        })
            start.ArgumentList.Add(arg);
        return start;
    }

    private static void PrepareImportConfiguration(string root, AppSettings settings)
    {
        var harmonyFolder = Path.Combine(root, "HarmonyConfig");
        Directory.CreateDirectory(harmonyFolder);
        var config = JsonNode.Parse(AssetStore.ReadText("config.CustomGenerator.default.json"))
            ?? throw new InvalidDataException("Harita motoru varsayılan ayarları okunamadı.");

        if (config["Map Settings"] is JsonObject mapSettings)
        {
            mapSettings["Generate new map everytime"] = false;
            mapSettings["Override Map Sizes (9000 not be changed to 6000)"] = true;
            mapSettings["Override Map Folder (saves to <Server Root>/maps/)"] = true;
            mapSettings["Override Map Name"] = true;
            mapSettings["Map Name ({0} - size, {1} - seed)"] = "CustomGenerator{0}_{1}";
        }

        if (config["Main Generator"] is JsonObject main)
        {
            foreach (var key in new[] { "Road", "Rail" })
                if (main[key] is JsonObject item) item["ShouldChange"] = false;
            if (main["UniqueEnviroment"] is JsonObject environment) environment["ShouldChange"] = false;
            main["Remove Rivers"] = false;
            main["Remove tunnel entrances"] = false;
            main["Change percentages"] = false;
        }
        if (config["Swap Monuments"] is JsonObject swap) swap["Enabled"] = false;
        if (config["Monuments"] is JsonObject monuments) monuments["Enabled"] = false;

        File.WriteAllText(Path.Combine(harmonyFolder, "CustomGenerator.json"),
            config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(harmonyFolder, "RavenGuaranteedPlacement.json"),
            JsonSerializer.Serialize(new
            {
                enabled = false,
                importAnalysis = true,
                terrainSafety = new
                {
                    fastCheck = settings.TerrainFastCheck,
                    deepCheck = settings.TerrainDeepCheck,
                    safeAutoRepair = settings.TerrainSafeAutoRepair,
                    fastBudgetMs = 750,
                    deepBudgetMs = 2500
                }
            }, new JsonSerializerOptions { WriteIndented = true }));

        var identityFolder = Path.Combine(root, "server", settings.Identity);
        Directory.CreateDirectory(identityFolder);
        File.WriteAllText(Path.Combine(identityFolder, "harita_ayarlari.json"),
            JsonSerializer.Serialize(new
            {
                MainRoads = true,
                SideRoads = true,
                Trails = true,
                Rivers = true,
                Powerlines = true,
                AboveGroundRails = true,
                BelowGroundRails = true,
                UnderwaterLabs = true,
                GenerateLakes = true,
                GenerateCanyons = true,
                GenerateOasis = true,
                PrefabBlacklist = Array.Empty<string>(),
                PrefabWhitelist = Array.Empty<string>()
            }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void RestoreConfiguration(string path, byte[]? original)
    {
        try
        {
            if (original is null) DeleteOldReport(path);
            else File.WriteAllBytes(path, original);
        }
        catch
        {
            // Bir sonraki normal üretim bu dosyaları yeniden kurar; analiz sonucunu bozma.
        }
    }

    private static async Task WaitForOutputsAsync(Process process, IReadOnlyCollection<string> requiredOutputs, CancellationToken cancellation)
    {
        var deadline = DateTime.UtcNow.AddMinutes(30);
        while (DateTime.UtcNow < deadline)
        {
            cancellation.ThrowIfCancellationRequested();
            if (requiredOutputs.All(File.Exists) && await AreStable(requiredOutputs, cancellation)) return;
            if (process.HasExited)
            {
                var missing = requiredOutputs.Where(x => !File.Exists(x)).Select(Path.GetFileName);
                throw new InvalidOperationException($"Harita üretimi tamamlanamadı (çıkış kodu {process.ExitCode}). Eksik çıktı: {string.Join(", ", missing)}");
            }
            await Task.Delay(2000, cancellation);
        }
        throw new TimeoutException("Harita, görsel ve doğrulama raporları 30 dakika içinde tamamlanmadı.");
    }

    private GenerationResult DeliverOutputs(GenerationJob job, AppSettings settings)
    {
        Log?.Invoke("Yeni ikonlar doğru koordinatlara işleniyor…");
        Directory.CreateDirectory(job.Output);
        var mapOut = Path.Combine(job.Output, job.SafeName + ".map");
        var imageOut = Path.Combine(job.Output, job.SafeName + ".png");
        File.Copy(job.MapExpected, mapOut, true);
        var counts = new OverlayService(catalog).Render(job.ImageExpected, imageOut, job.Reports,
            settings.WorldSize, settings.Seed, settings.IconSize);

        var archivedRawImage = Path.Combine(job.Output, "source.png");
        File.Copy(job.ImageExpected, archivedRawImage, true);
        var archivedReports = Path.Combine(job.Output, "Reports");
        SnapshotReports(job.Reports, archivedReports, settings.WorldSize, settings.Seed);
        File.WriteAllText(Path.Combine(job.Output, job.SafeName + ".ravenmap"),
            JsonSerializer.Serialize(ProjectService.ToDocument(settings),
                new JsonSerializerOptions { WriteIndented = true }));

        Log?.Invoke("MAP ve PNG otomatik kaydedildi: " + job.Output);
        LogValidationSummary(counts.GodRocks);
        LogTerrainSafetySummary(job.WorldReport);
        Log?.Invoke($"Tamamlandı: {counts.Icons} ikon, {counts.GodRocks} doğrulanmış God Rock.");
        Log?.Invoke(settings.StrictMonumentRules
            ? "Teslim modu: Katı kontrol. Etkin monument kuralları geçmeden MAP teslim edilmez."
            : "Teslim modu: Uyarı. Eksik monument kuralları raporlanır ve MAP teslim edilir.");
        return new GenerationResult(mapOut, imageOut, job.Output, counts.Icons, counts.GodRocks,
            archivedRawImage, archivedReports, settings.WorldSize, settings.Seed, job.BackupPath);
    }

    private void CleanupWorkingOutputs(GenerationJob job)
    {
        // Rust maps/mapimages klasörleri yalnız çalışma alanıdır. Teslim edilen MAP,
        // işlenmiş PNG, ham source.png ve raporlar job.Output içine başarıyla
        // kopyalandıktan sonra sadece bu seed'e ait kesin yollar temizlenir.
        var paths = new[]
        {
            job.MapExpected,
            job.MapExpected + ".raven_before_guarantee.bak",
            job.ImageExpected
        };

        var removed = 0;
        foreach (var path in paths)
        {
            try
            {
                if (!File.Exists(path)) continue;
                File.Delete(path);
                removed++;
            }
            catch (Exception ex)
            {
                // Teslim başarıyla tamamlandıysa çalışma alanı temizleme hatası
                // kullanıcı haritasını başarısız saymamalı.
                Log?.Invoke($"UYARI: Geçici üretim dosyası temizlenemedi: {Path.GetFileName(path)} — {ex.Message}");
            }
        }

        if (removed > 0)
            Log?.Invoke($"Rust çalışma alanı temizlendi: {removed} geçici dosya kaldırıldı.");
    }

    private void LogValidationSummary(int godRocks)
    {
        var godRule = catalog.FirstOrDefault(x => x.Id.Equals("god_rocks", StringComparison.OrdinalIgnoreCase));
        if (godRule?.State == RuleState.Required)
        {
            var target = Math.Clamp(godRule.Minimum > 0 ? godRule.Minimum : 1, 1, 10);
            Log?.Invoke($"God Rock hedefi: {target} / doğrulanan: {godRocks} {(godRocks == target ? "✓" : "HATA")}");
        }
        else if (godRule?.State == RuleState.Blocked)
            Log?.Invoke($"God Rock hedefi: 0 / doğrulanan: {godRocks} {(godRocks == 0 ? "✓" : "HATA")}");
    }

    private void LogTerrainSafetySummary(string reportPath)
    {
        try
        {
            if (!File.Exists(reportPath)) return;
            using var report = JsonDocument.Parse(File.ReadAllText(reportPath));
            if (!report.RootElement.TryGetProperty("terrainSafety", out var safety)
                || !safety.TryGetProperty("enabled", out var enabled) || !enabled.GetBoolean()) return;
            var mode = safety.TryGetProperty("mode", out var modeNode) && modeNode.GetString() == "deep" ? "Derin" : "Hızlı";
            var checkedCount = safety.TryGetProperty("checked", out var checkedNode) ? checkedNode.GetInt32() : 0;
            var elapsed = safety.TryGetProperty("elapsedMs", out var elapsedNode) ? elapsedNode.GetInt64() : 0;
            var review = safety.TryGetProperty("repairCandidates", out var reviewNode) ? reviewNode.GetInt32() : 0;
            var budgetExceeded = safety.TryGetProperty("budgetExceeded", out var budgetNode) && budgetNode.GetBoolean();
            Log?.Invoke($"{mode} arazi kontrolü: {checkedCount} taş kökü, {elapsed} ms, inceleme önerisi {review}."
                + (budgetExceeded ? " Süre bütçesi doldu; kalanlar atlandı." : ""));
            if (review > 0)
                Log?.Invoke("Arazi güvenliği: bilinmeyen taşlar otomatik değiştirilmedi; ayrıntılar Doğrulama ekranında.");
        }
        catch
        {
            // Terrain safety is an advisory report. A malformed optional section
            // must never turn a successfully generated map into a failed delivery.
        }
    }

    private sealed record GenerationJob(
        string Root, string Exe, string SafeName, string Output, string BackupPath, string Reports,
        string MapReport, string WorldReport, string MapExpected, string ImageExpected);

    private static void ValidateHarmonyRuntime(string root)
    {
        var managed = Path.Combine(root, "RustDedicated_Data", "Managed");
        var harmony = Path.Combine(managed, "0Harmony.dll");
        var loader = Path.Combine(managed, "Rust.Harmony.dll");
        if (File.Exists(harmony) && File.Exists(loader)) return;

        throw new InvalidOperationException(
            "Seçilen Rust sunucusunda yerleşik Harmony çalışma dosyaları bulunamadı. " +
            "RustDedicated_Data\\Managed altında 0Harmony.dll ve Rust.Harmony.dll olmalı. " +
            "Raven Map Panel > Ayarlar bölümündeki Harita Motorunu Kur / Doğrula-Onar seçeneğini çalıştırın. " +
            "Raven mod DLL'lerini elle kopyalamanız gerekmez; uygulama onları otomatik kurar.");
    }

    private static string BackupExistingDelivery(AppSettings settings, string output, string safeName)
    {
        if (!Directory.Exists(output) || !Directory.EnumerateFileSystemEntries(output).Any())
            return "";

        var backupRoot = Path.Combine(SettingsStore.OutputRoot(settings), "_Backups");
        Directory.CreateDirectory(backupRoot);
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var backup = Path.Combine(backupRoot, $"{safeName}_{settings.WorldSize}_{settings.Seed}_{stamp}");
        var suffix = 1;
        while (Directory.Exists(backup))
            backup = Path.Combine(backupRoot, $"{safeName}_{settings.WorldSize}_{settings.Seed}_{stamp}_{suffix++}");

        try
        {
            CopyDirectory(output, Path.Combine(backup, "Harita"));
            return backup;
        }
        catch (Exception ex)
        {
            try { if (Directory.Exists(backup)) Directory.Delete(backup, true); } catch { }
            throw new InvalidOperationException(
                "Aynı isim/seed için mevcut harita güvenli şekilde yedeklenemedi. " +
                "Eski çıktının üzerine yazmamak için üretim durduruldu. Çıktı klasörünün izinlerini ve boş disk alanını kontrol edin.", ex);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private static void SnapshotReports(string source, string destination, int size, int seed)
    {
        Directory.CreateDirectory(destination);
        var names = new[]
        {
            $"RavenMapReport_{size}_{seed}.json",
            $"RavenWorldObjectReport_{size}_{seed}.json",
            $"RavenGodRockExactCount_{size}_{seed}.json",
            $"RavenRequiredMonumentsValidation_{size}_{seed}.json",
            $"RavenCustomPrefabSelection_{size}_{seed}.json",
            "RavenCustomPrefabSelection_latest.json",
            $"RavenCustomPrefabRuntimeAudit_{size}_{seed}.json",
            "RavenCustomPrefabRuntimeAudit_latest.json"
        };
        foreach (var name in names)
        {
            var file = Path.Combine(source, name);
            if (File.Exists(file))
                File.Copy(file, Path.Combine(destination, name), true);
        }
    }

    private static void WriteCustomGenerator(string root, AppSettings s, bool swapEnabled)
    {
        var folder = Path.Combine(root, "HarmonyConfig"); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "CustomGenerator.json");
        JsonNode cfg;
        try { cfg = JsonNode.Parse(File.Exists(path) ? File.ReadAllText(path) : AssetStore.ReadText("config.CustomGenerator.default.json"))!; }
        catch { cfg = JsonNode.Parse(AssetStore.ReadText("config.CustomGenerator.default.json"))!; }

        var main = cfg["Main Generator"]!.AsObject();
        SetEnabled(main, "Road", s.Roads); SetEnabled(main, "Rail", s.Rails);
        main["Remove Rivers"] = !s.Rivers;
        main["Remove tunnel entrances"] = !s.UndergroundRails;
        main["Change percentages"] = true;

        if (main["UniqueEnviroment"] is JsonObject env)
        {
            env["ShouldChange"] = true;
            env["GenerateOasis"] = s.Oasis;
            env["GenerateCanyons"] = s.Canyons;
            env["GenerateLakes"] = s.Lakes;
        }

        if (main["Tier Percentages (100 in total)"] is not JsonObject tier)
            main["Tier Percentages (100 in total)"] = tier = [];
        tier["Tier0"] = ClampPercent(s.Tier0);
        tier["Tier1"] = ClampPercent(s.Tier1);
        tier["Tier2"] = ClampPercent(s.Tier2);

        const string biomeKey = "Bioms Percentages (100 in total) - idk why jungle 70%";
        if (main[biomeKey] is not JsonObject biome)
            main[biomeKey] = biome = [];
        biome["Arid"] = ClampPercent(s.BiomeArid);
        biome["Temperate"] = ClampPercent(s.BiomeTemperate);
        biome["Tundra"] = ClampPercent(s.BiomeTundra);
        biome["Arctic"] = ClampPercent(s.BiomeArctic);
        biome["Jungle"] = ClampPercent(s.BiomeJungle);

        if (cfg["Swap Monuments"] is not JsonObject swap)
            cfg["Swap Monuments"] = swap = [];
        swap["Enabled"] = swapEnabled;
        swap["Save both maps (with swap and without)"] = false;

        File.WriteAllText(path, cfg.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static double ClampPercent(double value) => Math.Clamp(double.IsFinite(value) ? value : 0d, 0d, 100d);

    private static void SetEnabled(JsonObject main, string key, bool value)
    {
        if (main[key] is not JsonObject obj) main[key] = obj = [];
        obj["ShouldChange"] = true; obj["Enabled"] = value; obj["GenerateRing"] = value; obj["GenerateSideMonuments"] = value;
    }

    private void WriteWorldConfig(string path, AppSettings s)
    {
        var blocked = catalog.Where(x => x.State == RuleState.Blocked).SelectMany(x => x.Prefabs).Distinct().ToArray();
        var data = new Dictionary<string, object> {
            ["MainRoads"] = s.Roads, ["SideRoads"] = s.Roads, ["Trails"] = s.Roads, ["Rivers"] = s.Rivers,
            ["Powerlines"] = s.PowerLines, ["AboveGroundRails"] = s.Rails, ["BelowGroundRails"] = s.UndergroundRails,
            ["UnderwaterLabs"] = catalog.FirstOrDefault(x => x.Id == "underwater_labs")?.State != RuleState.Blocked,
            ["GenerateLakes"] = s.Lakes, ["GenerateCanyons"] = s.Canyons, ["GenerateOasis"] = s.Oasis,
            ["PercentageTier0"] = ClampPercent(s.Tier0) / 100d, ["PercentageTier1"] = ClampPercent(s.Tier1) / 100d, ["PercentageTier2"] = ClampPercent(s.Tier2) / 100d,
            ["PercentageBiomeArid"] = ClampPercent(s.BiomeArid) / 100d, ["PercentageBiomeTemperate"] = ClampPercent(s.BiomeTemperate) / 100d, ["PercentageBiomeTundra"] = ClampPercent(s.BiomeTundra) / 100d,
            ["PercentageBiomeArctic"] = ClampPercent(s.BiomeArctic) / 100d, ["PercentageBiomeJungle"] = ClampPercent(s.BiomeJungle) / 100d,
            ["PrefabBlacklist"] = blocked, ["PrefabWhitelist"] = Array.Empty<string>()
        };
        File.WriteAllText(path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void WritePlacementConfig(string root, AppSettings s)
    {
        var policyDocument = GenerationPolicyStore.Load();
        var rules = catalog.Where(x => x.State is RuleState.Required or RuleState.Blocked).Select(x =>
        {
            var policy = policyDocument.For(x.Id);
            return new
            {
                id = x.Id,
                name = x.Name,
                state = x.State == RuleState.Required ? "required" : "blocked",
                min = x.State == RuleState.Required ? Math.Max(1, x.Minimum) : 0,
                max = x.State == RuleState.Blocked ? 0 : (x.Id == "god_rocks" ? Math.Max(1, x.Minimum) : Math.Max(Math.Max(1, x.Minimum), x.Maximum)),
                prefabMarkers = x.Prefabs.Distinct().ToArray(),
                spawnPrefab = SpawnPrefab(x.Id, x.Prefabs),
                placement = x.Id == "god_rocks" ? "native_prefab_before_terrain_anchors" : "native_generator_only",
                generatorOnly = true,
                removeWhenBlocked = IsSafeBlockedRootRemoval(x.Id),
                minDistance = policy.MinDistance,
                flatRadius = policy.FlatRadius,
                maxHeightDelta = policy.MaxHeightDelta
            };
        }).ToArray();

        var folder = Path.Combine(root, "HarmonyConfig");
        Directory.CreateDirectory(folder);
        var payload = new
        {
            enabled = rules.Any(),
            singleSeed = true,
            maxSeedAttempts = 1,
            alwaysDeliver = !s.StrictMonumentRules,
            jobId = $"native-{DateTime.UtcNow:yyyyMMddHHmmss}",
            seed = s.Seed,
            worldSize = s.WorldSize,
            auditPath = Path.Combine(folder, "RavenGuaranteedPlacementAudits", $"native_{s.WorldSize}_{s.Seed}.json"),
            mapPath = Path.Combine(root, "maps", $"CustomGenerator{s.WorldSize}_{s.Seed}.map"),
            terrainSafety = new
            {
                fastCheck = s.TerrainFastCheck,
                deepCheck = s.TerrainDeepCheck,
                safeAutoRepair = s.TerrainSafeAutoRepair,
                fastBudgetMs = 750,
                deepBudgetMs = 2500
            },
            rules
        };
        File.WriteAllText(Path.Combine(folder, "RavenGuaranteedPlacement.json"),
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WriteTerrainDraftConfig(string root, AppSettings settings)
    {
        var folder = Path.Combine(root, "HarmonyConfig");
        Directory.CreateDirectory(folder);
        var draft = settings.TerrainDraft ?? new TerrainDraft();
        var strokes = (draft.Strokes ?? []).Select(x => new
        {
            strokeId = x.StrokeId,
            kind = x.Kind.ToString().ToLowerInvariant(),
            x = Math.Clamp(x.X, 0d, 1d),
            y = Math.Clamp(x.Y, 0d, 1d),
            radius = Math.Clamp(x.Radius, 0.005d, 0.5d),
            strength = Math.Clamp(x.Strength, 0.01d, 1d)
        }).ToArray();
        var payload = new
        {
            format = "raven-terrain-draft-v1",
            enabled = draft.Enabled && strokes.Length > 0,
            previewOnly = true,
            worldSize = settings.WorldSize,
            seed = settings.Seed,
            brushRadius = draft.BrushRadius,
            strokes
        };
        File.WriteAllText(Path.Combine(folder, "RavenTerrainDraft.json"),
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static bool IsSafeBlockedRootRemoval(string id) =>
        id is not "caves"
        && !id.StartsWith("cave_", StringComparison.OrdinalIgnoreCase)
        && id is not "power_substations"
        && !id.StartsWith("power_sub_", StringComparison.OrdinalIgnoreCase)
        && id is not ("powerlines" or "metro_entrances" or
            "swamps" or "lakes" or "canyons" or "oases");

    private static string SpawnPrefab(string id, List<string> prefabs)
    {
        var direct = prefabs.FirstOrDefault(x => x.StartsWith("assets/", StringComparison.OrdinalIgnoreCase));
        if (direct is not null) return direct;
        return id switch {
            "military_tunnels" => "assets/bundled/prefabs/autospawn/monument/large/military_tunnel_1.prefab",
            "powerplant" => "assets/bundled/prefabs/autospawn/monument/large/powerplant_1.prefab",
            "water_treatment" => "assets/bundled/prefabs/autospawn/monument/large/water_treatment_plant_1.prefab",
            "excavator" => "assets/bundled/prefabs/autospawn/monument/large/excavator_1.prefab",
            "junkyard" => "assets/bundled/prefabs/autospawn/monument/medium/junkyard_1.prefab",
            "nuclear_silo" => "assets/bundled/prefabs/autospawn/monument/medium/nuclear_missile_silo.prefab",
            "arctic_research_base" => "assets/bundled/prefabs/autospawn/monument/arctic_bases/arctic_research_base_a.prefab",
            "desert_military_base" => "assets/bundled/prefabs/autospawn/monument/military_bases/desert_military_base_a.prefab",
            "ferry_terminal" => "assets/bundled/prefabs/autospawn/monument/harbor/ferry_terminal_1.prefab",
            "satellite_dish" => "assets/bundled/prefabs/autospawn/monument/small/satellite_dish.prefab",
            "dome" => "assets/bundled/prefabs/autospawn/monument/small/sphere_tank.prefab",
            "ziggurat" => "assets/bundled/prefabs/autospawn/monument/jungle_ruins/jungle_ziggurat_a.prefab",
            "radtown" => "assets/bundled/prefabs/autospawn/monument/roadside/radtown_1.prefab",
            "supermarket" => "assets/bundled/prefabs/autospawn/monument/roadside/supermarket_1.prefab",
            "gas_station" => "assets/bundled/prefabs/autospawn/monument/roadside/gas_station_1.prefab",
            "sewer_branch" => "assets/bundled/prefabs/autospawn/monument/medium/radtown_small_3.prefab",
            "warehouse" => "assets/bundled/prefabs/autospawn/monument/roadside/warehouse.prefab",
            "outpost" => "assets/bundled/prefabs/autospawn/monument/medium/compound.prefab",
            "bandit_camp" => "assets/bundled/prefabs/autospawn/monument/medium/bandit_town.prefab",
            "harbor" => "assets/bundled/prefabs/autospawn/monument/harbor/harbor_1.prefab",
            "lighthouse" => "assets/bundled/prefabs/autospawn/monument/lighthouse/lighthouse.prefab",
            "stone_quarry" => "assets/bundled/prefabs/autospawn/monument/small/mining_quarry_b.prefab",
            "sulfur_quarry" => "assets/bundled/prefabs/autospawn/monument/small/mining_quarry_a.prefab",
            "hqm_quarry" => "assets/bundled/prefabs/autospawn/monument/small/mining_quarry_c.prefab",
            _ => ""
        };
    }


    private static void DeleteOldReport(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    private static async Task<bool> AreStable(IReadOnlyCollection<string> paths, CancellationToken token)
    {
        var first = paths.ToDictionary(x => x, x => new FileInfo(x).Length, StringComparer.OrdinalIgnoreCase);
        if (first.Any(x => x.Value <= (x.Key.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? 2 : 1024)))
            return false;
        await Task.Delay(1200, token);
        return paths.All(path => File.Exists(path) && new FileInfo(path).Length == first[path]);
    }

    private static int GetAvailableTcpPort(int excludedPort = -1)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            try
            {
                listener.Start();
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                if (port != excludedPort) return port;
            }
            finally
            {
                listener.Stop();
            }
        }
        throw new InvalidOperationException("Harita motoru için boş bir yerel port bulunamadı.");
    }
    private static bool IsUseful(string line) => line.Contains("Raven", StringComparison.OrdinalIgnoreCase) || line.Contains("CustomGenerator", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Generating", StringComparison.OrdinalIgnoreCase) || line.Contains("Map", StringComparison.OrdinalIgnoreCase);
}
