using System;
using System.Collections.Generic;
using Queuey.Client;

namespace Queuey.Edge.Mqtt;

/// <summary>
/// An MQTT source: subscribe to a local broker and hand each message to the
/// durable Edge spool. This is an INTAKE adapter, not a bridge — there is no
/// transformation, no fan-out and no delivery here. The broker is typically
/// on the same gateway (Mosquitto, NanoMQ, the PLC vendor's broker); the
/// only thing Edge adds is that a message the broker handed us is durably
/// ours before we acknowledge it.
/// </summary>
public sealed class MqttSourceOptions
{
    /// <summary>Broker host. Defaults to the local broker.</summary>
    public string Server { get; set; } = "localhost";

    public int Port { get; set; } = 1883;

    public bool UseTls { get; set; }

    public string? Username { get; set; }

    public string? Password { get; set; }

    /// <summary>Stable client id. Defaults to <c>queuey-edge-{machine}</c> so a persistent session survives restarts.</summary>
    public string? ClientId { get; set; }

    /// <summary>Topic filter → queue routes. At least one is required.</summary>
    public List<MqttRoute> Routes { get; } = new();

    /// <summary>Reconnect delay ladder start (doubles up to <see cref="ReconnectDelayCap"/>).</summary>
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan ReconnectDelayCap { get; set; } = TimeSpan.FromMinutes(1);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Server))
            throw new QueueyConfigurationException("MqttSourceOptions.Server is required.");
        if (Port is < 1 or > 65535)
            throw new QueueyConfigurationException("MqttSourceOptions.Port must be 1–65535.");
        if (Routes.Count == 0)
            throw new QueueyConfigurationException("MqttSourceOptions.Routes needs at least one topic filter → queue route.");
        foreach (var r in Routes) r.Validate();
    }
}

/// <summary>
/// One subscription: messages matching <see cref="TopicFilter"/> become
/// events on <see cref="Queue"/>. Optionally a topic segment becomes the
/// event's group key (the lane) — e.g. filter <c>plant/+/alarms</c> with
/// <c>GroupKeySegment = 1</c> gives one FIFO lane per machine.
/// </summary>
public sealed class MqttRoute
{
    /// <summary>MQTT topic filter (<c>+</c> and <c>#</c> wildcards allowed).</summary>
    public string TopicFilter { get; set; } = string.Empty;

    /// <summary>Queuey queue (display name) the messages publish to.</summary>
    public string Queue { get; set; } = string.Empty;

    /// <summary>Zero-based index of the topic segment to use as <c>GroupKey</c>; null = the queue's default lane.</summary>
    public int? GroupKeySegment { get; set; }

    /// <summary>Optional <c>X-Queuey-Event-Type</c>. Null = the full topic.</summary>
    public string? EventType { get; set; }

    /// <summary>Content type recorded for the payload when the message carries none. Default JSON.</summary>
    public string DefaultContentType { get; set; } = "application/json";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(TopicFilter))
            throw new QueueyConfigurationException("MqttRoute.TopicFilter is required.");
        if (string.IsNullOrWhiteSpace(Queue))
            throw new QueueyConfigurationException($"MqttRoute for '{TopicFilter}' needs a Queue.");
        if (GroupKeySegment is < 0)
            throw new QueueyConfigurationException($"MqttRoute for '{TopicFilter}': GroupKeySegment must be ≥ 0.");
    }

    /// <summary>
    /// Parses the CLI shorthand <c>filter=queue[@segment]</c>, several
    /// separated by <c>;</c> — e.g. <c>plant/+/alarms=alarms@1;plant/+/state=machine-state@1</c>.
    /// </summary>
    public static List<MqttRoute> ParseMany(string spec)
    {
        var routes = new List<MqttRoute>();
        foreach (var raw in (spec ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = raw.IndexOf('=');
            if (eq <= 0 || eq == raw.Length - 1)
                throw new QueueyConfigurationException($"MQTT route '{raw}' must look like filter=queue[@segment].");
            var filter = raw[..eq].Trim();
            var rest = raw[(eq + 1)..].Trim();
            int? segment = null;
            var at = rest.LastIndexOf('@');
            if (at > 0)
            {
                if (!int.TryParse(rest[(at + 1)..], out var seg) || seg < 0)
                    throw new QueueyConfigurationException($"MQTT route '{raw}': segment after '@' must be a non-negative integer.");
                segment = seg;
                rest = rest[..at];
            }
            routes.Add(new MqttRoute { TopicFilter = filter, Queue = rest, GroupKeySegment = segment });
        }
        return routes;
    }
}
