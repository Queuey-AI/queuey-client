using System;
using System.Net.Http;
using System.Threading.Tasks;
using Queuey.Client;

namespace Queuey.Client.Cli;

/// <summary>
/// The whole command line, from the first argument to the exit code: picks the command and turns
/// what escapes it into an error in the form the caller asked for. <c>Program</c> only calls this,
/// so tests run the same path a shell does.
/// </summary>
internal static class CliEntry
{
    public static async Task<int> RunAsync(string[] args)
    {
        string command = args.Length > 0 ? args[0] : string.Empty;
        string[] rest = args.Length > 1 ? args[1..] : Array.Empty<string>();

        if (command is "" or "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage.Text);
            return command.Length == 0 ? ExitCodes.Usage : ExitCodes.Success;
        }

        if (command is "--version" or "version")
        {
            Console.WriteLine($"queuey {CliVersion.Current}");
            return ExitCodes.Success;
        }

        // Samme lesing av --json som kommandoene gjør, så --json=true gir JSON også her (2026-09-24).
        bool json = CliErrors.WantsJson(rest);

        try
        {
            return command switch
            {
                "advise" => await AdviseCommand.RunAsync(rest),
                "whoami" => WhoAmICommand.Run(rest),
                "sync" => await SyncCommand.RunAsync(rest),
                "queue" => await QueueCommand.RunAsync(rest),
                "apply" => await ApplyCommand.RunAsync(rest),
                "plan" => await PlanCommand.RunAsync(rest),
                "verify" => await VerifyCommand.RunAsync(rest),
                "schema" => SchemaCommand.Run(rest),
                "pull" => await PullCommand.RunAsync(rest),
                "keys" => await KeysCommand.RunAsync(rest),
                "credentials" => await CredentialsCommand.RunAsync(rest),
                "publish" => await PublishCommand.RunAsync(rest),
                "create-tenant" => await CreateTenantCommand.RunAsync(rest),
                "create-queue" => await CreateQueueCommand.RunAsync(rest),
                "metrics" => await MetricsCommand.RunAsync(rest),
                "issues" => await IssuesCommand.RunAsync(rest),
                "listen" => await ListenCommand.RunAsync(rest),
                "replay" => await ReplayCommand.RunAsync(rest),
                "edge" => await EdgeCommand.RunAsync(rest),
                _ => Unknown(command, json),
            };
        }
        catch (QueueyConfigurationException ex)
        {
            return CliErrors.Write(json, "config_error", ex.Message, ex.SuggestedAction, status: null, ExitCodes.Configuration, "Config error");
        }
        catch (QueueyException ex)
        {
            return CliErrors.Write(json, ex.ErrorCode ?? "queuey_error", ex.Message, ex.SuggestedAction, ex.StatusCode, ExitCodes.RuntimeError, "Queuey error");
        }
        catch (HttpRequestException ex)
        {
            return CliErrors.Write(json, "unreachable", $"Could not reach Queuey: {ex.Message}",
                "Check --api-base / --ingress-base (QUEUEY_API_BASE, QUEUEY_INGRESS_BASE) and the network.", status: null,
                ExitCodes.RuntimeError, "Error");
        }
        catch (TaskCanceledException)
        {
            return CliErrors.Write(json, "timeout", "The request timed out.", action: null, status: null, ExitCodes.RuntimeError, "Error");
        }
    }

    private static int Unknown(string command, bool json)
    {
        if (json)
            return CliErrors.Write(json: true, "unknown_command", $"Unknown command '{command}'.",
                "Run `queuey --help` for the commands.", status: null, ExitCodes.Usage);

        Console.Error.WriteLine($"Unknown command '{command}'.");
        Console.Error.WriteLine();
        Console.Error.WriteLine(Usage.Text);
        return ExitCodes.Usage;
    }
}
