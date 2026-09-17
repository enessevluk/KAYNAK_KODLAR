using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

sealed class PrefabContentService
{
    private sealed record ContentFile(string Plan,string MonumentId,string Id,string Name,string ContentId,string Path,string TargetFileName,string Sha256,long Size);
    private sealed record HashCacheEntry(long Length, DateTime LastWriteUtc, string Sha256);

    private static readonly IReadOnlyDictionary<string,string> Targets = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
    {
        ["outpost"] = "compound.prefab.map",
        ["bandit_camp"] = "bandit_town.prefab.map",
        ["stables_a"] = "stables_a.prefab.map",
        ["stables_b"] = "stables_b.prefab.map",
        ["fishing_village_a"] = "fishing_village_a.prefab.map",
        ["fishing_village_b"] = "fishing_village_b.prefab.map",
        ["fishing_village_c"] = "fishing_village_c.prefab.map"
    };

    private readonly string root;
    private readonly OfflineTokenSigner signer;
    private readonly object cacheGate = new();
    private readonly Dictionary<string, HashCacheEntry> hashCache = new(StringComparer.OrdinalIgnoreCase);

    public PrefabContentService(string root, OfflineTokenSigner signer)
    {
        this.root = Path.GetFullPath(root);
        this.signer = signer;
        Directory.CreateDirectory(Path.Combine(this.root, EntitlementPlans.Standard));
        Directory.CreateDirectory(Path.Combine(this.root, EntitlementPlans.Premium));
    }

    public PrefabCatalogEnvelope CreateSignedCatalog(string plan)
    {
        plan = EntitlementPlans.Normalize(plan);
        var variants = Scan(plan)
            .GroupBy(x => new { x.Plan, x.MonumentId, x.Id })
            .Select(g => new PrefabCatalogVariant(
                g.Key.MonumentId,
                $"{g.Key.Plan.ToLowerInvariant()}:{g.Key.MonumentId}:{g.Key.Id}",
                CatalogDisplayName(g.Key.Plan, g.Key.MonumentId, g.First().Name),
                g.Key.Plan,
                g.Select(x => new PrefabCatalogTarget(x.ContentId,x.TargetFileName,x.Sha256,x.Size)).ToList()))
            .OrderBy(x => x.MonumentId,StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.MinimumPlan == EntitlementPlans.Premium ? 1 : 0)
            .ThenBy(x => x.Name,StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var payload = JsonSerializer.SerializeToUtf8Bytes(new PrefabCatalogDocument(1,plan,DateTimeOffset.UtcNow,variants));
        return new PrefabCatalogEnvelope(Convert.ToBase64String(payload),signer.Sign(payload));
    }

    public bool TryResolve(string contentId, string plan, out string path, out string fileName)
    {
        var item = Scan(EntitlementPlans.Normalize(plan)).FirstOrDefault(x =>
            x.ContentId.Equals(contentId,StringComparison.OrdinalIgnoreCase));
        if (item is null) { path=""; fileName=""; return false; }
        path=item.Path; fileName=Path.GetFileName(item.Path); return true;
    }

    private List<ContentFile> Scan(string requestedPlan)
    {
        var allowed = requestedPlan == EntitlementPlans.Premium
            ? new[] { EntitlementPlans.Standard, EntitlementPlans.Premium }
            : new[] { EntitlementPlans.Standard };
        var result = new List<ContentFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var plan in allowed)
        {
            var planRoot = Path.Combine(root,plan);
            if (!Directory.Exists(planRoot)) continue;
            var planRootFull = Path.GetFullPath(planRoot) + Path.DirectorySeparatorChar;
            foreach (var monumentDir in Directory.EnumerateDirectories(planRoot))
            {
                var monumentId = Path.GetFileName(monumentDir);
                if (!Targets.TryGetValue(monumentId,out var target)) continue;
                foreach (var file in Directory.EnumerateFiles(monumentDir,"*",SearchOption.TopDirectoryOnly).Where(IsMap))
                {
                    var full = Path.GetFullPath(file);
                    if (!full.StartsWith(planRootFull,StringComparison.OrdinalIgnoreCase)) continue;
                    seen.Add(full);
                    var stem = Stem(Path.GetFileName(file));
                    var info = new FileInfo(full);
                    var sha = GetCachedSha256(full, info);
                    var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plan+"|"+monumentId+"|"+Path.GetFileName(file))))[..32];
                    result.Add(new ContentFile(plan,monumentId,SafeId(stem),Display(stem),id,full,target,sha,info.Length));
                }
            }
        }

        lock (cacheGate)
        {
            foreach (var stale in hashCache.Keys.Where(x => !seen.Contains(x)).ToArray())
                hashCache.Remove(stale);
        }
        return result;
    }

    private string GetCachedSha256(string fullPath, FileInfo info)
    {
        lock (cacheGate)
        {
            if (hashCache.TryGetValue(fullPath, out var cached) &&
                cached.Length == info.Length && cached.LastWriteUtc == info.LastWriteTimeUtc)
                return cached.Sha256;
        }

        string sha;
        using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan))
            sha = Convert.ToHexString(SHA256.HashData(stream));

        lock (cacheGate)
            hashCache[fullPath] = new HashCacheEntry(info.Length, info.LastWriteTimeUtc, sha);
        return sha;
    }

    private static bool IsMap(string path) => path.EndsWith(".map",StringComparison.OrdinalIgnoreCase);
    private static string Stem(string file) => file.EndsWith(".prefab.map",StringComparison.OrdinalIgnoreCase)
        ? file[..^".prefab.map".Length] : Path.GetFileNameWithoutExtension(file);
    private static string SafeId(string value) => new(value.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());
    private static string Display(string value)
    {
        var words=value.Replace('-','_').Split('_',StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ',words.Select(x => x.Length==0?x:char.ToUpperInvariant(x[0])+x[1..]));
    }

    private static string CatalogDisplayName(string plan, string monumentId, string fallback)
    {
        if (!monumentId.Equals("outpost", StringComparison.OrdinalIgnoreCase)) return fallback;
        return plan.Equals(EntitlementPlans.Premium, StringComparison.OrdinalIgnoreCase)
            ? "Premium Outpost"
            : "Standard Outpost";
    }
}
