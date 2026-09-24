using System;
using Queuey.Client.Waas;

namespace Queuey.Client.Cli;

/// <summary>
/// <c>queuey schema</c> — prints the JSON Schema for <c>queuey.deploy.json</c>: every field, the values
/// it accepts and what it does. Reads nothing and needs no credentials.
/// </summary>
internal static class SchemaCommand
{
    internal static readonly CommandOptions Options = new("schema");

    public static int Run(string[] args)
    {
        if (args.Length > 0 && args[0] == "help")
        {
            Console.WriteLine(Usage.Text);
            return ExitCodes.Success;
        }

        if (!Options.TryParse(args, out ArgMap map, out int failure)) return failure;
        if (map.Has("help") || map.Has("h")) { Console.WriteLine(Usage.Text); return ExitCodes.Success; }

        Console.Write(DeploymentFile.JsonSchema);
        return ExitCodes.Success;
    }
}
