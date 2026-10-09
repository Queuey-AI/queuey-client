namespace Queuey.Client.Cli;

/// <summary>Process exit codes. Stable contract for CI pipelines.</summary>
internal static class ExitCodes
{
    public const int Success = 0;
    public const int RuntimeError = 1;   // sync had failures / publish rejected / API error
    public const int Usage = 2;          // bad arguments / unknown command / an option the command does not take
    public const int Configuration = 3;  // missing/invalid credentials or host config
    public const int AssemblyLoad = 4;   // could not load the target assembly
    public const int PendingApproval = 5; // waits for a person: a plan's approval in Queuey's inbox (F3.11), or a login code (queuey login)
}
