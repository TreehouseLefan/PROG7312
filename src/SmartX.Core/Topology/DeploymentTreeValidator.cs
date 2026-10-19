using SmartX.Core.Contracts;
using SmartX.Core.Devices;

namespace SmartX.Core.Topology;

/// <summary>
/// Recursive validation of nested device deployment trees and multi-tier configuration profiles.
/// Every node is checked against its parent, and each branch's <see cref="ConfigProfile"/> is merged
/// with the profile inherited from its ancestors before the children are visited.
/// </summary>
public sealed class DeploymentTreeValidator
{
    public const int MaxDepth = 12;
    public const string Error = "Error";
    public const string Warning = "Warning";

    private readonly Func<string, int?>? _expectedIntervalLookup;

    /// <param name="expectedIntervalLookup">
    /// Optional: returns a device's reporting interval (seconds) from its MAC so the validator can
    /// check that the device is fast enough for the branch it is deployed in.
    /// </param>
    public DeploymentTreeValidator(Func<string, int?>? expectedIntervalLookup = null)
    {
        _expectedIntervalLookup = expectedIntervalLookup;
    }

    public static bool IsAllowedChild(DeploymentLevel parent, DeploymentLevel child) => (parent, child) switch
    {
        (DeploymentLevel.Facility, DeploymentLevel.Zone) => true,
        (DeploymentLevel.Zone, DeploymentLevel.SubZone) => true,
        (DeploymentLevel.SubZone, DeploymentLevel.Node) => true,
        _ => false
    };

    public static string ExpectedChild(DeploymentLevel parent) => parent switch
    {
        DeploymentLevel.Facility => "Zone",
        DeploymentLevel.Zone => "SubZone",
        DeploymentLevel.SubZone => "Node",
        _ => "no children"
    };

    // ------------------------------------------------------------------
    // Whole-tree validation
    // ------------------------------------------------------------------

    public TopologyValidationResultDto Validate(IReadOnlyList<DeploymentNode> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var context = new WalkContext();

        if (roots.Count == 0)
        {
            context.AddWarning("(none)", "(root)", "The topology is empty.");
        }

        foreach (DeploymentNode root in roots)
        {
            if (root is null)
            {
                context.AddError("(null)", "(root)", "A root entry is null.");
                continue;
            }

            if (root.Level != DeploymentLevel.Facility)
            {
                context.AddError(root.Id, Label(root), $"Top-level nodes must be Facilities, but '{Label(root)}' is a {root.Level}.");
            }

            ValidateNode(root, null, EffectiveProfile.Default, 0, context);
        }

        return context.ToResult();
    }

    private void ValidateNode(
        DeploymentNode node,
        DeploymentNode? parent,
        EffectiveProfile inherited,
        int depth,
        WalkContext context)
    {
        // Base case 1: stop descending into absurdly deep trees.
        if (depth > MaxDepth)
        {
            context.AddError(node.Id, context.PathWith(Label(node)), $"Maximum depth of {MaxDepth} exceeded; descent stopped.");
            return;
        }

        // Base case 2: a node that is already on the current path means the tree loops back on itself.
        if (!context.Visiting.Add(node))
        {
            context.AddError(node.Id, context.PathWith(Label(node)), "Cycle detected: this node is its own ancestor.");
            return;
        }

        context.Path.Add(Label(node));
        context.NodesChecked++;
        string path = context.CurrentPath;

        if (string.IsNullOrWhiteSpace(node.Id))
        {
            context.AddError("(blank)", path, "Every node needs an id.");
        }
        else if (!context.SeenIds.Add(node.Id))
        {
            context.AddError(node.Id, path, $"Duplicate node id '{node.Id}'.");
        }

        if (string.IsNullOrWhiteSpace(node.Name))
        {
            context.AddWarning(node.Id, path, "Node has no display name.");
        }

        if (parent is not null && !IsAllowedChild(parent.Level, node.Level))
        {
            context.AddError(
                node.Id,
                path,
                $"A {node.Level} cannot sit under a {parent.Level}; expected {ExpectedChild(parent.Level)}.");
        }

        EffectiveProfile effective = MergeProfile(node, inherited, path, context);
        List<DeploymentNode> children = node.Children ?? new List<DeploymentNode>();

        if (node.Level == DeploymentLevel.Node)
        {
            ValidateDeviceNode(node, effective, path, context);
            if (children.Count > 0)
            {
                context.AddError(node.Id, path, "Device nodes are leaves and cannot have children.");
            }
        }
        else if (children.Count == 0)
        {
            context.AddWarning(node.Id, path, $"This {node.Level} has no children yet.");
        }

        // Recursive case: validate each child with the profile this node passes down.
        foreach (DeploymentNode child in children)
        {
            if (child is null)
            {
                context.AddError(node.Id, path, "A child entry is null.");
                continue;
            }

            ValidateNode(child, node, effective, depth + 1, context);
        }

        context.Path.RemoveAt(context.Path.Count - 1);
        context.Visiting.Remove(node);
    }

    private void ValidateDeviceNode(DeploymentNode node, EffectiveProfile effective, string path, WalkContext context)
    {
        if (string.IsNullOrWhiteSpace(node.DeviceMac))
        {
            context.AddError(node.Id, path, "Device node has no MAC address.");
            return;
        }

        if (!MacAddress.TryNormalize(node.DeviceMac, out string mac))
        {
            context.AddError(node.Id, path, $"'{node.DeviceMac}' is not a valid MAC address.");
            return;
        }

        context.DevicesChecked++;

        if (!context.SeenMacs.Add(mac))
        {
            context.AddError(node.Id, path, $"MAC {mac} is deployed more than once.");
        }

        int? expected = _expectedIntervalLookup?.Invoke(mac);
        if (expected is int seconds && seconds > effective.MaxSampleIntervalSeconds)
        {
            context.AddError(
                node.Id,
                path,
                $"Device reports every {seconds}s but this branch requires a reading at least every {effective.MaxSampleIntervalSeconds}s.");
        }
    }

    private static EffectiveProfile MergeProfile(DeploymentNode node, EffectiveProfile inherited, string path, WalkContext context)
    {
        ConfigProfile? profile = node.Profile;
        if (profile is null)
        {
            return inherited;
        }

        int interval = inherited.MaxSampleIntervalSeconds;
        if (profile.MaxSampleIntervalSeconds is int requested)
        {
            if (requested <= 0)
            {
                context.AddError(node.Id, path, "MaxSampleIntervalSeconds must be positive.");
            }
            else if (requested > inherited.MaxSampleIntervalSeconds)
            {
                context.AddError(
                    node.Id,
                    path,
                    $"MaxSampleIntervalSeconds of {requested}s is looser than the inherited limit of {inherited.MaxSampleIntervalSeconds}s.");
            }
            else
            {
                interval = requested;
            }
        }

        bool encryption = inherited.EncryptionRequired;
        if (profile.EncryptionRequired is bool requestedEncryption)
        {
            if (!requestedEncryption && inherited.EncryptionRequired)
            {
                context.AddError(node.Id, path, "Encryption cannot be switched off because an ancestor requires it.");
            }
            else
            {
                encryption = requestedEncryption;
            }
        }

        double? min = inherited.MinValue;
        if (profile.MinValue is double requestedMin)
        {
            if (inherited.MinValue is double inheritedMin && requestedMin < inheritedMin)
            {
                context.AddError(node.Id, path, $"Minimum {requestedMin} is below the inherited minimum {inheritedMin}.");
            }
            else
            {
                min = requestedMin;
            }
        }

        double? max = inherited.MaxValue;
        if (profile.MaxValue is double requestedMax)
        {
            if (inherited.MaxValue is double inheritedMax && requestedMax > inheritedMax)
            {
                context.AddError(node.Id, path, $"Maximum {requestedMax} is above the inherited maximum {inheritedMax}.");
            }
            else
            {
                max = requestedMax;
            }
        }

        if (min is double low && max is double high && low >= high)
        {
            context.AddError(node.Id, path, $"Value range is invalid: minimum {low} must be below maximum {high}.");
        }

        return new EffectiveProfile(interval, encryption, min, max);
    }

    // ------------------------------------------------------------------
    // Single-node placement check, e.g. Node -> Sub-Zone B -> Zone 1 -> Facility A
    // ------------------------------------------------------------------

    public PlacementResultDto CheckPlacement(IReadOnlyList<DeploymentNode> roots, string nodeId)
    {
        ArgumentNullException.ThrowIfNull(roots);

        if (!TopologyNavigator.TryFindPath(roots, nodeId, out List<DeploymentNode> path))
        {
            return new PlacementResultDto(
                nodeId,
                false,
                false,
                string.Empty,
                Array.Empty<string>(),
                new[] { new ValidationIssueDto(Error, nodeId, "(not found)", $"Node '{nodeId}' does not exist in the topology.") },
                null);
        }

        var context = new WalkContext();
        var checks = new List<string>();
        EffectiveProfile effective = CheckChain(path, 0, EffectiveProfile.Default, context, checks);
        TopologyValidationResultDto result = context.ToResult();

        return new PlacementResultDto(
            nodeId,
            true,
            result.ErrorCount == 0,
            TopologyNavigator.FormatChain(path),
            checks.ToArray(),
            result.Issues,
            effective);
    }

    /// <summary>Recursively walks the chain from the Facility down to the target node.</summary>
    private EffectiveProfile CheckChain(
        IReadOnlyList<DeploymentNode> chain,
        int index,
        EffectiveProfile inherited,
        WalkContext context,
        List<string> checks)
    {
        if (index >= chain.Count)
        {
            return inherited; // base case: reached the target
        }

        DeploymentNode node = chain[index];
        DeploymentNode? parent = index == 0 ? null : chain[index - 1];
        context.Path.Add(Label(node));
        string path = context.CurrentPath;

        if (parent is null)
        {
            if (node.Level == DeploymentLevel.Facility)
            {
                checks.Add($"✓ {Label(node)} is a Facility root.");
            }
            else
            {
                context.AddError(node.Id, path, $"The chain must start at a Facility, not a {node.Level}.");
            }
        }
        else if (IsAllowedChild(parent.Level, node.Level))
        {
            checks.Add($"✓ {Label(node)} ({node.Level}) is correctly placed inside {Label(parent)} ({parent.Level}).");
        }
        else
        {
            context.AddError(node.Id, path, $"A {node.Level} cannot sit under a {parent.Level}.");
        }

        int issuesBefore = context.Issues.Count;
        EffectiveProfile effective = MergeProfile(node, inherited, path, context);
        if (node.Profile is not null && context.Issues.Count == issuesBefore)
        {
            string encryption = effective.EncryptionRequired ? "required" : "optional";
            checks.Add($"✓ {Label(node)} profile is consistent with its ancestors (report within {effective.MaxSampleIntervalSeconds}s, encryption {encryption}).");
        }

        if (node.Level == DeploymentLevel.Node)
        {
            int beforeDevice = context.Issues.Count;
            ValidateDeviceNode(node, effective, path, context);
            if (context.Issues.Count == beforeDevice)
            {
                checks.Add($"✓ Device {node.DeviceMac} satisfies the effective profile of its branch.");
            }
        }

        return CheckChain(chain, index + 1, effective, context, checks); // recursive case
    }

    private static string Label(DeploymentNode node) =>
        string.IsNullOrWhiteSpace(node.Name) ? (string.IsNullOrWhiteSpace(node.Id) ? "(unnamed)" : node.Id) : node.Name;

    private sealed class WalkContext
    {
        public HashSet<object> Visiting { get; } = new(ReferenceEqualityComparer.Instance);
        public HashSet<string> SeenIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> SeenMacs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Path { get; } = new();
        public List<ValidationIssueDto> Issues { get; } = new();
        public int NodesChecked { get; set; }
        public int DevicesChecked { get; set; }

        public string CurrentPath => string.Join(" > ", Path);

        public string PathWith(string label) =>
            Path.Count == 0 ? label : $"{CurrentPath} > {label}";

        public void AddError(string nodeId, string path, string message) =>
            Issues.Add(new ValidationIssueDto(Error, nodeId ?? string.Empty, path, message));

        public void AddWarning(string nodeId, string path, string message) =>
            Issues.Add(new ValidationIssueDto(Warning, nodeId ?? string.Empty, path, message));

        public TopologyValidationResultDto ToResult()
        {
            int errors = Issues.Count(i => i.Severity == Error);
            int warnings = Issues.Count - errors;
            return new TopologyValidationResultDto(errors == 0, NodesChecked, DevicesChecked, errors, warnings, Issues.ToArray());
        }
    }
}
