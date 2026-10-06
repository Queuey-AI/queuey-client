using Queuey.Client;

namespace Queuey.Client.Waas.Tests;

/// <summary>
/// Hva en ingress krever, mot det denne klienten kan gi (F2.7, 2026-10-06): en nøkkel, Queuey sin signatur, eller en providers
/// signatur. Det klienten ikke kan gi, avvises før noe sendes; resten avgjør ingressen.
/// </summary>
public class QueuePublishRefusalTests
{
    private static QueueyOptions ApiKey => new() { ApiKey = "qak_kid.secret", TenantPublicId = "ten_abc" };

    private static QueueyOptions Signer => new() { SigningKeyId = "isk_1", SigningSecret = "s3cret", TenantPublicId = "ten_abc" };

    private static IngressResponse Ingress(string? authMode, string? template = null)
        => new() { AuthMode = authMode, SignedRequest = template is null ? null : new SignedRequestResponse { Template = template } };

    [Theory]
    [InlineData(null)]
    [InlineData("None")]
    [InlineData("ApiKey")]
    [InlineData("SomethingNewer")]
    public void An_ingress_this_client_can_publish_to_or_does_not_know_is_left_to_the_ingress(string? authMode)
        => Assert.Null(QueuePublisher.IngressRefusal(Ingress(authMode), ApiKey, "orders"));

    [Fact]
    public void An_api_key_ingress_without_a_configured_key_is_refused()
    {
        QueueyException? refusal = QueuePublisher.IngressRefusal(Ingress("ApiKey"), Signer, "orders");

        Assert.Equal("ingress_requires_api_key", refusal?.ErrorCode);
    }

    [Fact]
    public void Queueys_own_signature_is_made_only_by_a_client_that_signs_and_has_no_api_key()
    {
        Assert.Null(QueuePublisher.IngressRefusal(Ingress("SignedRequest", "queuey"), Signer, "orders"));

        // Med begge satt sender klienten nøkkelen og signerer ikke (QueueyClient.BuildIngressAuthenticator).
        var both = new QueueyOptions { ApiKey = "qak_kid.secret", SigningKeyId = "isk_1", SigningSecret = "s3cret" };
        Assert.Equal("ingress_requires_signature", QueuePublisher.IngressRefusal(Ingress("SignedRequest", "queuey"), both, "orders")?.ErrorCode);
        Assert.Equal("ingress_requires_signature", QueuePublisher.IngressRefusal(Ingress("SignedRequest", "queuey"), ApiKey, "orders")?.ErrorCode);
    }

    [Theory]
    [InlineData("SignedRequest", "stripe")]
    [InlineData("SignedRequest", null)]
    [InlineData("ApiKeyAndSignedRequest", "queuey")]
    [InlineData("ApiKeyAndSignedRequest", "stripe")]
    public void A_providers_signature_or_a_key_and_a_signature_together_is_never_made_here(string authMode, string? template)
    {
        QueueyException? refusal = QueuePublisher.IngressRefusal(Ingress(authMode, template), Signer, "orders");

        Assert.Equal("ingress_requires_signature", refusal?.ErrorCode);
        Assert.Contains("nothing was published", refusal!.Message);
    }

    [Fact]
    public void A_template_or_queue_that_is_not_a_plain_word_is_left_out_of_the_command_it_suggests()
    {
        QueueyException? refusal = QueuePublisher.IngressRefusal(Ingress("SignedRequest", "stripe; rm -rf /"), ApiKey, "orders && curl x");

        Assert.DoesNotContain("rm -rf", refusal!.Message + refusal.SuggestedAction);
        Assert.DoesNotContain("curl", refusal.SuggestedAction);
        Assert.Contains("queuey verify <queue>", refusal.SuggestedAction);
    }
}
