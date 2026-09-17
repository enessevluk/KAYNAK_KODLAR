using System.ComponentModel.DataAnnotations;

namespace Raven.Server.Controllers.Admin.Models
{
    public class UpdateModel
    {
        [Required]
        public string Name { get; set; } = string.Empty;
        
        [Required]
        public string Version { get; set; } = string.Empty;
        
        public string Description { get; set; } = string.Empty;
        
        [Required]
        public DateTime ReleaseDate { get; set; }
        
        public string? DownloadUrl { get; set; }
        
        public long FileSize { get; set; }
        
        public bool IsCritical { get; set; } = false;
    }
}