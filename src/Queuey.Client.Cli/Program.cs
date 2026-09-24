using System;
using System.Net.Http;
using System.Threading.Tasks;
using Queuey.Client;
using Queuey.Client.Cli;

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

try
{
    return command switch
    {
        "advise" => await AdviseCommand.RunAsync(rest),
        "whoami" => WhoAmICommand.Run(rest),
        "sync" => await SyncCommand.RunAsync(rest),
        "queue" => await QueueCommand.RunAsync(rest),
        "apply" => await ApplyCommand.RunAsync(rest),
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
        _ => Unknown(command),
    };
}
catch (QueueyConfigurationException ex)
{
    return CliErrors.Write(rest, "config_error", ex.Message, ex.SuggestedAction, status: null, ExitCodes.Configuration, "Config error");
}
catch (QueueyException ex)
{
    return CliErrors.Write(rest, ex.ErrorCode ?? "queuey_error", ex.Message, ex.SuggestedAction, ex.StatusCode, ExitCodes.RuntimeError, "Queuey error");
}
catch (HttpRequestException ex)
{
    return CliErrors.Write(rest, "unreachable", $"Could not reach Queuey: {ex.Message}",
        "Check --api-base / --ingress-base (QUEUEY_API_BASE, QUEUEY_INGRESS_BASE) and the network.", status: null,
        ExitCodes.RuntimeError, "Error");
}
catch (TaskCanceledException)
{
    return CliErrors.Write(rest, "timeout", "The request timed out.", action: null, status: null, ExitCodes.RuntimeError, "Error");
}

static int Unknown(string command)
{
    Console.Error.WriteLine($"Unknown command '{command}'.");
    Console.Error.WriteLine();
    Console.Error.WriteLine(Usage.Text);
    return ExitCodes.Usage;
}
