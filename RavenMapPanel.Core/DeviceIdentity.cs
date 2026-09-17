using Microsoft.Win32;
using System.Security.Cryptography;
using System.Text;

namespace RavenMapPanel;

internal static class DeviceIdentity
{
    private static readonly object Gate = new();
    private static string? cachedStableId;

    public static string GetDeviceHash()
    {
        var source = $"RAVEN|DEVICE-V2|{GetStableMachineId()}|{Environment.Is64BitOperatingSystem}";
        return Hash(source);
    }

    public static string GetLegacyDeviceHash()
    {
        // Eski aktivasyonların migration'ını korur. MachineGuid okunamıyorsa artık
        // ortak "unknown" değeri yerine kalıcı install id kullanılır.
        var source = $"RAVEN|{GetStableMachineId()}|{Environment.MachineName}|{Environment.Is64BitOperatingSystem}";
        return Hash(source);
    }

    private static string GetStableMachineId()
    {
        lock (Gate)
        {
            if (!string.IsNullOrWhiteSpace(cachedStableId))
                return cachedStableId;

            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                    var machineGuid = Convert.ToString(key?.GetValue("MachineGuid"))?.Trim();
                    if (!string.IsNullOrWhiteSpace(machineGuid))
                        return cachedStableId = machineGuid;
                }
                catch { }
            }

            return cachedStableId = "install-" + GetOrCreateInstallId();
        }
    }

    private static string GetOrCreateInstallId()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RavenMapPanel");
        var path = Path.Combine(root, "device.id");
        try
        {
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (Guid.TryParse(existing, out var parsed))
                    return parsed.ToString("N");
            }

            Directory.CreateDirectory(root);
            var created = Guid.NewGuid().ToString("N");
            File.WriteAllText(path, created, new UTF8Encoding(false));
            return created;
        }
        catch
        {
            // Çok istisnai bir durumda dahi tüm makineleri aynı kimliğe düşürme.
            // Bu değer süreç boyunca stabildir; sonraki açılışta erişim düzeldiğinde
            // kalıcı install id üretilecektir.
            return "ephemeral-" + Guid.NewGuid().ToString("N");
        }
    }

    private static string Hash(string source) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
}
