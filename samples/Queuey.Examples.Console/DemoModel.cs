using System.Text.Json.Serialization;
using Queuey.Client.Waas;

namespace Queuey.Examples;

/// <summary>
/// A producer event model. <see cref="QueueyModelAttribute"/> declares the stream name, the event
/// types it advertises, and the packages it's published in. On <c>SyncModels</c> the stream and its
/// packages are created (idempotently) and the stream assigned to each package.
/// </summary>
[QueueyModel("order-events",
    EventTypes = new[] { "order.created", "order.paid" },
    Packages = new[] { "standard", "premium" },
    GenerateSchema = true)]
public sealed class OrderCreated
{
    public string OrderId { get; init; } = "";
    public decimal Total { get; init; }
    public string Currency { get; init; } = "NOK";

    // Excluded from the generated payloadSchema. (Use [JsonIgnore] to also drop it from the wire body.)
    [QueueyIgnore]
    public string? InternalRef { get; init; }
}
