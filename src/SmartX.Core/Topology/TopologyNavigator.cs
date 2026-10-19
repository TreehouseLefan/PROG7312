namespace SmartX.Core.Topology;

/// <summary>Recursive helpers for walking the deployment tree.</summary>
public static class TopologyNavigator
{
    /// <summary>Stops runaway recursion if a malformed tree ever contains a cycle.</summary>
    public const int MaxDepth = 32;

    private static readonly List<DeploymentNode> NoChildren = new();

    /// <summary>Finds the chain root → ... → target. Returns false if the id does not exist.</summary>
    public static bool TryFindPath(IEnumerable<DeploymentNode> roots, string id, out List<DeploymentNode> path)
    {
        ArgumentNullException.ThrowIfNull(roots);
        path = new List<DeploymentNode>();

        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        foreach (DeploymentNode root in roots)
        {
            if (root is not null && FindPath(root, id, path, 0))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Recursive depth-first search. The path list is used as the call stack record.</summary>
    private static bool FindPath(DeploymentNode node, string id, List<DeploymentNode> path, int depth)
    {
        if (depth > MaxDepth)
        {
            return false; // base case: malformed / cyclic tree
        }

        path.Add(node);

        if (string.Equals(node.Id, id, StringComparison.OrdinalIgnoreCase))
        {
            return true; // base case: found
        }

        foreach (DeploymentNode child in node.Children ?? NoChildren)
        {
            if (child is not null && FindPath(child, id, path, depth + 1))
            {
                return true; // recursive case
            }
        }

        path.RemoveAt(path.Count - 1); // backtrack
        return false;
    }

    public static DeploymentNode? FindById(IEnumerable<DeploymentNode> roots, string id) =>
        TryFindPath(roots, id, out List<DeploymentNode> path) ? path[^1] : null;

    /// <summary>Removes a node (and its subtree) anywhere in the tree.</summary>
    public static bool Remove(List<DeploymentNode> nodes, string id)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        for (int i = 0; i < nodes.Count; i++)
        {
            if (string.Equals(nodes[i].Id, id, StringComparison.OrdinalIgnoreCase))
            {
                nodes.RemoveAt(i);
                return true;
            }

            List<DeploymentNode>? children = nodes[i].Children;
            if (children is not null && children.Count > 0 && Remove(children, id))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Deep copy, so callers can serialise the tree without holding a lock.</summary>
    public static DeploymentNode Clone(DeploymentNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        var copy = new DeploymentNode
        {
            Id = node.Id,
            Name = node.Name,
            Description = node.Description,
            Level = node.Level,
            DeviceMac = node.DeviceMac,
            Profile = node.Profile is null
                ? null
                : new ConfigProfile
                {
                    MaxSampleIntervalSeconds = node.Profile.MaxSampleIntervalSeconds,
                    EncryptionRequired = node.Profile.EncryptionRequired,
                    MinValue = node.Profile.MinValue,
                    MaxValue = node.Profile.MaxValue
                }
        };

        foreach (DeploymentNode child in node.Children ?? NoChildren)
        {
            if (child is not null)
            {
                copy.Children.Add(Clone(child));
            }
        }

        return copy;
    }

    public static int CountDevices(DeploymentNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        int count = node.Level == DeploymentLevel.Node ? 1 : 0;

        foreach (DeploymentNode child in node.Children ?? NoChildren)
        {
            if (child is not null)
            {
                count += CountDevices(child);
            }
        }

        return count;
    }

    public static void Flatten(DeploymentNode node, List<DeploymentNode> into)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(into);
        into.Add(node);

        foreach (DeploymentNode child in node.Children ?? NoChildren)
        {
            if (child is not null)
            {
                Flatten(child, into);
            }
        }
    }

    /// <summary>Formats a root-first path as "Node -> Sub-Zone B -> Zone 1 -> Facility A".</summary>
    public static string FormatChain(IReadOnlyList<DeploymentNode> path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var names = new string[path.Count];

        for (int i = 0; i < path.Count; i++)
        {
            DeploymentNode node = path[path.Count - 1 - i];
            names[i] = string.IsNullOrWhiteSpace(node.Name) ? node.Id : node.Name;
        }

        return string.Join(" -> ", names);
    }
}
