using nexthire_api.DTOs;

namespace nexthire_api.Models.Public;

public class ApplyJobResponse
{
    public CandidateDto Candidate { get; set; } = new();
    public ApplicationCardDto Application { get; set; } = new();
}

