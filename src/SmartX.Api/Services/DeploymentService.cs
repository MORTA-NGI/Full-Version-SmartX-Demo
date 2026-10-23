using SmartX.Core.Contracts;
using SmartX.Core.Deployment;

namespace SmartX.Api.Services;

/// <summary>Owns the nested Facility → Zone → Sub-Zone → Node tree and runs the recursive validators.</summary>
public sealed class DeploymentService
{
    private readonly List<DeploymentNode> _facilities = [];
    private readonly Lock _gate = new();
    private int _nextId = 100;

    public void AddFacility(DeploymentNode facility)
    {
        lock (_gate) _facilities.Add(facility);
    }

    public PathValidationResult ValidatePath(string nodeId)
    {
        lock (_gate) return DeploymentValidator.ValidatePath(_facilities, nodeId);
    }

    public TreeValidationResult ValidateAll()
    {
        lock (_gate) return DeploymentValidator.ValidateForest(_facilities);
    }

    public DeploymentNode? Find(string id)
    {
        lock (_gate)
        {
            foreach (var f in _facilities)
            {
                var hit = DeploymentValidator.Find(f, id);
                if (hit is not null) return hit;
            }
            return null;
        }
    }

    /// <summary>Returns (facilityId, zoneLabel) for a node by walking the validated chain.</summary>
    public (string FacilityId, string Zone, string Path) Describe(string nodeId)
    {
        lock (_gate)
        {
            foreach (var f in _facilities)
            {
                var chain = new List<DeploymentNode>();
                if (Collect(f, nodeId, chain))
                {
                    var zone = chain.Count >= 3 ? $"{chain[1].Name} / {chain[2].Name}" : chain[^1].Name;
                    var path = string.Join(" > ", chain.Select(c => c.Name));
                    return (f.Id, zone, path);
                }
            }
            return ("", "", "");
        }
    }

    // Recursive root→target chain collector.
    private static bool Collect(DeploymentNode node, string id, List<DeploymentNode> chain)
    {
        chain.Add(node);
        if (string.Equals(node.Id, id, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var c in node.Children)
            if (Collect(c, id, chain)) return true;
        chain.RemoveAt(chain.Count - 1);
        return false;
    }

    public IReadOnlyList<(string Id, string Name)> Facilities()
    {
        lock (_gate) return _facilities.Select(f => (f.Id, f.Name)).ToList();
    }

    public (DeploymentNode? Node, string? Error) AddChild(AddDeploymentNodeRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) return (null, "Name is required.");
        lock (_gate)
        {
            DeploymentNode? parent = null;
            foreach (var f in _facilities)
            {
                parent = DeploymentValidator.Find(f, req.ParentId);
                if (parent is not null) break;
            }
            if (parent is null) return (null, $"Parent '{req.ParentId}' not found.");
            if (parent.Level == DeploymentLevel.Node) return (null, "A Node is a leaf - choose a Facility, Zone or Sub-Zone as parent.");

            var level = (DeploymentLevel)((int)parent.Level + 1);
            var prefix = level switch { DeploymentLevel.Zone => "Z", DeploymentLevel.SubZone => "SZ", _ => "N" };
            var node = new DeploymentNode
            {
                Id = $"{prefix}-{Interlocked.Increment(ref _nextId)}",
                Name = req.Name.Trim(),
                Level = level,
                PowerWatts = req.PowerWatts
            };
            parent.Children.Add(node);
            return (node, null);
        }
    }

    public DeploymentOverviewDto Overview(IReadOnlyDictionary<string, int> deviceCounts)
    {
        lock (_gate)
        {
            var dtos = _facilities.Select(f => ToDto(f, deviceCounts)).ToList();
            return new DeploymentOverviewDto(dtos, DeploymentValidator.ValidateForest(_facilities));
        }
    }

    private static DeploymentNodeDto ToDto(DeploymentNode n, IReadOnlyDictionary<string, int> counts)
    {
        var children = n.Children.Select(c => ToDto(c, counts)).ToList();
        int own = counts.TryGetValue(n.Id, out var c) ? c : 0;
        return new DeploymentNodeDto(n.Id, n.Name, n.Level, n.PowerWatts, n.Enabled, own + children.Sum(x => x.DeviceCount), children);
    }
}
