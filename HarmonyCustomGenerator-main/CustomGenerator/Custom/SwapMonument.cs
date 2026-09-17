using Newtonsoft.Json;
using ProtoBuf;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

using static CustomGenerator.ExtConfig;

public class SwapMonument
{
    private static WorldSerialization _mainMap = new WorldSerialization();
    private static WorldSerialization _swapMap = new WorldSerialization();
    private static readonly List<Monument> monuments = new List<Monument>();
    private static readonly List<SwapAuditEntry> auditEntries = new List<SwapAuditEntry>();
    private static string mapPath = string.Empty;

    public static void Initiate(string path)
    {
        mapPath = path;
        _mainMap.Load(mapPath);

        Log(_mainMap.world.prefabs.Count);
        LoadMonuments();
        SwapMonuments();
        WriteRuntimeAudit();

        if (!Config.Swap.SaveBothMaps)
            _mainMap.Save(mapPath);
        else
            _mainMap.Save(mapPath.Replace(".map", ".swapped.map"));
    }

    private static void SwapMonuments()
    {
        auditEntries.Clear();
        foreach (Monument monument in monuments)
        {
            var matchPrefabs = _mainMap.world.prefabs.Where(x =>
            {
                string prefabPath = (StringPool.Get(x.id) ?? string.Empty).Replace('\\', '/');
                return prefabPath.EndsWith("/" + monument.prefabShortname, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Path.GetFileName(prefabPath), monument.prefabShortname, StringComparison.OrdinalIgnoreCase);
            }).ToList();

            if (matchPrefabs.Count == 0)
            {
                auditEntries.Add(new SwapAuditEntry(monument.prefabShortname, monument.Metadata.VariantId, 0, 0, "vanilla_not_found", new List<uint>()));
                continue;
            }

            _swapMap.Load(monument.path);
            if (_swapMap.world == null || _swapMap.world.prefabs == null || _swapMap.world.prefabs.Count == 0)
            {
                Log("Skipped empty custom monument map: " + monument.path);
                auditEntries.Add(new SwapAuditEntry(monument.prefabShortname, monument.Metadata.VariantId, matchPrefabs.Count, 0, "empty_custom_map", new List<uint>()));
                continue;
            }

            var invalidIds = FindInvalidPrefabIds(_swapMap.world.prefabs);
            if (invalidIds.Count > 0)
            {
                Log("Skipped incompatible custom monument map: " + monument.path + " | invalid StringPool ids: " + string.Join(", ", invalidIds));
                auditEntries.Add(new SwapAuditEntry(monument.prefabShortname, monument.Metadata.VariantId, matchPrefabs.Count, 0, "invalid_stringpool_ids", invalidIds));
                continue;
            }

            var applied = 0;
            foreach (var firstfab in matchPrefabs)
            {
                var replacement = MapHander.CreatePrefabFromMap(
                    firstfab.position,
                    firstfab.rotation,
                    _swapMap.world.prefabs,
                    monument.Metadata.Anchor);
                if (replacement.Count == 0) continue;

                _mainMap.world.prefabs.Remove(firstfab);
                _mainMap.world.prefabs.AddRange(replacement);
                applied++;
            }
            auditEntries.Add(new SwapAuditEntry(monument.prefabShortname, monument.Metadata.VariantId, matchPrefabs.Count, applied, "applied", new List<uint>()));
        }
    }

    private static List<uint> FindInvalidPrefabIds(List<PrefabData> prefabs)
    {
        var invalid = new HashSet<uint>();
        foreach (var prefab in prefabs)
        {
            // Existing Raven compatibility replacement is validated using the effective id.
            var effectiveId = prefab.id == 2749405185u ? 504351302u : prefab.id;
            var value = StringPool.Get(effectiveId);
            if (string.IsNullOrWhiteSpace(value)) invalid.Add(prefab.id);
        }
        return invalid.OrderBy(x => x).ToList();
    }

    private static void LoadMonuments()
    {
        if (!Directory.Exists("maps/prefabs")) Directory.CreateDirectory("maps/prefabs");

        monuments.Clear();
        string[] files = Directory.GetFiles("maps/prefabs", "*.map", SearchOption.TopDirectoryOnly);
        foreach (string file in files)
        {
            string prefabShortname = Path.GetFileNameWithoutExtension(file);
            monuments.Add(new Monument(prefabShortname, file, LoadMetadata(file + ".raven.json")));
        }
    }

    private static RavenSwapMetadata LoadMetadata(string path)
    {
        try
        {
            if (!File.Exists(path)) return new RavenSwapMetadata();
            return JsonConvert.DeserializeObject<RavenSwapMetadata>(File.ReadAllText(path)) ?? new RavenSwapMetadata();
        }
        catch (Exception ex)
        {
            Log("Swap metadata could not be read: " + path + " | " + ex.Message);
            return new RavenSwapMetadata();
        }
    }

    private static void WriteRuntimeAudit()
    {
        try
        {
            var folder = Path.Combine("HarmonyConfig", "RavenMapReports");
            Directory.CreateDirectory(folder);
            var payload = new
            {
                format = "raven-custom-prefab-runtime-audit-v2",
                generatedAtUtc = DateTime.UtcNow,
                worldSize = tempData.mapsize,
                seed = tempData.mapseed,
                entries = auditEntries
            };
            var json = JsonConvert.SerializeObject(payload, Formatting.Indented);
            var specific = Path.Combine(folder, $"RavenCustomPrefabRuntimeAudit_{tempData.mapsize}_{tempData.mapseed}.json");
            File.WriteAllText(specific, json);
            File.WriteAllText(Path.Combine(folder, "RavenCustomPrefabRuntimeAudit_latest.json"), json);
        }
        catch (Exception ex)
        {
            Log("Runtime custom prefab audit could not be written: " + ex.Message);
        }
    }

    private sealed class Monument
    {
        public string prefabShortname;
        public string path;
        public RavenSwapMetadata Metadata;

        public Monument(string prefabShortname, string path, RavenSwapMetadata metadata)
        {
            this.prefabShortname = prefabShortname;
            this.path = path;
            Metadata = metadata;
        }
    }

    private sealed class SwapAuditEntry
    {
        public string Target { get; }
        public string VariantId { get; }
        public int VanillaMatches { get; }
        public int AppliedInstances { get; }
        public string Status { get; }
        public List<uint> InvalidPrefabIds { get; }

        public SwapAuditEntry(string target, string variantId, int vanillaMatches, int appliedInstances, string status, List<uint> invalidPrefabIds)
        {
            Target = target;
            VariantId = variantId ?? string.Empty;
            VanillaMatches = vanillaMatches;
            AppliedInstances = appliedInstances;
            Status = status;
            InvalidPrefabIds = invalidPrefabIds;
        }
    }

    static void Log(object obj) => Debug.Log("[SWAP MN] " + obj);
}

public sealed class RavenSwapMetadata
{
    [JsonProperty("monumentId")]
    public string MonumentId = string.Empty;
    [JsonProperty("variantId")]
    public string VariantId = string.Empty;
    [JsonProperty("variantName")]
    public string VariantName = string.Empty;
    [JsonProperty("anchor")]
    public RavenSwapAnchor Anchor;
}

public sealed class RavenSwapAnchor
{
    [JsonProperty("x")]
    public float X;
    [JsonProperty("y")]
    public float Y;
    [JsonProperty("z")]
    public float Z;
    [JsonProperty("yaw")]
    public float Yaw;
}

public class MapHander
{
    private static PrefabData CreatePrefab(uint PrefabID, VectorData position, VectorData rotation, VectorData scale, string category = "Monument")
    {
        return new PrefabData
        {
            category = category,
            id = PrefabID,
            position = position,
            rotation = rotation,
            scale = scale
        };
    }

    private static VectorData CalculateLocalPos(VectorData placePos, VectorData globalPos, VectorData rotation) =>
        RotateVector(new VectorData(globalPos.x - placePos.x, globalPos.y - placePos.y, globalPos.z - placePos.z), rotation);

    private static VectorData RotateVector(VectorData vector, VectorData rotation)
    {
        float radX = rotation.x * (float)Math.PI / 180.0f;
        float radY = rotation.y * (float)Math.PI / 180.0f;
        float radZ = rotation.z * (float)Math.PI / 180.0f;

        float cosX = (float)Math.Cos(radX), sinX = (float)Math.Sin(radX);
        float cosY = (float)Math.Cos(radY), sinY = (float)Math.Sin(radY);
        float cosZ = (float)Math.Cos(radZ), sinZ = (float)Math.Sin(radZ);

        float newY = vector.y * cosX - vector.z * sinX;
        float newZ = vector.y * sinX + vector.z * cosX;
        vector.y = newY;
        vector.z = newZ;

        float newX = vector.x * cosY + vector.z * sinY;
        newZ = vector.z * cosY - vector.x * sinY;
        vector.x = newX;
        vector.z = newZ;

        newX = vector.x * cosZ - vector.y * sinZ;
        newY = vector.x * sinZ + vector.y * cosZ;
        vector.x = newX;
        vector.y = newY;

        return vector;
    }

    public static List<PrefabData> CreatePrefabFromMap(VectorData startPos, VectorData rotation, List<PrefabData> prefabs, RavenSwapAnchor anchor = null)
    {
        var createdPrefabs = new List<PrefabData>();
        if (prefabs == null || prefabs.Count == 0) return createdPrefabs;

        if (anchor == null)
        {
            // Backward-compatible mode for existing Raven templates.
            bool first = true;
            foreach (var prefab in prefabs)
            {
                createdPrefabs.Add(CreatePrefab(
                    prefab.id == 2749405185u ? 504351302u : prefab.id,
                    CalculateLegacy(startPos, prefab.position, prefabs, rotation),
                    first ? rotation : CalculateRot(rotation, prefab.rotation),
                    prefab.id == 2749405185u ? new VectorData(0, 0, 0) : prefab.scale,
                    prefab.category));
                first = false;
            }
            return createdPrefabs;
        }

        // Explicit anchor mode: no dependency on prefabs[0]. The anchor's yaw is
        // aligned to the vanilla monument's yaw and all prefab transforms remain local.
        var deltaRotation = new VectorData(rotation.x, rotation.y - anchor.Yaw, rotation.z);
        var anchorPos = new VectorData(anchor.X, anchor.Y, anchor.Z);
        foreach (var prefab in prefabs)
        {
            var local = CalculateLocalPos(anchorPos, prefab.position, deltaRotation);
            var position = new VectorData(startPos.x + local.x, startPos.y + local.y, startPos.z + local.z);
            var localRotation = new VectorData(prefab.rotation.x, prefab.rotation.y - anchor.Yaw, prefab.rotation.z);
            createdPrefabs.Add(CreatePrefab(
                prefab.id == 2749405185u ? 504351302u : prefab.id,
                position,
                CalculateRot(rotation, localRotation),
                prefab.id == 2749405185u ? new VectorData(0, 0, 0) : prefab.scale,
                prefab.category));
        }
        return createdPrefabs;
    }

    private static VectorData CalculateLegacy(VectorData globalPos, VectorData position, List<PrefabData> prefabs, VectorData firstPrefabRotation)
    {
        var localPos = CalculateLocalPos(prefabs[0].position, position, firstPrefabRotation);
        return new VectorData(globalPos.x + localPos.x, globalPos.y + localPos.y, globalPos.z + localPos.z);
    }

    private static VectorData CalculateRot(VectorData globalRot, VectorData localRot) =>
        new VectorData(globalRot.x + localRot.x, globalRot.y + localRot.y, globalRot.z + localRot.z);
}
