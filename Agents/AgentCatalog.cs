namespace nexthire_api.Agents;

public sealed record AgentDefinition(string AgentId, string Availability)
{
    public bool Hireable => !string.Equals(Availability, "future", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Product catalog of NextHire AI agent types. Names, avatars, and copy live in the frontend.
/// </summary>
public static class AgentCatalog
{
    public static readonly IReadOnlyList<AgentDefinition> All =
    [
        new("sourcing", "v1"),
        new("campaign", "v1"),
        new("screening", "v1"),
        new("interview", "v1"),
        new("offer", "v1"),
    ];

    private static readonly Dictionary<string, AgentDefinition> ById =
        All.ToDictionary(item => item.AgentId, item => item, StringComparer.OrdinalIgnoreCase);

    public static bool TryGet(string? agentId, out AgentDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            definition = null!;
            return false;
        }

        return ById.TryGetValue(agentId.Trim(), out definition!);
    }

    public static AgentDefinition Require(string? agentId)
    {
        if (!TryGet(agentId, out var definition))
            throw new ArgumentException("Unknown agent.");

        return definition;
    }

    public static string NormalizeId(string agentId) => Require(agentId).AgentId;
}
