using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RavenMapPanel;

internal sealed class CustomPrefabService
{
    private static readonly object RemoteGate = new();
    private static IReadOnlyList<CustomPrefabVariant> entitledVariants = [];

    public static void SetEntitledVariants(IReadOnlyList<CustomPrefabVariant> variants)
    {
        lock (RemoteGate) entitledVariants = variants.ToList();
    }
    private sealed class DefinitionDocument
    {
        public string Format { get; set; } = "";
        public List<Definition> Definitions { get; set; } = [];
    }

    private sealed class Definition
    {
        public string MonumentId { get; set; } = "";
        public List<string> Targets { get; set; } = [];
    }

    private sealed class RecoveryManifest
    {
        public string Format { get; set; } = "raven-prefab-recovery-v1";
        public string TargetDirectory { get; set; } = "";
        public List<RecoveryBackup> Backups { get; set; } = [];
        public List<string> Installed { get; set; } = [];
    }

    private sealed class RecoveryBackup
    {
        public string Original { get; set; } = "";
        public string Backup { get; set; } = "";
    }

    private readonly DefinitionDocument definitions;
    private readonly Dictionary<string, Definition> byMonument;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    // v1.4.7-v1.4.9 sürümlerinin otomatik çıkardığı eski gömülü prefablar.
    // Sadece hash birebir eşleşirse migration sırasında atılır. Kullanıcı dosyayı
    // değiştirmişse hash değişeceği için dosya korunur.
    private static readonly HashSet<string> LegacyGeneratedPrefabHashes =
    [
        "62F75DFE35BBEBF16A5B077C7C75F6E7C007CFD00761B7F955774A211600BA4C",
        "6471CF7D1F2BAD74230E22BF4F2CF4E1B01DC6B5E8C06F9F7E9FD95F46BA6929",
        "8553A6D8960C275780FB82CC03282E217125E664474D6D9BE321021CE700569B",
        "DD4B83F52DC73F6AAE7D44C6A242D99DFD37C72E4C1900DB64DD5FEFF0EC0AC5",
        "E7DA979DCDE5359C061079BFEDA3F3F7D93D7D9D3941BF3B93E6ED9FAEBC479B",
        "92E48CF62D68C3097F6FA00D219DC58F7783CF4223222C84E315075B91548AC3",
        "AB1B592236CB6B8DAD7D4BE35367A66F8D2C966B8BB53FCA87C53BCE675F6513"
    ];

    public CustomPrefabService()
    {
        definitions = JsonSerializer.Deserialize<DefinitionDocument>(
            AssetStore.ReadText("data.custom_prefab_targets.json"), JsonOptions) ?? new();

        byMonument = definitions.Definitions
            .Where(x => !string.IsNullOrWhiteSpace(x.MonumentId))
            .GroupBy(x => x.MonumentId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
    }

    public string Root => SettingsStore.CustomPrefabsRoot;
    public string FolderFor(string monumentId) => Path.Combine(Root, SafeSegment(monumentId));

    // Eski çağrıları bozmamak için isim korunuyor; artık gömülü prefab kopyalamaz.
    public void EnsureBundledDefaults()
    {
        // v1.5: prefab kaynakları istemcide tutulmaz. Yetkili liste Raven Server'dan gelir.
    }

    public IReadOnlyList<CustomPrefabVariant> GetVariants(string monumentId)
    {
        lock (RemoteGate)
            return entitledVariants.Where(x => x.MonumentId.Equals(monumentId,StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public CustomPrefabVariant? FindVariant(string monumentId, string variantId)
    {
        if (string.IsNullOrWhiteSpace(variantId))
            return null;

        var variants = GetVariants(monumentId);

        var exact = variants.FirstOrDefault(
            x => x.Id.Equals(variantId, StringComparison.OrdinalIgnoreCase));

        if (exact is not null)
            return exact;

        // v1.4.x projelerindeki "custom/custom1" seçimini ilk mevcut dosyaya taşı.
        if (IsLegacySelectionId(variantId))
            return variants.FirstOrDefault();

        return null;
    }

    public async Task<Session> PrepareSessionAsync(
        string rustRoot,
        AppSettings settings,
        IReadOnlyList<MonumentRule> catalog,
        LicenseService licenseService,
        Action<string>? log,
        CancellationToken cancellation)
    {
        EnsureBundledDefaults();
        RecoverInterruptedSessions(rustRoot, log);

        var targetDir = Path.Combine(rustRoot, "maps", "prefabs");
        var backupDir = Path.Combine(
            rustRoot,
            "HarmonyConfig",
            "RavenPrefabBackups",
            $"{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}");

        Directory.CreateDirectory(targetDir);
        Directory.CreateDirectory(backupDir);

        var session = new Session(targetDir, backupDir);
        try
        {
            foreach (var existing in Directory.EnumerateFiles(targetDir, "*", SearchOption.TopDirectoryOnly).ToArray())
            {
                var backup = Path.Combine(backupDir, Path.GetFileName(existing));
                File.Copy(existing, backup, true);
                // Manifest dosyası silme işleminden ÖNCE güncellenir. Uygulama tam bu
                // noktada kapanırsa sonraki açılış hangi dosyanın geri konacağını bilir.
                session.RegisterBackup(existing, backup);
                File.Delete(existing);
            }

            foreach (var rule in catalog.Where(x => x.CustomPrefabAvailable))
            {
                var selectedId = ResolveSelectedVariantId(settings, rule.Id);
                if (string.IsNullOrWhiteSpace(selectedId))
                    continue;

                session.SelectedMonumentCount++;

                if (rule.State == RuleState.Blocked)
                {
                    session.Skipped.Add((rule.Id, selectedId, "Monument kuralı Olmasın durumunda."));
                    continue;
                }

                var variant = FindVariant(rule.Id, selectedId);
                if (variant is null)
                {
                    session.Skipped.Add((rule.Id, selectedId, "Seçili prefab dosyası artık bulunamadı."));
                    log?.Invoke($"UYARI: {rule.Name} için seçili prefab bulunamadı. Vanilla kullanılacak.");
                    continue;
                }

                if (!variant.IsValid)
                {
                    session.Skipped.Add((rule.Id, selectedId, variant.ValidationMessage));
                    log?.Invoke($"UYARI: {rule.Name} / {variant.Name} geçersiz: {variant.ValidationMessage}. Vanilla kullanılacak.");
                    continue;
                }

                foreach (var target in variant.Targets)
                {
                    // Kullanıcının dosya adı serbesttir.
                    // Rust/Harmony tarafına giderken gerçek vanilla target adına çevrilir.
                    var destination = Path.Combine(targetDir, Path.GetFileName(target.TargetFileName));
                    session.RegisterInstalled(destination);
                    if (!target.ContentId.Equals("",StringComparison.Ordinal))
                    {
                        var bytes=await licenseService.DownloadPrefabAsync(target.ContentId,target.Sha256,cancellation);
                        try { await File.WriteAllBytesAsync(destination,bytes,cancellation); }
                        finally { CryptographicOperations.ZeroMemory(bytes); }
                    }
                    else File.Copy(target.SourcePath, destination, true);

                    var metadataPath = destination + ".raven.json";
                    session.RegisterInstalled(metadataPath);
                    var metadata = new
                    {
                        format = "raven-swap-metadata-v4",
                        monumentId = rule.Id,
                        variantId = variant.Id,
                        variantName = variant.Name,
                        sourceFile = variant.IsRemote ? "protected:"+target.ContentId : Path.GetFileName(target.SourcePath),
                        targetFile = Path.GetFileName(target.TargetFileName),
                        anchor = target.Anchor is null ? null : new
                        {
                            x = target.Anchor.X,
                            y = target.Anchor.Y,
                            z = target.Anchor.Z,
                            yaw = target.Anchor.Yaw
                        }
                    };

                    File.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata, JsonOptions));
                }

                session.Applied.Add((rule.Id, rule.Name, variant.Id, variant.Name, variant.Targets.Count));
                log?.Invoke($"Özel prefab: {rule.Name} → {variant.Name} ({variant.Targets.Count} target)");
            }

            WriteAudit(rustRoot, settings, session);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private static void RecoverInterruptedSessions(string rustRoot, Action<string>? log)
    {
        var backupRoot = Path.Combine(rustRoot, "HarmonyConfig", "RavenPrefabBackups");
        if (!Directory.Exists(backupRoot)) return;

        var defaultTargetDir = Path.Combine(rustRoot, "maps", "prefabs");
        Directory.CreateDirectory(defaultTargetDir);

        foreach (var sessionDir in Directory.EnumerateDirectories(backupRoot).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray())
        {
            try
            {
                var manifestPath = Path.Combine(sessionDir, "recovery.json");
                RecoveryManifest? manifest = null;
                if (File.Exists(manifestPath))
                {
                    try { manifest = JsonSerializer.Deserialize<RecoveryManifest>(File.ReadAllText(manifestPath), JsonOptions); }
                    catch { manifest = null; }
                }

                var targetDir = !string.IsNullOrWhiteSpace(manifest?.TargetDirectory)
                    ? Path.GetFullPath(manifest.TargetDirectory)
                    : defaultTargetDir;
                Directory.CreateDirectory(targetDir);

                if (manifest is not null)
                {
                    foreach (var installed in manifest.Installed.AsEnumerable().Reverse())
                    {
                        if (File.Exists(installed)) File.Delete(installed);
                    }
                    foreach (var backup in manifest.Backups.AsEnumerable().Reverse())
                    {
                        if (!File.Exists(backup.Backup)) continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(backup.Original)!);
                        File.Copy(backup.Backup, backup.Original, true);
                    }
                }
                else
                {
                    // v1.5.0 ve daha eski sürüm yarım kaldıysa manifest yoktur. Raven'ın
                    // bıraktığı metadata dosyalarından geçici targetları temizle, ardından
                    // backup klasöründeki orijinalleri geri koy.
                    foreach (var metadata in Directory.EnumerateFiles(targetDir, "*.raven.json", SearchOption.TopDirectoryOnly).ToArray())
                    {
                        var target = metadata[..^".raven.json".Length];
                        if (File.Exists(target)) File.Delete(target);
                        File.Delete(metadata);
                    }
                    foreach (var backup in Directory.EnumerateFiles(sessionDir, "*", SearchOption.TopDirectoryOnly))
                    {
                        var backupName = Path.GetFileName(backup);
                        if (backupName.StartsWith("recovery.json", StringComparison.OrdinalIgnoreCase)) continue;
                        File.Copy(backup, Path.Combine(targetDir, backupName), true);
                    }
                }

                Directory.Delete(sessionDir, true);
                log?.Invoke("Yarım kalmış özel prefab oturumu otomatik kurtarıldı; kullanıcı prefabları geri yüklendi.");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Önceki Raven özel prefab oturumu güvenli şekilde geri yüklenemedi: {sessionDir}. " +
                    "Yeni üretim başlatılmadı; backup dosyaları korunuyor. " + ex.Message, ex);
            }
        }
    }

    public string ResolveSelectedVariantId(AppSettings settings, string ruleId)
    {
        if (!settings.Rules.TryGetValue(ruleId, out var saved))
            return "";

        var requested = (saved.CustomPrefabVariantId ?? "").Trim();

        if (string.IsNullOrWhiteSpace(requested) && !saved.UseCustomPrefab)
            return "";

        var variants = GetVariants(ruleId);
        if (variants.Count == 0)
            return "";

        if (!string.IsNullOrWhiteSpace(requested))
        {
            var exact = variants.FirstOrDefault(
                x => x.Id.Equals(requested, StringComparison.OrdinalIgnoreCase));

            if (exact is not null)
                return exact.Id;

            if (!IsLegacySelectionId(requested))
                return "";
        }

        // Eski boolean/"custom" kayıtları için ilk mevcut prefabı seç.
        return variants[0].Id;
    }

    private static CustomPrefabVariant BuildSingleFileVariant(
        string monumentId,
        string folder,
        string sourcePath,
        string targetFileName)
    {
        var fileName = Path.GetFileName(sourcePath);
        var displayName = DisplayNameFromPrefabFile(fileName);

        return new CustomPrefabVariant
        {
            MonumentId = monumentId,
            Id = fileName,
            Name = displayName,
            FolderPath = folder,
            PreviewPath = FindPreviewForFile(sourcePath),
            Targets =
            [
                new CustomPrefabTarget
                {
                    SourcePath = sourcePath,
                    TargetFileName = targetFileName
                }
            ],
            IsValid = true,
            ValidationMessage = $"Hazır: {fileName} → {targetFileName}"
        };
    }

    private static IReadOnlyList<CustomPrefabVariant> BuildMultiTargetVariants(
        string monumentId,
        string folder,
        IReadOnlyList<string> targets)
    {
        var result = new List<CustomPrefabVariant>();
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
            .Where(IsPrefabMapFile)
            .ToList();

        // Exact target isimleri aynı klasördeyse tek bir set olarak sun.
        var exactTargets = targets
            .Select(target => new
            {
                Target = target,
                Source = files.FirstOrDefault(
                    file => Path.GetFileName(file).Equals(target, StringComparison.OrdinalIgnoreCase))
            })
            .ToList();

        if (exactTargets.All(x => x.Source is not null))
        {
            result.Add(new CustomPrefabVariant
            {
                MonumentId = monumentId,
                Id = "__direct__",
                Name = monumentId,
                FolderPath = folder,
                PreviewPath = FindPreviewForVariant(folder, monumentId),
                Targets = exactTargets
                    .Select(x => new CustomPrefabTarget
                    {
                        SourcePath = x.Source!,
                        TargetFileName = x.Target
                    })
                    .ToList(),
                IsValid = true,
                ValidationMessage = $"Hazır: {targets.Count} dosyalık set"
            });
        }

        // variant__target biçimini grupla.
        var groups = new Dictionary<string, Dictionary<string, string>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var display = DisplayNameFromPrefabFile(Path.GetFileName(file));

            foreach (var target in targets)
            {
                var targetStem = DisplayNameFromPrefabFile(target);
                var suffix = "__" + targetStem;

                if (!display.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    continue;

                var variantName = display[..^suffix.Length].Trim();
                if (string.IsNullOrWhiteSpace(variantName))
                    continue;

                if (!groups.TryGetValue(variantName, out var group))
                {
                    group = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    groups[variantName] = group;
                }

                group[target] = file;
                break;
            }
        }

        foreach (var (variantName, group) in groups)
        {
            var missing = targets.Where(target => !group.ContainsKey(target)).ToList();
            if (missing.Count > 0)
                continue;

            result.Add(new CustomPrefabVariant
            {
                MonumentId = monumentId,
                Id = "set:" + variantName,
                Name = variantName,
                FolderPath = folder,
                PreviewPath = FindPreviewForVariant(folder, variantName),
                Targets = targets.Select(target => new CustomPrefabTarget
                {
                    SourcePath = group[target],
                    TargetFileName = target
                }).ToList(),
                IsValid = true,
                ValidationMessage = $"Hazır: {targets.Count} dosyalık {variantName} seti"
            });
        }

        return result
            .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsPrefabMapFile(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".prefab.map", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".map", StringComparison.OrdinalIgnoreCase);
    }

    private static string DisplayNameFromPrefabFile(string fileName)
    {
        if (fileName.EndsWith(".prefab.map", StringComparison.OrdinalIgnoreCase))
            return fileName[..^".prefab.map".Length];

        if (fileName.EndsWith(".map", StringComparison.OrdinalIgnoreCase))
            return fileName[..^".map".Length];

        return Path.GetFileNameWithoutExtension(fileName);
    }

    private static string FindPreviewForFile(string mapPath)
    {
        var folder = Path.GetDirectoryName(mapPath) ?? "";
        var fileName = Path.GetFileName(mapPath);
        var display = DisplayNameFromPrefabFile(fileName);

        string[] candidates =
        [
            Path.Combine(folder, display + ".png"),
            Path.Combine(folder, display + ".jpg"),
            Path.Combine(folder, display + ".jpeg"),
            Path.Combine(folder, display + ".webp"),
            mapPath + ".png"
        ];

        return candidates.FirstOrDefault(File.Exists) ?? "";
    }

    private static string FindPreviewForVariant(string folder, string variantName)
    {
        string[] candidates =
        [
            Path.Combine(folder, variantName + ".png"),
            Path.Combine(folder, variantName + ".jpg"),
            Path.Combine(folder, variantName + ".jpeg"),
            Path.Combine(folder, variantName + ".webp")
        ];

        return candidates.FirstOrDefault(File.Exists) ?? "";
    }

    private static bool IsLegacySelectionId(string value)
    {
        var v = value.Trim();
        return v.Equals("custom", StringComparison.OrdinalIgnoreCase)
            || v.Equals("custom1", StringComparison.OrdinalIgnoreCase)
            || v.Equals("Özel Prefab", StringComparison.CurrentCultureIgnoreCase);
    }

    private void MigrateLegacyStorage()
    {
        var legacyRoot = SettingsStore.LegacyCustomPrefabsRoot;
        if (!Directory.Exists(legacyRoot))
            return;

        var newRoot = Root;
        Directory.CreateDirectory(newRoot);

        try
        {
            foreach (var monumentFolder in Directory.EnumerateDirectories(legacyRoot, "*", SearchOption.TopDirectoryOnly))
            {
                var monumentId = Path.GetFileName(monumentFolder);
                if (string.IsNullOrWhiteSpace(monumentId))
                    continue;

                var destinationFolder = Path.Combine(newRoot, SafeSegment(monumentId));
                Directory.CreateDirectory(destinationFolder);

                foreach (var file in Directory.EnumerateFiles(monumentFolder, "*", SearchOption.AllDirectories))
                {
                    if (IsGeneratedLegacyDefault(file))
                        continue;

                    if (!IsPrefabMapFile(file) && !IsPreviewFile(file))
                        continue;

                    var destination = UniqueDestination(
                        destinationFolder,
                        Path.GetFileName(file));

                    File.Copy(file, destination, false);
                }
            }

            // Yeni sistem Config'in içinde CustomPrefabs tutmaz.
            Directory.Delete(legacyRoot, true);
        }
        catch
        {
            // Migration hatası uygulamanın açılmasını engellemesin.
            // Eski klasör olduğu yerde kalır; kullanıcı dosyaları silinmez.
        }
    }

    private static bool IsPreviewFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGeneratedLegacyDefault(string path)
    {
        if (!IsPrefabMapFile(path))
            return false;

        try
        {
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            return LegacyGeneratedPrefabHashes.Contains(hash);
        }
        catch
        {
            return false;
        }
    }

    private static string UniqueDestination(string folder, string fileName)
    {
        var candidate = Path.Combine(folder, fileName);
        if (!File.Exists(candidate))
            return candidate;

        var display = DisplayNameFromPrefabFile(fileName);
        var suffix = fileName.EndsWith(".prefab.map", StringComparison.OrdinalIgnoreCase)
            ? ".prefab.map"
            : Path.GetExtension(fileName);

        for (var i = 1; i < 1000; i++)
        {
            candidate = Path.Combine(folder, $"{display}_legacy{i}{suffix}");
            if (!File.Exists(candidate))
                return candidate;
        }

        return Path.Combine(folder, $"{Guid.NewGuid():N}_{fileName}");
    }

    private static void WriteAudit(string root, AppSettings settings, Session session)
    {
        var reportFolder = Path.Combine(root, "HarmonyConfig", "RavenMapReports");
        Directory.CreateDirectory(reportFolder);

        var audit = new
        {
            format = "raven-custom-prefab-session-v4",
            generatedAtUtc = DateTime.UtcNow,
            settings.WorldSize,
            settings.Seed,
            selectedMonumentCount = session.SelectedMonumentCount,
            applied = session.Applied.Select(x => new
            {
                monumentId = x.MonumentId,
                monumentName = x.MonumentName,
                variantId = x.VariantId,
                variantName = x.VariantName,
                targetCount = x.TargetCount
            }).ToArray(),
            skipped = session.Skipped.Select(x => new
            {
                monumentId = x.MonumentId,
                variantId = x.VariantId,
                reason = x.Reason
            }).ToArray(),
            installedTargets = session.Installed
                .Where(x => x.EndsWith(".map", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileName)
                .ToArray(),
            runtimeValidation = "CustomGenerator validates every prefab id against Rust StringPool before replacing the vanilla monument.",
            swapEnabled = session.Applied.Count > 0
        };

        var json = JsonSerializer.Serialize(audit, JsonOptions);
        File.WriteAllText(
            Path.Combine(reportFolder, $"RavenCustomPrefabSelection_{settings.WorldSize}_{settings.Seed}.json"),
            json);
        File.WriteAllText(
            Path.Combine(reportFolder, "RavenCustomPrefabSelection_latest.json"),
            json);
    }

    private void EnsureReadme()
    {
        var path = Path.Combine(Root, "README.txt");
        var text = """
RAVEN CUSTOM PREFABS — DOSYA ADINA GÖRE SEÇİM

CustomPrefabs artık Config klasörünün DIŞINDADIR.

TEK TARGETLI MONUMENT ÖRNEĞİ — OUTPOST:

  CustomPrefabs\outpost\
      outpost_standar.prefab.map
      outpost_gold.prefab.map
      outpost_premium.prefab.map

Panelde otomatik olarak:
  Vanilla
  outpost_standar
  outpost_gold
  outpost_premium

görünür.

Dosya adı serbesttir. Seçilen dosya üretim sırasında otomatik olarak
Outpost'un gerçek targetı olan compound.prefab.map adına çevrilir.

BANDIT CAMP için de aynı mantık geçerlidir.

BİRDEN FAZLA TARGETLI MONUMENTLER:

Stables gibi iki dosyalı bir monument için birden fazla seçim istiyorsanız
aynı variant adını çift alt çizgi ile target adının önüne yazın:

  CustomPrefabs\stables\
      gold__stables_a.prefab.map
      gold__stables_b.prefab.map
      standard__stables_a.prefab.map
      standard__stables_b.prefab.map

Panelde:
  Vanilla
  gold
  standard

görünür.

Alt klasör / custom1 / custom2 / manifest.json kullanılmaz.
Dosya ekledikten veya sildikten sonra panelde "Prefabları Yenile" düğmesine basın.
""";

        File.WriteAllText(path, text, Encoding.UTF8);
    }

    private static string SafeSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
            sb.Append(invalid.Contains(ch) ? '_' : ch);
        return sb.ToString().Trim();
    }

    public sealed class Session : IDisposable
    {
        private readonly string targetDir;
        private readonly string backupDir;
        private readonly string manifestPath;
        private bool disposed;

        public Session(string targetDir, string backupDir)
        {
            this.targetDir = Path.GetFullPath(targetDir);
            this.backupDir = Path.GetFullPath(backupDir);
            manifestPath = Path.Combine(this.backupDir, "recovery.json");
            PersistManifest();
        }

        public List<(string Original, string Backup)> Backups { get; } = [];
        public List<string> Installed { get; } = [];

        public int InstalledMapCount => Installed.Count(
            x => x.EndsWith(".map", StringComparison.OrdinalIgnoreCase));

        public List<(string MonumentId, string MonumentName, string VariantId, string VariantName, int TargetCount)> Applied { get; } = [];
        public List<(string MonumentId, string VariantId, string Reason)> Skipped { get; } = [];
        public int SelectedMonumentCount { get; set; }

        public void RegisterBackup(string original, string backup)
        {
            Backups.Add((Path.GetFullPath(original), Path.GetFullPath(backup)));
            PersistManifest();
        }

        public void RegisterInstalled(string path)
        {
            var full = Path.GetFullPath(path);
            if (Installed.Contains(full, StringComparer.OrdinalIgnoreCase)) return;
            Installed.Add(full);
            // Dosya yazılmadan önce manifest'e eklenir; hard-crash anında bile temizlenebilir.
            PersistManifest();
        }

        private void PersistManifest()
        {
            Directory.CreateDirectory(backupDir);
            var manifest = new RecoveryManifest
            {
                TargetDirectory = targetDir,
                Backups = Backups.Select(x => new RecoveryBackup { Original = x.Original, Backup = x.Backup }).ToList(),
                Installed = Installed.ToList()
            };
            var temp = manifestPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(manifest, JsonOptions));
            File.Move(temp, manifestPath, true);
        }

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            var restoreFailed = false;

            foreach (var installed in Installed.AsEnumerable().Reverse())
            {
                try
                {
                    if (File.Exists(installed))
                        File.Delete(installed);
                }
                catch { restoreFailed = true; }
            }

            foreach (var (original, backup) in Backups.AsEnumerable().Reverse())
            {
                try
                {
                    if (File.Exists(backup))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                        File.Copy(backup, original, true);
                    }
                }
                catch { restoreFailed = true; }
            }

            // Restore sırasında tek bir dosya bile başarısız olduysa recovery klasörünü
            // SİLME. Sonraki çalıştırmada RecoverInterruptedSessions tekrar deneyecek.
            if (restoreFailed) return;

            try
            {
                if (Directory.Exists(backupDir))
                    Directory.Delete(backupDir, true);
            }
            catch { }
        }
    }
}
