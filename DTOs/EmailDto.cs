using System.ComponentModel.DataAnnotations;

namespace nexthire_api.DTOs;

public class SendEmailRequestDto
{
    [Required]
    [EmailAddress]
    public string ToEmail { get; set; } = string.Empty;

    [Required]
    public string Subject { get; set; } = string.Empty;

    [Required]
    public string PlainText { get; set; } = string.Empty;

    [Required]
    public string Html { get; set; } = string.Empty;
}
