using System.ComponentModel.DataAnnotations;

namespace nexthire_api.Models.Tasks;

public class UpdateTaskStatusRequest
{
    [Required]
    [StringLength(40)]
    public string Status { get; set; } = string.Empty;
}

