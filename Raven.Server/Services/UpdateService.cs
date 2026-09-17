using System.IO.Compression;
using System.Text.Json;

namespace Raven.Server.Services
{
    public class UpdateService
    {
        private readonly ILogger<UpdateService> _logger;
        private readonly string _updatesDirectory;
        private readonly string _configPath;

        public UpdateService(ILogger<UpdateService> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configPath = Path.Combine(configuration.GetValue<string>("DataDirectory") ?? "Data", "raven_server.json");
            _updatesDirectory = Path.Combine(Path.GetDirectoryName(_configPath) ?? "", "Updates");
        }

        public async Task<UpdateInfo?> GetLatestUpdateAsync()
        {
            try
            {
                var updateFiles = Directory.GetFiles(_updatesDirectory, "*.zip")
                    .Select(file => new FileInfo(file))
                    .OrderByDescending(f => f.CreationTime)
                    .ToArray();

                if (!updateFiles.Any())
                    return null;

                var latestFile = updateFiles.First();
                var version = Path.GetFileNameWithoutExtension(latestFile.Name);

                return new UpdateInfo
                {
                    Version = version,
                    FileName = latestFile.Name,
                    Size = latestFile.Length,
                    ReleaseDate = latestFile.CreationTime,
                    IsAvailable = true
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting latest update");
                return null;
            }
        }

        public async Task<bool> ApplyUpdateAsync(string fileName)
        {
            try
            {
                var filePath = Path.Combine(_updatesDirectory, fileName);
                if (!File.Exists(filePath))
                    return false;

                // Extract update
                var extractPath = Path.Combine(Path.GetDirectoryName(_configPath) ?? "", "temp_update");
                Directory.CreateDirectory(extractPath);

                using (var archive = ZipFile.OpenRead(filePath))
                {
                    foreach (var entry in archive.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.FullName))
                            continue;

                        var destinationPath = Path.Combine(extractPath, entry.FullName);
                        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                        entry.ExtractToFile(destinationPath, true);
                    }
                }

                // Apply update logic here
                _logger.LogInformation("Update applied successfully: {FileName}", fileName);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error applying update: {FileName}", fileName);
                return false;
            }
        }

        public async Task<UpdateInfo> CreateUpdateAsync(string name, string version, string description)
        {
            try
            {
                var fileName = $"{name}_{version}.zip";
                var filePath = Path.Combine(_updatesDirectory, fileName);
                
                // In a real implementation, this would create an actual update package
                // For now, we'll just create a placeholder file
                File.WriteAllText(filePath, $"Update {name} version {version}");
                
                return new UpdateInfo
                {
                    Version = version,
                    FileName = fileName,
                    Size = new FileInfo(filePath).Length,
                    ReleaseDate = DateTime.UtcNow,
                    IsAvailable = true
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating update: {Name} {Version}", name, version);
                throw;
            }
        }

        public async Task<List<UpdateInfo>> GetAvailableUpdatesAsync()
        {
            try
            {
                var updates = new List<UpdateInfo>();
                
                var updateFiles = Directory.GetFiles(_updatesDirectory, "*.zip")
                    .Select(file => new FileInfo(file))
                    .OrderByDescending(f => f.CreationTime)
                    .ToArray();

                foreach (var file in updateFiles)
                {
                    updates.Add(new UpdateInfo
                    {
                        Version = Path.GetFileNameWithoutExtension(file.Name),
                        FileName = file.Name,
                        Size = file.Length,
                        ReleaseDate = file.CreationTime,
                        IsAvailable = true
                    });
                }

                return updates;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting available updates");
                return new List<UpdateInfo>();
            }
        }
    }

    public class UpdateInfo
    {
        public string Version { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public long Size { get; set; }
        public DateTime ReleaseDate { get; set; }
        public bool IsAvailable { get; set; }
        public string? Description { get; set; }
    }
}