using System;

namespace Queuey.Client.Waas.Tests;

/// <summary>
/// Deploy-filas tenant er en ten_-id (F2.7, 2026-10-06), sjekket i Resolve for hver kommando som leser fila. Før ble den
/// sjekket bare når --tenant eller QUEUEY_TENANT navnga et annet workspace, så en API-nøkkel limt inn som tenant gikk ut i
/// URL-ene. Verdien vises aldri.
/// </summary>
public class DeploymentTenantIdTests
{
    [Theory]
    [InlineData("qak_kid.pasted-secret")]
    [InlineData("whsec_1a2b3c")]
    [InlineData("ten_has space")]
    [InlineData("TEN_upper")]
    [InlineData("ten_")]
    public void A_tenant_that_is_not_a_workspace_id_is_refused_without_showing_it(string tenant)
    {
        var ex = Assert.Throws<QueueyConfigurationException>(() =>
            DeploymentFile.Parse($$"""{ "tenant": {{System.Text.Json.JsonSerializer.Serialize(tenant)}}, "queues": {} }""").Resolve());

        // Teksten er fast, så verdien står ikke i den.
        Assert.Equal("The deployment file's tenant is not a workspace id. Its value is not shown, since it may be a secret.", ex.Message);
        Assert.Equal("A workspace id starts with ten_, as the Queuey console shows it. An API key belongs in --api-key " +
                     "or QUEUEY_API_KEY, never in the deployment file.", ex.SuggestedAction);
    }

    [Fact]
    public void A_workspace_id_or_none_passes()
    {
        DeploymentFile.Parse("""{ "tenant": "ten_abc-1_X", "queues": {} }""").Resolve();
        DeploymentFile.Parse("""{ "queues": {} }""").Resolve();
    }

    [Fact]
    public void A_tenant_from_a_variable_is_checked_once_it_is_expanded()
    {
        DeploymentFile file = DeploymentFile.Parse("""{ "tenant": "${QUEUEY_TENANT_ID}", "queues": {} }""");

        file.Resolve();   // som fila står, i en dry run
        file.Expand(_ => "ten_staging").Resolve();
        var ex = Assert.Throws<QueueyConfigurationException>(() => file.Expand(_ => "qak_kid.secret").Resolve());
        Assert.DoesNotContain("qak_kid", ex.Message + ex.SuggestedAction);
    }
}
