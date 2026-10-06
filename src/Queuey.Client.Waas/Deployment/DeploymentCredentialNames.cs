namespace Queuey.Client.Waas;

// Queuey F2.3-review (2026-10-06): `queuey credentials set` sjekket ikke navnet, så CLI-en kunne lage et navn dens egen
// deploy-fil avviser i ingress.signedRequest.credentialRef. CLI-en spør nå de samme reglene som fila (CredentialNameRules)
// gjennom denne klassen.
//
// F2.7 (2026-10-06): internal, ikke public. Den var public bare for at CLI-en skulle nå reglene, og en public const blir
// bakt inn i koden til den som kompilerer mot pakken, så en endret lengde ville ikke nådd dem. CLI-en ser den nå gjennom
// InternalsVisibleTo (Queuey.Client.Waas.csproj).

/// <summary>
/// The rules for the name of a credential a deployment file refers to: the shape every such name has, and the guess that
/// keeps a secret pasted where the name goes out of it. The same rules the deployment file and Queuey apply.
/// </summary>
internal static class DeploymentCredentialNames
{
    /// <summary>The longest a name can be.</summary>
    internal const int MaxLength = CredentialNameRules.MaxLength;

    /// <summary>The shape of a name as a regular expression, as the deployment file's schema publishes it.</summary>
    internal const string Pattern = CredentialNameRules.PendingNamePattern;

    /// <summary>
    /// True when <paramref name="name"/> starts with a letter or digit and holds only letters, digits and
    /// <c>. _ : @ / -</c>, at most <see cref="MaxLength"/> characters: safe in a shell command and in text.
    /// </summary>
    internal static bool FitsShape(string? name) => CredentialNameRules.FitsPendingShape(name);

    /// <summary>
    /// True when <paramref name="name"/> starts like a provider's or Queuey's secret (<c>whsec_</c>, <c>sk_live_</c>,
    /// <c>qak_</c> …) or looks random (hex, a UUID, base64): a secret, not a name.
    /// </summary>
    internal static bool LooksLikeASecret(string name) => CredentialNameRules.LooksLikeASecret(name);
}
