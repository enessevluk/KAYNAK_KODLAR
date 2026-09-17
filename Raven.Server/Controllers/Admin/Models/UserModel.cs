using System.ComponentModel.DataAnnotations;

namespace Raven.Server.Controllers.Admin.Models
{
    public class UserModel
    {
        [Required]
        public string Username { get; set; } = string.Empty;
        
        [Required]
        public string Email { get; set; } = string.Empty;
        
        public string? DisplayName { get; set; }
        
        public bool IsActive { get; set; } = true;
        
        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
        
        public DateTime? LastLogin { get; set; }
        
        public string[] Roles { get; set; } = Array.Empty<string>();
    }
}