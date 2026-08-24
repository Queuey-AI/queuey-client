using Queuey.Edge;

namespace Queuey.Edge.Tests.Contracts;

/// <summary>
/// TransferClass/TransferReason are PERSISTED on spool rows: names ride
/// string conversion, numbers ride any numeric serialization — append only,
/// never reorder or rename. This test freezes the numeric values (the same
/// discipline the backend applies to FailureClass/DeliveryDecisionKind).
/// A failing assertion here means a migration story is owed, not a rename.
/// </summary>
public class FrozenEnumTests
{
    [Fact]
    public void TransferClass_values_are_frozen()
    {
        Assert.Equal(0, (int)TransferClass.Accepted);
        Assert.Equal(1, (int)TransferClass.Transient);
        Assert.Equal(2, (int)TransferClass.Throttled);
        Assert.Equal(3, (int)TransferClass.RequiresAction);
        Assert.Equal(4, (int)TransferClass.EventRejected);
        Assert.Equal(5, (int)TransferClass.Unknown);
        Assert.Equal(6, Enum.GetValues<TransferClass>().Length);
    }

    [Fact]
    public void TransferReason_values_are_frozen()
    {
        Assert.Equal(0, (int)TransferReason.Accepted);
        Assert.Equal(1, (int)TransferReason.Replayed);
        Assert.Equal(10, (int)TransferReason.Timeout);
        Assert.Equal(11, (int)TransferReason.DnsFailure);
        Assert.Equal(12, (int)TransferReason.ConnectionRefused);
        Assert.Equal(13, (int)TransferReason.ConnectionReset);
        Assert.Equal(14, (int)TransferReason.CloudServerError);
        Assert.Equal(20, (int)TransferReason.RateLimited);
        Assert.Equal(30, (int)TransferReason.AuthenticationRejected);
        Assert.Equal(31, (int)TransferReason.Forbidden);
        Assert.Equal(32, (int)TransferReason.RouteUnknown);
        Assert.Equal(33, (int)TransferReason.QueuePaused);
        Assert.Equal(34, (int)TransferReason.BillingBlocked);
        Assert.Equal(35, (int)TransferReason.TlsFailure);
        Assert.Equal(40, (int)TransferReason.MalformedRequest);
        Assert.Equal(41, (int)TransferReason.PayloadTooLarge);
        Assert.Equal(42, (int)TransferReason.UnsupportedContentType);
        Assert.Equal(99, (int)TransferReason.Unclassified);
    }

    [Fact]
    public void EdgeState_values_are_frozen()
    {
        // Exposed as a numeric gauge on the OTel meter — same append-only rule.
        Assert.Equal(0, (int)EdgeState.Healthy);
        Assert.Equal(1, (int)EdgeState.Backlogged);
        Assert.Equal(2, (int)EdgeState.RequiresAction);
        Assert.Equal(3, (int)EdgeState.StorageFull);
        Assert.Equal(4, (int)EdgeState.StorageFaulted);
    }
}
