using Queuey.Client;

namespace Queuey.Client.Tests;

public class QueueyNameTests
{
    [Theory]
    [InlineData("order-events")]
    [InlineData("orders")]
    [InlineData("order.created")]
    [InlineData("order_events")]
    [InlineData("q1")]
    [InlineData("1orders")]
    public void Accepts_names_the_server_accepts(string name)
        => Assert.True(QueueyName.IsValid(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("OrderEvents")]     // uppercase
    [InlineData("order events")]    // whitespace inside
    [InlineData("-orders")]         // must start with a letter or digit
    [InlineData(".orders")]
    [InlineData("_orders")]
    [InlineData("order/events")]
    [InlineData("ordrekø")]         // non-ASCII
    [InlineData("sandbox")]         // reserved routing segment
    public void Rejects_names_the_server_rejects(string? name)
        => Assert.False(QueueyName.IsValid(name));

    [Fact]
    public void Rejects_names_longer_than_the_max()
    {
        Assert.True(QueueyName.IsValid(new string('a', QueueyName.MaxLength)));
        Assert.False(QueueyName.IsValid(new string('a', QueueyName.MaxLength + 1)));
    }

    [Fact]
    public void Ignores_surrounding_whitespace_like_the_server_does()
        => Assert.True(QueueyName.IsValid("  orders  "));

    [Theory]
    [InlineData("OrderCreated", "order-created")]
    [InlineData("InvoiceIssued", "invoice-issued")]
    [InlineData("HTTPOrderCreated", "http-order-created")]
    [InlineData("Order2Created", "order2-created")]
    [InlineData("orders", "orders")]
    [InlineData("order-events", "order-events")]
    [InlineData("Order.Created", "order.created")]
    [InlineData("Order Created", "order-created")]
    [InlineData("  Order / Created  ", "order-created")]
    [InlineData("Order--Created", "order-created")]
    [InlineData("_Order", "order")]
    [InlineData("Envelope`1", "envelope")]
    public void Normalizes_to_a_name_the_server_accepts(string input, string expected)
    {
        string normalized = QueueyName.Normalize(input);
        Assert.Equal(expected, normalized);
        Assert.True(QueueyName.IsValid(normalized));
    }

    [Fact]
    public void Normalize_truncates_to_the_max_without_a_trailing_separator()
    {
        // 20 × "Abc" normalizes to "abc-abc-…" (79 chars), and the cut at 64 lands exactly on a
        // separator — which must be trimmed, or the name would end in '-'.
        string normalized = QueueyName.Normalize(string.Concat(Enumerable.Repeat("Abc", 20)));

        Assert.Equal(QueueyName.MaxLength - 1, normalized.Length);
        Assert.EndsWith("c", normalized);
        Assert.True(QueueyName.IsValid(normalized));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("///", "")]
    public void Normalize_returns_empty_when_nothing_usable_remains(string? input, string expected)
        => Assert.Equal(expected, QueueyName.Normalize(input));

    [Fact]
    public void Normalize_is_best_effort_and_can_still_yield_a_reserved_name()
    {
        // Callers must validate the result rather than assume it — this is why the factory checks.
        Assert.Equal("sandbox", QueueyName.Normalize("Sandbox"));
        Assert.False(QueueyName.IsValid(QueueyName.Normalize("Sandbox")));
    }

    [Fact]
    public void EnsureValid_passes_a_valid_name_through()
        => QueueyName.EnsureValid("order-events", "stream name");

    [Fact]
    public void EnsureValid_throws_with_the_rule_and_a_suggestion()
    {
        var ex = Assert.Throws<QueueyConfigurationException>(
            () => QueueyName.EnsureValid("OrderEvents", "stream name"));

        Assert.Contains("stream name", ex.Message);
        Assert.Contains("OrderEvents", ex.Message);
        Assert.Contains("Did you mean 'order-events'?", ex.Message);
    }

    [Fact]
    public void EnsureValid_omits_the_suggestion_when_normalizing_helps_nothing()
    {
        var ex = Assert.Throws<QueueyConfigurationException>(
            () => QueueyName.EnsureValid("sandbox", "queue name"));

        Assert.Contains("reserved", ex.Message);
        Assert.DoesNotContain("Did you mean", ex.Message);
    }
}
