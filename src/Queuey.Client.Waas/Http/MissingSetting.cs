namespace Queuey.Client.Waas;

/// <summary>
/// The errors for a setting a call needs and nobody set, with where to set it — the same wording whether the control-plane
/// client or <see cref="QueueyService"/> finds it missing.
/// </summary>
// Re-review 2026-10-05: QueueyService sa «required for SyncStreams» også for apply, pull og verify, og uten handling.
internal static class MissingSetting
{
    public static QueueyConfigurationException Tenant() =>
        new("A workspace (ten_…) is required for this call, and none is set.")
        {
            SuggestedAction = "Set QueueyOptions.TenantPublicId. In the CLI: --tenant, QUEUEY_TENANT, or tenant in queuey.json.",
        };

    public static QueueyConfigurationException ApiKey() =>
        new("An API key or a login is required for this call, and neither is set.")
        {
            SuggestedAction = "In the CLI: run `queuey login`, or set --api-key, QUEUEY_API_KEY, or apiKey in queuey.json. " +
                              "In the SDK: QueueyOptions.ApiKey or AccessTokenProvider. Keys are made in the Queuey console.",
        };

    public static QueueyConfigurationException License() =>
        new("A license id (lic_…) is required for this call, and none is set.")
        {
            SuggestedAction = "Set QueueyOptions.LicensePublicId. In the CLI: --license, QUEUEY_LICENSE, or license in queuey.json. " +
                              "The Queuey console shows it with your keys.",
        };
}
