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
