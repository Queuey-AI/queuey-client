namespace Queuey.Edge;

/// <summary>Keys in the spool's <c>edge_meta</c> table. Public so the CLI can read what the host wrote.</summary>
public static class EdgeMetaKeys
{
    /// <summary>
    /// The node's stable identity: the configured <see cref="EdgeHealthReportOptions.NodeId"/>, or a UUIDv7 that health
    /// reporting minted on first use.
    /// </summary>
    public const string NodeId = "node_id";
}
