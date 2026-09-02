namespace Queuey.Client.Waas.Tests;

[QueueyModel("order-events", Description = "Order lifecycle events", EventTypes = new[] { "order.created", "order.paid" })]
public sealed class OrderCreated
{
    public string OrderId { get; init; } = string.Empty;
    public decimal Total { get; init; }
}

/// <summary>No attribute — stream name should fall back to the type name (and excluded from assembly scans).</summary>
public sealed class InvoiceIssued
{
    public string InvoiceId { get; init; } = string.Empty;
}

/// <summary>A stream shared across two packages (tiers).</summary>
[QueueyModel("tiered-orders", Packages = new[] { "standard", "premium" })]
public sealed class TieredOrder
{
    public string OrderId { get; init; } = string.Empty;
}

/// <summary>Exercises schema generation: mixed types, ignore, and rename.</summary>
[QueueyModel("schema-events", GenerateSchema = true)]
public sealed class SchemaModel
{
    public string Id { get; init; } = string.Empty;
    public int Count { get; init; }
    public decimal Amount { get; init; }
    public bool Active { get; init; }
    public System.DateTimeOffset At { get; init; }
    public string[] Tags { get; init; } = System.Array.Empty<string>();

    [QueueyIgnore] public string Secret { get; init; } = string.Empty;
    [System.Text.Json.Serialization.JsonIgnore] public string Internal { get; init; } = string.Empty;
    [System.Text.Json.Serialization.JsonPropertyName("renamed")] public string Original { get; init; } = string.Empty;
}

/// <summary>A type whose name normalizes to a reserved routing segment — convention can't save it.</summary>
public sealed class Sandbox
{
    public string Id { get; init; } = string.Empty;
}

/// <summary>A queue declared with an explicit name and a partial policy — the rest inherits.</summary>
[QueueyQueue("orders", Description = "Order pipeline", Ordering = "bykey", MaxAttempts = 8)]
public sealed class OrderQueue
{
    public string OrderId { get; init; } = string.Empty;
}

/// <summary>A queue with no attribute values at all — name by convention, behaviour fully inherited.</summary>
[QueueyQueue]
public sealed class ShipmentUpdates
{
    public string ShipmentId { get; init; } = string.Empty;
}
