using System;
using Queuey.Client;

namespace Queuey.Client.Cli;

/// <summary>
/// The workspace a deployment command acts on — one rule for <c>apply</c> (with <c>--check</c> and
/// <c>--dry-run</c>) and <c>verify</c>, so a verification reaches the workspace the apply wrote to.
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

    private static (string? Tenant, string Source) Explicit(ArgMap args, Func<string, string?> getEnv)
    {
        if (args.Get("tenant") is { } flag && !string.IsNullOrWhiteSpace(flag))
            return (flag.Trim(), "--tenant");
        if (getEnv("QUEUEY_TENANT") is { } env && !string.IsNullOrWhiteSpace(env))
            return (env.Trim(), "QUEUEY_TENANT");
        return (null, string.Empty);
    }
}
