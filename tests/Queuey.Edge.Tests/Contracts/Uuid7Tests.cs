using Queuey.Edge;

namespace Queuey.Edge.Tests.Contracts;

/// <summary>
/// The transfer identity's shape: valid UUIDv7 text, time-ordered across
/// millisecond boundaries, unique under burst. Ordering matters because the
/// id is both the spool's dedup key and a human-sortable correlation handle.
/// </summary>
public class Uuid7Tests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Produces_canonical_uuid_text_with_version_7()
    {
        var id = Uuid7.NewString(T0);

        Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", id);
        Assert.True(Guid.TryParse(id, out _));
    }

    [Fact]
    public void Ids_order_by_time()
    {
        var earlier = Uuid7.NewString(T0);
        var later = Uuid7.NewString(T0.AddMilliseconds(2));

        Assert.True(string.CompareOrdinal(earlier, later) < 0,
            "the 48-bit millisecond prefix must make lexicographic order follow publish time");
    }

    [Fact]
    public void Burst_of_ids_is_unique()
    {
        var ids = Enumerable.Range(0, 10_000).Select(_ => Uuid7.NewString(T0)).ToHashSet();
        Assert.Equal(10_000, ids.Count);
    }
}
