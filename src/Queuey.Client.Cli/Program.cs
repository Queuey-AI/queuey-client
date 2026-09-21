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
    Console.Error.WriteLine($"Config error: {ex.Message}");
    return ExitCodes.Configuration;
}
catch (QueueyException ex)
{
    Console.Error.WriteLine($"Queuey error: {ex.Message}");
    return ExitCodes.RuntimeError;
}
catch (HttpRequestException ex)
{
    Console.Error.WriteLine($"Could not reach Queuey: {ex.Message}");
    return ExitCodes.RuntimeError;
}
catch (TaskCanceledException)
{
    Console.Error.WriteLine("The request timed out.");
    return ExitCodes.RuntimeError;
}

static int Unknown(string command)
{
    Console.Error.WriteLine($"Unknown command '{command}'.");
    Console.Error.WriteLine();
    Console.Error.WriteLine(Usage.Text);
    return ExitCodes.Usage;
}
