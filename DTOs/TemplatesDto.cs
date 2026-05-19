using System.ComponentModel.DataAnnotations;

namespace nexthire_api.DTOs;

public class TemplateListItemDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Channel { get; set; } = string.Empty; // email | whatsapp
    public string? Subject { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class TemplateDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Channel { get; set; } = string.Empty; // email | whatsapp
    public string? Subject { get; set; }
    public string Body { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class CreateTemplateRequestDto
{
    [Required]
    [StringLength(160)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [StringLength(40)]
    public string Channel { get; set; } = string.Empty; // email | whatsapp

    [StringLength(400)]
    public string? Subject { get; set; }

    [Required]
    public string Body { get; set; } = string.Empty;

    public bool? IsActive { get; set; }
}

public class UpdateTemplateRequestDto
{
    [Required]
    [StringLength(160)]
    public string Name { get; set; } = string.Empty;

    [StringLength(400)]
    public string? Subject { get; set; }

    [Required]
    public string Body { get; set; } = string.Empty;

    public bool? IsActive { get; set; }
}

