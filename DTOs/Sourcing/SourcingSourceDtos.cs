using System.ComponentModel.DataAnnotations;

namespace nexthire_api.DTOs.Sourcing;

public class SourcingSourceTypeDto
{
    public string Code { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public bool IsActive { get; set; }
    public int? SortOrder { get; set; }
}

public class SourcingSourceConnectionListItemDto
{
    public string SourceTypeCode { get; set; } = string.Empty;
    public string? SourceTypeName { get; set; }
    public string? DisplayName { get; set; }
    public bool IsConnected { get; set; }
    public bool IsActive { get; set; }
    public string? ConfigJson { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

public class UpsertSourcingSourceConnectionRequestDto
{
    [Required]
    [StringLength(64)]
    public string SourceTypeCode { get; set; } = string.Empty;

    public bool IsConnected { get; set; }
    public bool IsActive { get; set; }
    public string? ConfigJson { get; set; }
}
