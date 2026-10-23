using SmartX.Core.Contracts;
using SmartX.Core.Devices;
using SmartX.Core.Telemetry;

namespace SmartX.Api.Services;

/// <summary>
/// Manual override commands with a <b>Stack&lt;CommandRecord&gt;</b> undo history.
/// Each record stores the <i>previous</i> state of every actuator it touched, so popping the stack (LIFO)
/// restores exactly what the operator last changed - including batch actions such as "shut down all valves",
/// which are undone as one unit.
/// </summary>
public sealed class CommandService(DeviceRegistry registry, InsightEngine insights, AlertService alerts)
{
    private sealed record Change(string Mac, string Name, bool Before, bool After);

    private sealed record CommandRecord(long Id, string Description, string Source, DateTime IssuedUtc, List<Change> Changes);

    private readonly Stack<CommandRecord> _undo = new();
    private readonly LinkedList<CommandRecordDto> _log = new();
    private readonly Lock _gate = new();
    private long _nextId;

    public (CommandRecordDto? Record, string? Error) Issue(CommandRequest request)
    {
        // Resolve targets first (registry lookups are O(1) per MAC).
        var targets = new List<SensorDevice>();
        string description;

        if (request.Scope == CommandScope.Device)
        {
            if (string.IsNullOrWhiteSpace(request.Mac) || !MacAddress.TryNormalize(request.Mac, out var mac))
                return (null, "A valid device MAC is required.");
            if (!registry.TryGet(mac, out var device)) return (null, $"Device {mac} is not registered.");
            if (device.Category != SensorCategory.Actuator) return (null, $"{device.DisplayName} is a sensor, not an actuator.");
            targets.Add(device);
            description = $"{Verb(request.Action)} {device.DisplayName}";
        }
        else
        {
            if (string.IsNullOrWhiteSpace(request.Metric) || !MetricCatalog.TryGet(request.Metric, out var def) || def.Category != SensorCategory.Actuator)
                return (null, "Batch commands need an actuator metric (pump_state, valve_state or relay_state).");
            foreach (var d in registry.Snapshot())
            {
                if (d.Metric == def.Key && registry.TryGet(d.Mac, out var dev)) targets.Add(dev);
            }
            if (targets.Count == 0) return (null, "No actuators of that type are registered.");
            description = $"{Verb(request.Action)} ALL {def.Label.ToLowerInvariant()}s ({targets.Count})";
        }

        CommandRecord record;
        lock (_gate)
        {
            var changes = new List<Change>(targets.Count);
            foreach (var t in targets)
            {
                bool current = t.ActuatorOn ?? false;
                bool after = request.Action switch
                {
                    CommandAction.On => true,
                    CommandAction.Off => false,
                    _ => !current
                };
                var before = registry.SetActuator(t.Mac, after) ?? current;
                changes.Add(new Change(t.Mac, t.DisplayName, before, after));
            }
            record = new CommandRecord(++_nextId, description, request.Source, DateTime.UtcNow, changes);
            _undo.Push(record);                     // newest command on top
            AddLogLocked(ToDto(record, undone: false));
        }

        // Feed the predictive engine (outside our lock).
        var conditions = alerts.ActiveConditions();
        foreach (var c in record.Changes)
        {
            insights.RecordCommand(c.Mac, c.After ? CommandAction.On : CommandAction.Off, conditions);
        }
        return (ToDto(record, false), null);
    }

    /// <summary>Pops the most recent command and restores every actuator it changed.</summary>
    public CommandRecordDto? Undo()
    {
        lock (_gate)
        {
            if (!_undo.TryPop(out var record)) return null;
            foreach (var c in record.Changes) registry.SetActuator(c.Mac, c.Before);

            var dto = new CommandRecordDto(record.Id, $"UNDO: {record.Description}", "Undo", DateTime.UtcNow,
                record.Changes.Count, true,
                record.Changes.Select(c => $"{c.Name}: {OnOff(c.After)} → {OnOff(c.Before)}").ToList());
            AddLogLocked(dto);
            return dto;
        }
    }

    public CommandStateDto State()
    {
        lock (_gate)
        {
            // Stack enumerates top → bottom, i.e. the next command Undo will revert comes first.
            var stack = _undo.Take(10).Select(r => ToDto(r, false)).ToList();
            return new CommandStateDto(stack, _log.Take(20).ToList(), registry.ActuatorStates());
        }
    }

    public int UndoDepth
    {
        get { lock (_gate) return _undo.Count; }
    }

    private void AddLogLocked(CommandRecordDto dto)
    {
        _log.AddFirst(dto);
        while (_log.Count > 50) _log.RemoveLast();
    }

    private static CommandRecordDto ToDto(CommandRecord r, bool undone) =>
        new(r.Id, r.Description, r.Source, r.IssuedUtc, r.Changes.Count, undone,
            r.Changes.Select(c => $"{c.Name}: {OnOff(c.Before)} → {OnOff(c.After)}").ToList());

    private static string OnOff(bool b) => b ? "ON" : "OFF";

    private static string Verb(CommandAction a) => a switch
    {
        CommandAction.On => "Switch ON",
        CommandAction.Off => "Switch OFF",
        _ => "Toggle"
    };
}
