using System.ComponentModel.DataAnnotations;

namespace nexthire_api.DTOs;

public class CreatePublicCandidateLinkRequestDto
{
    [Required]
    [StringLength(160, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;

    [StringLength(400)]
    public string? Search { get; set; }

    [StringLength(160)]
    public string? Source { get; set; }

    [StringLength(40)]
    public string? From { get; set; }

    [StringLength(40)]
    public string? To { get; set; }

    public List<Guid>? TagIds { get; set; }
}

public class PublicCandidateLinkCreatedDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class PublicCandidateLinkAdminDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

public class PublicCandidateLinkRecord
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Search { get; set; }
    public string? Source { get; set; }
    public DateTimeOffset? FromAt { get; set; }
    public DateTimeOffset? ToAt { get; set; }
    public string TagIdsJson { get; set; } = "[]";
}

public class PublicCandidateTagDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class PublicCandidateCardDto
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? Source { get; set; }
    public List<PublicCandidateTagDto> Tags { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; }
}

public class PublicCandidateLinkListDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public IReadOnlyList<PublicCandidateCardDto> Items { get; set; } = Array.Empty<PublicCandidateCardDto>();
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int Total { get; set; }
}

public class PublicCandidateDetailDto
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? Source { get; set; }
    public List<PublicCandidateTagDto> Tags { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; }
    public DocumentAnalysisEnvelopeDto? Analysis { get; set; }
}

public static class PublicCandidateNames
{
    public static string Partial(string? firstName, string? lastName)
    {
        var first = (firstName ?? string.Empty).Trim();
        var last = (lastName ?? string.Empty).Trim();
        if (first.Length == 0 && last.Length == 0)
            return string.Empty;
        if (last.Length == 0)
            return first;
        var initial = char.ToUpperInvariant(last[0]);
        if (first.Length == 0)
            return $"{initial}.";
        return $"{first} {initial}.";
    }

    public static List<PublicCandidateTagDto> Tags(IEnumerable<CandidateTagDto>? tags)
    {
        return (tags ?? Array.Empty<CandidateTagDto>())
            .Where(tag => tag.Id != Guid.Empty && !string.IsNullOrWhiteSpace(tag.Name))
            .Select(tag => new PublicCandidateTagDto { Id = tag.Id, Name = tag.Name.Trim() })
            .ToList();
    }
}
