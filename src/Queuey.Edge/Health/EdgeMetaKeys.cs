namespace Queuey.Edge;

/// <summary>Keys in the spool's <c>edge_meta</c> table. Public so the CLI can read what the host wrote.</summary>
public static class EdgeMetaKeys
{
    /// <summary>
    /// The node's stable identity: the configured <see cref="EdgeHealthReportOptions.NodeId"/>, or a UUIDv7 that
    /// <see cref="EdgeNodeIdentity"/> minted the first time something on the node needed it (health reporting, or an
    /// intake that names itself after the node).
    /// </summary>
    public const string NodeId = "node_id";

    /// <summary>
    /// When Edge created this spool file, as an ISO 8601 UTC timestamp. A spool written by Edge 0.1.0-preview.13 or
    /// earlier has none, and that is how a newer Edge tells such a spool apart from a new one. A recovered spool keeps
    /// the value of the file it was salvaged from.
    /// </summary>
    public const string CreatedUtc = "created_utc";

    /// <summary>
    /// The client id the MQTT intake (<c>Queuey.Edge.Mqtt</c>) last connected to its broker with, kept so that the node
    /// keeps its broker session when its hostname changes, as it does with every new container.
    /// </summary>
    public const string MqttClientId = "mqtt_client_id";
}
