namespace Queuey.Client.Waas;

/// <summary>
/// The way out when a deployment file names a lower environment than its workspace has: an API key never lowers it, but it
/// sets one when it creates a workspace. So the way out is a new workspace, made from the command line.
/// </summary>
// Gullflyten 2026-10-09: plan svarte environment_lowering_needs_a_person med «ask a person in the console», og agenten kom rundt
// det med POST /tenants og "environment":"dev" direkte. Det API-et tillater, sier CLI-en nå selv.
internal static class EnvironmentWayOut
{
    /// <summary>The code Queuey refuses a lowering with (403).</summary>
    public const string LoweringCode = "environment_lowering_needs_a_person";

    /// <summary>What to do instead, for a workspace that should be <paramref name="environment"/>.</summary>
    public static string For(string environment)
    {
        string wanted = environment.Trim().ToLowerInvariant();
        return $"An API key sets a workspace's environment only when it creates one. Make a workspace marked {wanted} with "
               + $"`queuey create-tenant --name <name> --environment {wanted}`, and name it with --tenant, in the file's tenant or "
               + $"in the profile. With no workspace named anywhere, `queuey apply` creates one marked {wanted} itself.";
    }

    /// <summary>
    /// <paramref name="error"/> with <see cref="For"/> as its action when it is Queuey's refusal to lower the environment, else
    /// <paramref name="error"/> as it is. The message stays Queuey's.
    /// </summary>
    public static QueueyException Rewrite(QueueyException error, string? environment)
        => error.ErrorCode == LoweringCode && !string.IsNullOrWhiteSpace(environment)
            ? new QueueyForbiddenException(error.Message, LoweringCode) { SuggestedAction = For(environment!) }
            : error;
}
