using System;
using System.Linq;
using System.Text.Json;
using Queuey.Client;
using Queuey.Client.Waas;

namespace Queuey.Client.Cli;

/// <summary>
/// The workspace a deployment command acts on — one rule for <c>apply</c> (with <c>--check</c> and
/// <c>--dry-run</c>), <c>plan</c> and <c>verify</c>, so a plan and a verification reach the workspace
/// the apply writes to.
/// </summary>
/// <remarks>
/// The deployment file's <c>tenant</c> when it names one, otherwise the configured tenant: the flag,
/// then <c>QUEUEY_TENANT</c>, then <c>queuey.json</c>. When <c>--tenant</c> or <c>QUEUEY_TENANT</c>
/// names another workspace than the file, the command fails and names both.
/// </remarks>
internal static class DeploymentTenant
{
    /// <summary>
    /// <paramref name="config"/> pointed at the workspace the deployment file names, after checking
    /// that an explicit <c>--tenant</c> or <c>QUEUEY_TENANT</c> does not name another one.
    /// </summary>
    /// <param name="config">The connection config as the flags, environment and queuey.json resolve it.</param>
    /// <param name="args">The command's arguments, for <c>--tenant</c>.</param>
    /// <param name="getEnv">The environment, for <c>QUEUEY_TENANT</c>.</param>
    /// <param name="fileTenant">The file's tenant with its <c>${VAR}</c> expanded, or null when it names none.</param>
    /// <param name="filePath">The file, for the error message.</param>
    public static ResolvedConfig Resolve(
        ResolvedConfig config, ArgMap args, Func<string, string?> getEnv, string? fileTenant, string filePath)
    {
        EnsureNoConflict(args, getEnv, fileTenant, filePath);
        return string.IsNullOrWhiteSpace(fileTenant) ? config : config.WithTenant(fileTenant!.Trim());
    }

    /// <summary>
    /// Throws when <c>--tenant</c> or <c>QUEUEY_TENANT</c> names another workspace than the file. For a
    /// command that needs no connection, like <c>apply --dry-run</c>: it fails where the apply would.
    /// </summary>
    public static void EnsureNoConflict(ArgMap args, Func<string, string?> getEnv, string? fileTenant, string filePath)
    {
        // Før 2026-09-24 vant fila over flagget i apply, mens flagget vant over fila i verify. Da kunne
        // `verify --tenant X` bevise levering i et annet workspace enn det `apply --tenant X` skrev til.
        // Når de er uenige, kan hvem som helst av dem være feilen, så ingen av dem velges.
        if (string.IsNullOrWhiteSpace(fileTenant))
            return;

        (string? explicitTenant, string source) = Explicit(args, getEnv);
        if (explicitTenant is null || string.Equals(explicitTenant, fileTenant!.Trim(), StringComparison.Ordinal))
            return;

        throw new QueueyConfigurationException(
            $"{filePath} names workspace {fileTenant!.Trim()}, but {source} names {explicitTenant}. " +
            "A deploy and its verification have to reach the same workspace, so neither is picked: remove one of " +
            "them, or make them name the same workspace.");
    }

    /// <summary>
    /// The tenant a deployment file names, with its <c>${VAR}</c> expanded, read without the rest of the
    /// file. For <c>verify</c>, which needs nothing else from it. Null when the file names none.
    /// </summary>
    public static string? ReadFromFile(string json, string path)
    {
        // verify leser bare tenant (review 2026-10-05): et felt apply avviser et annet sted i fila, som forsøkene,
        // stoppet en verifisering som ikke bruker det. JSON som ikke kan leses, feiler fortsatt, med fila navngitt.
        if (string.IsNullOrWhiteSpace(json))
            return null;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException ex)
        {
            throw new QueueyConfigurationException($"{path}: Could not parse the deployment file: {ex.Message}");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new QueueyConfigurationException($"{path}: A deployment file is a JSON object, like {{ \"tenant\": \"ten_…\", \"queues\": {{}} }}.");

            // Store og små bokstaver teller ikke, som når apply leser fila. To tenant-er avvises, som apply avviser dem:
            // parseren tar den siste, og den første tok verify, så de traff hvert sitt workspace (re-review 2026-10-05).
            JsonProperty[] tenants = document.RootElement.EnumerateObject()
                .Where(p => string.Equals(p.Name, "tenant", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (tenants.Length > 1)
                throw new QueueyConfigurationException(
                    $"{path}: The deployment file names the tenant {tenants.Length} times ({string.Join(", ", tenants.Select(t => t.Name))}), and only the last would count. Keep one.");
            if (tenants.Length == 0)
                return null;

            return tenants[0].Value.ValueKind switch
            {
                JsonValueKind.String => DeploymentVariables.Expand(tenants[0].Value.GetString(), null, "tenant"),
                JsonValueKind.Null => null,
                _ => throw new QueueyConfigurationException($"{path}: tenant is a workspace id in quotes, like \"ten_…\"."),
            };
        }
    }

    private static (string? Tenant, string Source) Explicit(ArgMap args, Func<string, string?> getEnv)
    {
        if (args.Get("tenant") is { } flag && !string.IsNullOrWhiteSpace(flag))
            return (flag.Trim(), "--tenant");
        if (getEnv("QUEUEY_TENANT") is { } env && !string.IsNullOrWhiteSpace(env))
            return (env.Trim(), "QUEUEY_TENANT");
        return (null, string.Empty);
    }
}
