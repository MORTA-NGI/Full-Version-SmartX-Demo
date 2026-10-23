namespace SmartX.Core.Deployment;

/// <summary>The four tiers of a Smart-X deployment: Facility → Zone → Sub-Zone → Node.</summary>
public enum DeploymentLevel : byte
{
    Facility = 0,
    Zone = 1,
    SubZone = 2,
    Node = 3
}

/// <summary>
/// One vertex in the nested deployment tree. For Facility/Zone/Sub-Zone, <see cref="PowerWatts"/> is the
/// supply <i>capacity</i>; for a Node it is the expected <i>demand</i>. The recursive validator checks that
/// demand never exceeds capacity at any tier.
/// </summary>
public sealed class DeploymentNode
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public required DeploymentLevel Level { get; init; }
    public int PowerWatts { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>Max sensors allowed on a Node (ESP32 GPIO / power budget).</summary>
    public int MaxSensors { get; set; } = 4;

    public List<DeploymentNode> Children { get; init; } = [];

    public DeploymentNode Add(DeploymentNode child)
    {
        Children.Add(child);
        return this;
    }
}

public sealed record ValidationIssue(string NodeId, string Path, string Message, bool IsError);

public sealed record TreeValidationResult(
    bool IsValid,
    int NodesVisited,
    int MaxDepthReached,
    int TotalDemandWatts,
    IReadOnlyList<ValidationIssue> Issues);

public sealed record PathValidationResult(
    bool Found,
    bool IsSafe,
    IReadOnlyList<string> Chain,
    string Display,
    IReadOnlyList<string> Problems);
