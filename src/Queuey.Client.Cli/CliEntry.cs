using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Queuey.Client;
using Queuey.Client.Waas;

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
        // `queuey --json apply` betyr det samme som `queuey apply --json` (review 2026-10-05). Før ble --json lest som
        // kommandoen, og svaret var «Unknown command» som prosa.
        args = MoveLeadingJsonAfterTheCommand(args);
        // Queuey #514: apply --check sammenligner en redigert URL med reglene CLI-en har kopiert (Waas bygger også for netstandard).
        DeploymentDrift.RedactUrl ??= TargetUrlRedaction.Redact;

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

        // Hver advarsel Queuey svarer med (X-Queuey-Warning), som would_require_approval (Queuey F3.11), står på stderr i det den
        // kommer, i alle kommandoer. stdout er kommandoens, også med --json.
        using IDisposable warnings = QueueyControlPlaneClient.CollectWarnings(new ServerWarnings(ServerWarningText.Write));

        try
        {
            return command switch
            {
                "advise" => await AdviseCommand.RunAsync(rest),
                "login" => await LoginCommand.RunAsync(rest),
                "logout" => await LogoutCommand.RunAsync(rest),
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
                "events" => await EventsCommand.RunAsync(rest),
                "create-tenant" => await CreateTenantCommand.RunAsync(rest),
                "create-queue" => await CreateQueueCommand.RunAsync(rest),
                "metrics" => await MetricsCommand.RunAsync(rest),
                "issues" => await IssuesCommand.RunAsync(rest),
                "listen" => await ListenCommand.RunAsync(rest),
                "replay" => await ReplayCommand.RunAsync(rest),
                "diagnose" => await DiagnoseCommand.RunAsync(rest),
                "resume" => await ResumeCommand.RunAsync(rest),
                "unlock" => await UnlockCommand.RunAsync(rest),
                "edge" => await EdgeCommand.RunAsync(rest),
                _ => Unknown(command, json),
            };
        }
        catch (CliUsageException ex)
        {
            return CliErrors.Write(json, ex.Code, ex.Message, ex.Action, status: null, ExitCodes.Usage);
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
        catch (CliFileException ex)
        {
            // En fil som ikke kan leses (manglende tilgang), ga exit 134 og stack trace (review 2026-10-05). Bare filene
            // kommandolinjen navngir havner her (re-review samme dag): en annen IOException er ikke en fil brukeren kan rette.
            return CliErrors.Write(json, ex.Code, ex.Message, ex.Action, status: null, ExitCodes.Configuration, "Error");
        }
        catch (Exception ex) when (json)
        {
            // Med --json er hver feil JSON, også en feil i CLI-en selv (review 2026-10-05). Uten --json står stack trace-en,
            // som er det som trengs for å rette den.
            return CliErrors.Write(json: true, "internal_error", $"{ex.GetType().Name}: {ex.Message}",
                "This is a bug in the queuey CLI. Run the command again without --json for the stack trace, and report it.",
                status: null, ExitCodes.RuntimeError);
        }
    }

    /// <summary>A leading <c>--json</c> (or <c>--json=…</c>), before the command, moved to just after it.</summary>
    private static string[] MoveLeadingJsonAfterTheCommand(string[] args)
    {
        int leading = 0;
        while (leading < args.Length && IsJsonSwitch(args[leading]))
            leading++;

        if (leading == 0 || leading == args.Length)
            return args;

        return new[] { args[leading] }.Concat(args.Take(leading)).Concat(args.Skip(leading + 1)).ToArray();

        static bool IsJsonSwitch(string arg) => arg == "--json" || arg.StartsWith("--json=", StringComparison.Ordinal);
    }

    private static int Unknown(string command, bool json)
    {
        if (json)
            return CliErrors.Write(json: true, "unknown_command", $"Unknown command '{CliErrors.Shown(command)}'.",
                "Run `queuey --help` for the commands.", status: null, ExitCodes.Usage);

        Console.Error.WriteLine($"Unknown command '{CliErrors.Shown(command)}'.");
        Console.Error.WriteLine();
        Console.Error.WriteLine(Usage.Text);
        return ExitCodes.Usage;
    }
}
