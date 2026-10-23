using SmartX.Core.Contracts;
using SmartX.Core.Deployment;
using SmartX.Core.Devices;
using SmartX.Core.Telemetry;

namespace SmartX.Api.Services;

/// <summary>Server-side validation + registration of a new sensor profile.</summary>
public sealed class RegistrationService(DeviceRegistry registry, DeploymentService deployment, AttachmentStore attachments)
{
    public (RegisterSensorResponse? Result, Dictionary<string, string[]> Errors) Register(RegisterSensorRequest req, DateTime? registeredUtc = null)
    {
        var errors = new Dictionary<string, string[]>();

        if (!MacAddress.TryNormalize(req.Mac, out var mac))
            errors[nameof(req.Mac)] = ["Enter a valid MAC address, e.g. 24:6F:28:1A:2B:3C (all-zero/broadcast not allowed)."];
        else if (registry.Contains(mac)) // O(1) duplicate check
            errors[nameof(req.Mac)] = [$"{mac} is already registered."];

        var name = req.DisplayName?.Trim() ?? "";
        if (name.Length is < 3 or > 32)
            errors[nameof(req.DisplayName)] = ["Display name must be 3-32 characters."];
        else if (registry.Snapshot().Exists(d => d.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase)))
            errors[nameof(req.DisplayName)] = [$"A device called '{name}' already exists."];

        if (!MetricCatalog.TryGet(req.Metric ?? "", out var metric))
            errors[nameof(req.Metric)] = ["Choose a metric."];
        else if (metric.Category != req.Category)
            errors[nameof(req.Metric)] = [$"'{metric.Label}' belongs to {metric.Category}, not {req.Category}."];

        PathValidationResult? path = null;
        if (string.IsNullOrWhiteSpace(req.NodeId))
        {
            errors[nameof(req.NodeId)] = ["Choose the deployment node."];
        }
        else
        {
            path = deployment.ValidatePath(req.NodeId); // recursive tree walk + per-tier safety checks
            if (!path.Found || !path.IsSafe)
                errors[nameof(req.NodeId)] = path.Problems.Count > 0 ? path.Problems.ToArray() : ["Deployment path is not valid."];
            else
            {
                var node = deployment.Find(req.NodeId)!;
                if (registry.CountOnNode(node.Id) >= node.MaxSensors)
                    errors[nameof(req.NodeId)] = [$"{node.Name} already hosts {node.MaxSensors} sensors (ESP32 GPIO/power budget)."];
            }
        }

        if (errors.Count > 0) return (null, errors);

        var (facilityId, zone, locationPath) = deployment.Describe(req.NodeId);
        var device = new SensorDevice
        {
            Mac = mac,
            DisplayName = name,
            Category = req.Category,
            Metric = metric,
            NodeId = deployment.Find(req.NodeId)!.Id,
            LocationPath = locationPath,
            Zone = zone,
            FacilityId = facilityId,
            RegisteredUtc = registeredUtc ?? DateTime.UtcNow,
            AttachmentCount = attachments.Count(mac)
        };

        if (!registry.TryAdd(device))
            return (null, new Dictionary<string, string[]> { [nameof(req.Mac)] = [$"{mac} is already registered."] });

        return (new RegisterSensorResponse(registry.Get(mac)!, path!), errors);
    }
}
