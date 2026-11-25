using System;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Queuey.Server.Hosting;

/// <summary>
/// The installation's license as the server judges it, from <c>Queuey.Server.dll license --format json</c>. The fields are
/// the server's; a field it leaves out is null here.
/// </summary>
internal sealed record InstallationLicense(
    string? Edition,
    string Plan,
    string State,
    string? Reason,
    string? LicenseId,
    string? Customer,
    string? Environment,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? GraceEndsAt,
    DateTimeOffset? UnlimitedUntil,
    long? ReceivedLimit,
    long? ReceivedLastDays,
    int? VolumeWindowDays,
    Guid? InstallationId,
    DateTimeOffset? InstalledAt,
    string? Version,
    string? ActivationCode);

/// <summary>
/// What the server answered about its license: the object it wrote, read both as it is and as an
/// <see cref="InstallationLicense"/>, or, when there is none, why.
/// </summary>
/// <param name="Json">The server's object, unchanged; null when there is none.</param>
/// <param name="License">The same object read; null when there is none.</param>
/// <param name="Unavailable">Why there is no license to show, as the rest of a sentence after "License: "; null when there is one.</param>
internal sealed record LicenseAnswer(JsonElement? Json, InstallationLicense? License, string? Unavailable)
{
    public static LicenseAnswer Missing(string why) => new(null, null, why);
}

/// <summary>Asks a running installation's server for its license, the way an operator would with <c>docker compose exec</c>.</summary>
internal static class ServerLicense
{
    /// <summary>The command, after <c>docker compose</c>, run in the installation folder.</summary>
    public static readonly string[] Command = { "exec", "-T", "server", "dotnet", "Queuey.Server.dll", "license", "--format", "json" };

    /// <summary>How long the server gets to answer. A seam: a test does not wait that long.</summary>
    internal static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The license of the installation in <paramref name="folder"/>, whose server runs. Never throws for what the server
    /// answers or fails to answer: that becomes <see cref="LicenseAnswer.Unavailable"/>. Only <paramref name="ct"/> cancels.
    /// </summary>
    public static async Task<LicenseAnswer> ReadAsync(ServerFolder folder, CancellationToken ct)
    {
        // Lisensen er tillegg til status, aldri en grunn til at status feiler (steg 1.3). Kommandoen bygger hele
        // serveren for å lese databasen, så den får en egen frist i stedet for å la status henge.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Timeout);
        DockerResult result;
        try
        {
            result = await ServerDocker.ComposeAsync(folder.Path, deadline.Token, Command);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return LicenseAnswer.Missing(string.Create(CultureInfo.InvariantCulture,
                $"not read: the server did not answer within {Timeout.TotalSeconds:0.#} seconds."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return LicenseAnswer.Missing($"not read: docker compose exec failed. {Text.Line(ex.Message)}");
        }

        // En server fra før lisenskommandoen svarer «Unknown command 'license'» med 2; 2 er også et valg den ikke kjenner.
        if (result.ExitCode == 2)
            return LicenseAnswer.Missing("not available from this server version.");
        if (result.ExitCode != 0)
            return LicenseAnswer.Missing(ServerDocker.Detail(result) is { } detail
                ? $"not read. {detail}"
                : $"not read: the license command ended with {result.ExitCode}.");
        return Parse(result.Stdout);
    }

    /// <summary>The server's stdout read as a license, or why it is not one.</summary>
    public static LicenseAnswer Parse(string stdout)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(stdout);
            JsonElement json = document.RootElement.Clone();
            if (json.ValueKind == JsonValueKind.Object
                && json.Deserialize<InstallationLicense>(Web) is { Plan.Length: > 0, State.Length: > 0 } license)
                return new LicenseAnswer(json, license, null);
        }
        catch (JsonException)
        {
        }
        return LicenseAnswer.Missing("not read: the server's answer is not a license.");
    }
}
