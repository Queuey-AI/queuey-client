using System.Collections.Generic;
using System.Linq;

namespace Queuey.Client.Waas.Tests;

/// <summary>
/// SDK-en uten reglene fra CLI-en (security-review av queuey-client #72, N4): en URL Queuey viser redigert, sammenlignes på
/// skjema og vert, og noe annet ulikt er drift. Aldri «i synk» i stillhet, og filens URL vises aldri hel.
/// </summary>
public class RedactedUrlDriftTests
{
    private static DeploymentFile File(string url) => DeploymentFile.Parse(
        "{ \"tenant\": \"ten_abc\", \"queues\": { \"orders\": { \"delivery\": { \"url\": \"" + url + "\" } } } }");

    [Fact]
    public void Without_the_rules_a_redacted_url_on_the_same_host_is_listed_not_called_in_sync_by_silence()
    {
        DeploymentUrls.Redact = null;
        var compared = new List<string>();

        IReadOnlyList<DriftItem> drift = DeploymentDrift.Compare(File("https://h.test/in/s3cr3t"), File("https://h.test/in/…"), compared);

        Assert.Empty(drift);
        Assert.Equal(new[] { "queues.orders.delivery.url" }, compared);
    }

    [Fact]
    public void Without_the_rules_another_host_or_any_other_difference_is_drift_and_shown_without_its_path()
    {
        DeploymentUrls.Redact = null;

        DriftItem host = Assert.Single(DeploymentDrift.Compare(File("https://other.test/in/s3cr3t"), File("https://h.test/in/…"), new List<string>()));
        DriftItem whole = Assert.Single(DeploymentDrift.Compare(File("https://h.test/in?token=abc"), File("https://h.test/in?token=xyz"), new List<string>()));

        Assert.Equal("https://other.test/…", host.Declared);
        Assert.DoesNotContain("s3cr3t", host.Declared + host.Actual);
        Assert.DoesNotContain("abc", whole.Declared + whole.Actual);
        Assert.DoesNotContain("xyz", whole.Declared + whole.Actual);
    }

    [Fact]
    public void The_check_says_which_urls_it_compared_redacted_on_its_public_surface()
        => Assert.NotNull(typeof(DeploymentCheck).GetProperty(nameof(DeploymentCheck.ComparedRedacted))!.GetGetMethod());

    // Queuey #517: «...» gjelder som markøren der redigeringen setter den, som i RedactedUrlWrites.CarriesMarker.
    [Theory]
    [InlineData("https://h.test/in/...", true)]
    [InlineData("https://h.test/in/.../x", true)]
    [InlineData("https://h.test/in?...", true)]
    [InlineData("https://h.test/in#...", true)]
    [InlineData("https://...@h.test/in", true)]
    [InlineData("https://h.test/in/a...b", false)]          // tre punktum utenfor markørens plass er en vanlig sti
    [InlineData("https://h.test/in?q=...", false)]
    public void Three_dots_count_as_the_marker_only_where_the_redaction_puts_it(string url, bool marker)
        => Assert.Equal(marker, DeploymentUrls.CarriesMarker(url));

}
