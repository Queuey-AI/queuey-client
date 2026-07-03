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
