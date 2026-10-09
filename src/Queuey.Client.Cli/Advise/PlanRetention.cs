using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Queuey.Client;

namespace Queuey.Client.Cli.Advise;

// Blindtesten 2026-10-09 (funn 2): advise skrev retentionDays 30, og Free tar høyst 7, så apply ble avvist med
// retention_cap_exceeded. Standarden er nå innenfor planen: lisensens, lest med innloggingen, ellers Free sine 7 dager.

/// <summary>The retention a scaffolded queue gets: at most 30 days, and never more than the license's plan keeps events.</summary>
internal static class PlanRetention
{
    /// <summary>What a queue gets when nothing larger is known: Free's window, which every plan allows.</summary>
    internal const int FreeDays = 7;

    /// <summary>The most advise ever proposes, even on a plan that keeps events longer.</summary>
    internal const int MostDays = 30;

    /// <summary>
    /// The days to scaffold, and a sentence saying where they come from. With a login or a key for the API host, the plan is
    /// read from Queuey (<c>GET /billing/status</c> and <c>GET /billing/plans</c>); without one, or when that fails, Free's 7.
    /// </summary>
    public static async Task<(int Days, string From)> ForAsync(ResolvedConfig? config, CancellationToken cancellationToken = default)
    {
        if (config is null || (config.Login is null && string.IsNullOrWhiteSpace(config.ApiKey)) || string.IsNullOrWhiteSpace(config.LicensePublicId))
            return (FreeDays, $"{FreeDays} days, which every plan allows (Free's limit); log in for your plan's");

        try
        {
            using var http = CliHost.TestHandler is { } handler ? new HttpClient(handler, disposeHandler: false) : new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(5);
            Uri api = config.ResolvedApiBase();

            JsonElement status = await GetAsync(http, config, new Uri(api, "billing/status"), cancellationToken).ConfigureAwait(false);
            string? level = status.TryGetProperty("plan", out JsonElement plan) ? plan.GetString() : null;
            JsonElement plans = await GetAsync(http, config, new Uri(api, "billing/plans"), cancellationToken).ConfigureAwait(false);
            JsonElement? match = plans.ValueKind == JsonValueKind.Array
                ? plans.EnumerateArray().Cast<JsonElement?>().FirstOrDefault(p =>
                    p!.Value.TryGetProperty("level", out JsonElement l) && string.Equals(l.GetString(), level, StringComparison.OrdinalIgnoreCase))
                : null;
            if (level is null || match is null)
                return (FreeDays, $"{FreeDays} days: Queuey did not say the license's plan, and every plan allows {FreeDays}");

            // Null er Enterprise: forhandlet, uten fast tak.
            int? planDays = match.Value.TryGetProperty("retentionDays", out JsonElement d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : null;
            int days = Math.Max(1, Math.Min(MostDays, planDays ?? MostDays));
            string shown = TerminalText.Line(level);
            return (days, planDays is { } cap && cap <= MostDays
                ? $"{days} days, the most the license's {shown} plan keeps events"
                : $"{days} days, within the license's {shown} plan");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or QueueyException or InvalidOperationException)
        {
            return (FreeDays, $"{FreeDays} days: the license's plan could not be read, and every plan allows {FreeDays}");
        }
    }

    private static async Task<JsonElement> GetAsync(HttpClient http, ResolvedConfig config, Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (!string.IsNullOrWhiteSpace(config.ApiKey))
            request.Headers.TryAddWithoutValidation(QueueyHeaders.ApiKey, config.ApiKey);
        else if (config.Login is { } login)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await login.AccessTokenAsync(cancellationToken).ConfigureAwait(false));
        request.Headers.TryAddWithoutValidation(QueueyHeaders.LicensePublicId, config.LicensePublicId);

        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return document.RootElement.Clone();
    }

    /// <summary>
    /// What to add under Queuey's <c>retention_cap_exceeded</c>, so plan and apply say it is the plan's cap and what to do.
    /// Null for any other refusal.
    /// </summary>
    public static string? CapHint(string? errorCode)
        => errorCode == "retention_cap_exceeded"
            ? "This is the license's plan: it keeps events for a fixed number of days (Free: 7). Lower retentionDays to at most that, " +
              "or leave it out to take the plan's, or change the plan in the console."
            : null;
}
