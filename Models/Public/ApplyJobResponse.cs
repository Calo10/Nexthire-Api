using nexthire_api.DTOs;

namespace nexthire_api.Models.Public;

public class ApplyJobResponse
{
    public Guid LeadId { get; set; }
    public string Message { get; set; } = "Application received.";
}

