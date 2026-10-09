using System.Net;
using System.Text.Json;
using Queuey.Client.Cli;

namespace Queuey.Client.Cli.Tests;

/// <summary>
/// Klientens side av Queuey #514 (2026-10-09): mottakerens URL leses redigert av en nøkkel og en innlogging. pull skriver aldri en
/// redigert URL inn i fila, apply sender filens verdi og viser 400 redacted_url_written_back tydelig, apply --check sammenligner
/// mot den redigerte lesingen, og plan viser en endring i den skjulte delen som det den er.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class RedactedUrlsTests
{
    // ── TargetUrlRedaction, kopiert på nytt fra #514 ────────────────────────

    [Theory]
    [InlineData("https://h.test/in/AbCdEfGhIjKlMn", "https://h.test/in/…")]          // et token av bokstaver leses ikke lenger som et ord
    [InlineData("https://h.test/orders/v1", "https://h.test/orders/v1")]
    [InlineData("/orders/s3cr3t-T0ken9", "/orders/…")]                               // en sti alene
    public void The_rules_are_queueys_from_514(string url, string shown)
        => Assert.Equal(shown, TargetUrlRedaction.Redact(url));

    [Fact]
    public void Text_redacts_a_url_without_a_scheme_and_an_encoded_one()
    {
        Assert.Equal("failed at hooks.slack.com/services/T1/B2/…", TargetUrlRedaction.RedactUrlsIn("failed at hooks.slack.com/services/T1/B2/xoxbSECRET"));
        Assert.DoesNotContain("hunter2", TargetUrlRedaction.RedactUrlsIn("ops:hunter2@shop.test/in") );
        Assert.DoesNotContain("s3cr3t", TargetUrlRedaction.RedactUrlsIn("https%3A%2F%2Fshop.test%2Fin%2Fs3cr3t9x"));
    }
}
