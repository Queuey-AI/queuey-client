using System;
using Queuey.Client.Waas;

namespace Queuey.Client.Cli;

// Queuey F3.11 (Q1 i plan-approval-f311): en nøkkels apply uten plan mot prod får én release med advarselen
// would_require_approval før Queuey nekter den. Advarselen skal ikke drukne i resten av utskriften, så den står på stderr med
// hva som gjøres, i det Queuey svarer med den. Teksten er Queueys og går gjennom TerminalText.

/// <summary>Writes a warning Queuey answered with (<c>X-Queuey-Warning</c>) to stderr, with what to do about the known ones.</summary>
internal static class ServerWarningText
{
    /// <summary>The warning that a write or apply will need a configuration plan once Queuey enforces plans.</summary>
    internal const string WouldRequireApproval = "would_require_approval";

    public static void Write(string warning)
    {
        Console.Error.WriteLine($"Warning from Queuey: {TerminalText.Line(warning)}");
        if (ServerWarnings.CodeOf(warning) == WouldRequireApproval)
            Console.Error.WriteLine("  → Soon an API key changes this workspace only through a configuration plan: `queuey plan` "
                                    + "stores one in Queuey, and `queuey apply` makes it and applies it once the policy or a person lets it.");
    }
}
