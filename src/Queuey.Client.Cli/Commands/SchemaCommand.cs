using System;
using Queuey.Client.Waas;

namespace Queuey.Client.Cli;

/// <summary>
/// <c>queuey schema</c> — prints the JSON Schema for <c>queuey.deploy.json</c>: every field, the values
/// it accepts and what it does. Reads nothing and needs no credentials.
/// </summary>
internal static class SchemaCommand
{
    public static int Run(string[] args)
    {
        if (args.Length > 0 && args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage.Text);
            return ExitCodes.Success;
        }

        Console.Write(DeploymentFile.JsonSchema);
        return ExitCodes.Success;
    }
}
