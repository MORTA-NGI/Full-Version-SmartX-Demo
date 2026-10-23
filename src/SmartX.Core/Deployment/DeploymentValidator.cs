namespace SmartX.Core.Deployment;

/// <summary>
/// Recursive validation of the nested deployment tree (Facility → Zone → Sub-Zone → Node).
/// <para>
/// <b>Recursive case:</b> validate this node, then recurse into each child with <c>expectedLevel + 1</c> and
/// sum the power demand the children report back up the call stack.<br/>
/// <b>Base cases:</b> (1) a <see cref="DeploymentLevel.Node"/> leaf returns its own demand;
/// (2) depth exceeds <see cref="MaxDepth"/> → stop (guards against stack overflow from a malformed import);
/// (3) an Id that was already visited → stop (guards against cycles/duplicate references).
/// </para>
/// </summary>
public static class DeploymentValidator
{
    public const int MaxDepth = 16;

    public static TreeValidationResult ValidateTree(DeploymentNode root) => ValidateSubtree(root, DeploymentLevel.Facility);

    /// <summary>Validates any subtree, treating <paramref name="expectedRootLevel"/> as the tier of its root.</summary>
    public static TreeValidationResult ValidateSubtree(DeploymentNode root, DeploymentLevel expectedRootLevel)
    {
        var issues = new List<ValidationIssue>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stats = new WalkStats();

        int demand = Validate(root, expectedRootLevel, root.Name, depth: 0, seen, issues, stats);

        return new TreeValidationResult(
            IsValid: issues.All(i => !i.IsError),
            NodesVisited: stats.Visited,
            MaxDepthReached: stats.MaxDepth,
            TotalDemandWatts: demand,
            Issues: issues);
    }

    /// <summary>Validates every facility in a forest.</summary>
    public static TreeValidationResult ValidateForest(IEnumerable<DeploymentNode> facilities)
    {
        var issues = new List<ValidationIssue>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stats = new WalkStats();
        int demand = 0;
        foreach (var f in facilities)
        {
            demand += Validate(f, DeploymentLevel.Facility, f.Name, 0, seen, issues, stats);
        }
        return new TreeValidationResult(issues.All(i => !i.IsError), stats.Visited, stats.MaxDepth, demand, issues);
    }

    private sealed class WalkStats
    {
        public int Visited;
        public int MaxDepth;
    }

    /// <returns>The total power demand (W) of the subtree rooted at <paramref name="node"/>.</returns>
    private static int Validate(DeploymentNode node, DeploymentLevel expected, string path, int depth,
        HashSet<string> seen, List<ValidationIssue> issues, WalkStats stats)
    {
        // ---- Base case 2: depth guard ----
        if (depth > MaxDepth)
        {
            issues.Add(new(node.Id, path, $"Hierarchy deeper than {MaxDepth} levels - aborted this branch.", true));
            return 0;
        }

        // ---- Base case 3: cycle / duplicate guard ----
        if (!seen.Add(node.Id))
        {
            issues.Add(new(node.Id, path, $"Duplicate or cyclic node id '{node.Id}'.", true));
            return 0;
        }

        stats.Visited++;
        stats.MaxDepth = Math.Max(stats.MaxDepth, depth);

        if (string.IsNullOrWhiteSpace(node.Name))
            issues.Add(new(node.Id, path, "Node has no name.", true));

        if (node.Level != expected)
            issues.Add(new(node.Id, path, $"Expected a {expected} at this tier but found a {node.Level}.", true));

        if (!node.Enabled)
            issues.Add(new(node.Id, path, "Branch is disabled - devices below it will not be routed.", false));

        // ---- Base case 1: leaf node ----
        if (node.Level == DeploymentLevel.Node)
        {
            if (node.Children.Count > 0)
                issues.Add(new(node.Id, path, "A Node is a leaf and cannot contain children.", true));
            if (node.PowerWatts <= 0)
                issues.Add(new(node.Id, path, "Node power demand must be greater than 0 W.", true));
            return Math.Max(node.PowerWatts, 0);
        }

        if (node.Children.Count == 0)
            issues.Add(new(node.Id, path, $"{node.Level} '{node.Name}' has no children yet.", false));

        // ---- Recursive case ----
        int childDemand = 0;
        var nextLevel = (DeploymentLevel)Math.Min((int)node.Level + 1, (int)DeploymentLevel.Node);
        foreach (var child in node.Children)
        {
            childDemand += Validate(child, nextLevel, $"{path} > {child.Name}", depth + 1, seen, issues, stats);
        }

        if (node.PowerWatts > 0 && childDemand > node.PowerWatts)
        {
            issues.Add(new(node.Id, path,
                $"Power overload: children demand {childDemand} W but {node.Level} capacity is {node.PowerWatts} W.", true));
        }

        return childDemand;
    }

    /// <summary>
    /// Recursively locates <paramref name="targetId"/> and validates that every tier on the way down is safely
    /// configured, e.g. "Node 07 → Sub-Zone B → Zone 1 → Facility A".
    /// </summary>
    public static PathValidationResult ValidatePath(IEnumerable<DeploymentNode> facilities, string targetId)
    {
        foreach (var facility in facilities)
        {
            var chain = new Stack<DeploymentNode>();
            if (!FindChain(facility, targetId, chain, depth: 0)) continue;

            // Stack enumerates leaf → root, which is exactly the order we want to display.
            var ordered = chain.ToList();
            var problems = new List<string>();

            if (ordered[0].Level != DeploymentLevel.Node)
                problems.Add($"'{ordered[0].Name}' is a {ordered[0].Level}; sensors can only be attached to a Node.");

            for (int i = ordered.Count - 1; i >= 0; i--)
            {
                var n = ordered[i];
                int expectedLevel = ordered.Count - 1 - i;
                if ((int)n.Level != expectedLevel)
                    problems.Add($"Tier mismatch at '{n.Name}' (expected {(DeploymentLevel)expectedLevel}).");
                if (!n.Enabled)
                    problems.Add($"'{n.Name}' is disabled.");
            }

            // Re-use the recursive subtree check on each ancestor to catch power overloads along the chain.
            foreach (var ancestor in ordered.Skip(1))
            {
                var sub = ValidateSubtree(ancestor, ancestor.Level);
                foreach (var issue in sub.Issues.Where(x => x.IsError && x.NodeId == ancestor.Id))
                    problems.Add($"{ancestor.Name}: {issue.Message}");
            }

            return new PathValidationResult(
                Found: true,
                IsSafe: problems.Count == 0,
                Chain: ordered.Select(n => n.Name).ToList(),
                Display: string.Join(" → ", ordered.Select(n => n.Name)),
                Problems: problems);
        }

        return new PathValidationResult(false, false, [], "", [$"No deployment node with id '{targetId}'."]);
    }

    /// <summary>Depth-first recursive search that leaves the root→target chain on the stack.</summary>
    private static bool FindChain(DeploymentNode current, string targetId, Stack<DeploymentNode> chain, int depth)
    {
        if (depth > MaxDepth) return false;                // base case: too deep
        chain.Push(current);
        if (string.Equals(current.Id, targetId, StringComparison.OrdinalIgnoreCase))
            return true;                                   // base case: found

        foreach (var child in current.Children)
        {
            if (FindChain(child, targetId, chain, depth + 1)) return true; // recursive case
        }

        chain.Pop();                                       // backtrack
        return false;
    }

    /// <summary>Recursively finds a node by id (null when not found).</summary>
    public static DeploymentNode? Find(DeploymentNode node, string id, int depth = 0)
    {
        if (depth > MaxDepth) return null;
        if (string.Equals(node.Id, id, StringComparison.OrdinalIgnoreCase)) return node;
        foreach (var c in node.Children)
        {
            var hit = Find(c, id, depth + 1);
            if (hit is not null) return hit;
        }
        return null;
    }

    /// <summary>Recursively counts nodes at a given level (used for dashboard stats).</summary>
    public static int CountLevel(DeploymentNode node, DeploymentLevel level) =>
        (node.Level == level ? 1 : 0) + node.Children.Sum(c => CountLevel(c, level));
}
