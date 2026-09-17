using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace Raven.Server.Controllers.Admin
{
    [ApiController]
    [Route("api/[controller]")]
    public class AdminController : ControllerBase
    {
        private readonly ILogger<AdminController> _logger;
        private readonly string _configPath;

        public AdminController(ILogger<AdminController> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configPath = Path.Combine(configuration.GetValue<string>("DataDirectory") ?? "Data", "raven_server.json");
        }

        [HttpGet("status")]
        public IActionResult GetStatus()
        {
            try
            {
                if (System.IO.File.Exists(_configPath))
                {
                    var json = System.IO.File.ReadAllText(_configPath);
                    var config = JsonSerializer.Deserialize<ServerConfig>(json);
                    
                    return Ok(new
                    {
                        Status = "OK",
                        Version = "1.0.0",
                        Config = config
                    });
                }
                else
                {
                    return NotFound("Configuration file not found");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting server status");
                return StatusCode(500, "Internal server error");
            }
        }

        [HttpGet("config")]
        public IActionResult GetConfig()
        {
            try
            {
                if (System.IO.File.Exists(_configPath))
                {
                    var json = System.IO.File.ReadAllText(_configPath);
                    return Ok(JsonSerializer.Deserialize<object>(json));
                }
                else
                {
                    return NotFound("Configuration file not found");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting configuration");
                return StatusCode(500, "Internal server error");
            }
        }

        [HttpPut("config")]
        public IActionResult UpdateConfig([FromBody] ServerConfig config)
        {
            try
            {
                var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                System.IO.File.WriteAllText(_configPath, json);
                
                return Ok(new { Message = "Configuration updated successfully" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating configuration");
                return StatusCode(500, "Internal server error");
            }
        }

        [HttpGet("updates")]
        public IActionResult GetUpdates()
        {
            try
            {
                var updatesDir = Path.Combine(Path.GetDirectoryName(_configPath) ?? "", "Updates");
                if (!Directory.Exists(updatesDir))
                {
                    return Ok(new { Updates = new object[0] });
                }

                var updateFiles = Directory.GetFiles(updatesDir, "*.zip")
                    .Select(file => new
                    {
                        Name = Path.GetFileName(file),
                        Size = new FileInfo(file).Length,
                        Created = new FileInfo(file).CreationTime
                    })
                    .ToArray();

                return Ok(new { Updates = updateFiles });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting updates");
                return StatusCode(500, "Internal server error");
            }
        }

        [HttpPost("updates")]
        public IActionResult UploadUpdate(IFormFile file)
        {
            try
            {
                if (file == null || file.Length == 0)
                    return BadRequest("No file uploaded");

                var updatesDir = Path.Combine(Path.GetDirectoryName(_configPath) ?? "", "Updates");
                Directory.CreateDirectory(updatesDir);

                var filePath = Path.Combine(updatesDir, file.FileName);
                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    file.CopyTo(stream);
                }

                return Ok(new { Message = "Update uploaded successfully", FileName = file.FileName });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading update");
                return StatusCode(500, "Internal server error");
            }
        }
    }

    public class ServerConfig
    {
        public string? ServerName { get; set; }
        public int Port { get; set; } = 8080;
        public bool EnableHttps { get; set; } = false;
        public string? CertificatePath { get; set; }
        public string? CertificatePassword { get; set; }
        public string? AdminUsername { get; set; }
        public string? AdminPassword { get; set; }
        public string[]? AllowedHosts { get; set; }
        public bool EnableShopierPayments { get; set; } = false;
        public string? ShopierApiKey { get; set; }
        public string? ShopierApiSecret { get; set; }
        public string? SteamOpenIdUrl { get; set; }
        public string[]? AdminUsers { get; set; }
        public int MaxConcurrentGenerations { get; set; } = 2;
        public bool EnableLogging { get; set; } = true;
        public string? LogLevel { get; set; } = "Information";
    }
}