using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Queuey.Client;

namespace Queuey.Client.Cli;

/// <summary>
/// The access token of one stored login, renewed when it has less than <see cref="Margin"/> left. A renewal runs under
/// <see cref="LoginStore.LockAsync"/> and reads the file again first, so two processes never spend the same refresh token:
/// the second finds the first one's new token and uses it.
/// </summary>
internal sealed class LoginTokens
{
    /// <summary>How much time a token must have left to be sent as it is.</summary>
    internal static readonly TimeSpan Margin = TimeSpan.FromSeconds(60);

    /// <summary>The clock, a seam for tests.</summary>
    internal static Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    private readonly string _path;
    private readonly Uri _apiBase;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LoginTokens(string path, Uri apiBase, StoredLogin login)
    {
        _path = path;
        _apiBase = apiBase;
        Login = login;
    }

    /// <summary>The login as last read or renewed.</summary>
    public StoredLogin Login { get; private set; }

    /// <summary>A token with at least <see cref="Margin"/> left, renewed first when needed.</summary>
    public async Task<string> AccessTokenAsync(CancellationToken cancellationToken)
    {
        if (Fresh(Login))
            return Login.AccessToken;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Fresh(Login))
                Login = await RenewAsync(_path, _apiBase, Login.License, cancellationToken).ConfigureAwait(false);
            return Login.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static bool Fresh(StoredLogin login)
        => !string.IsNullOrWhiteSpace(login.AccessToken) && login.AccessTokenExpiresAt - Now() > Margin;

    /// <summary>
    /// The stored login for <paramref name="license"/> on <paramref name="apiBase"/> with a fresh access token: the one
    /// another process stored meanwhile, or a new one for the refresh token, with the new refresh token stored in its place.
    /// A login Queuey no longer accepts is removed here, and the error says to log in again.
    /// </summary>
    internal static async Task<StoredLogin> RenewAsync(string path, Uri apiBase, string license, CancellationToken cancellationToken)
    {
        string host = LoginStore.HostKey(apiBase);
        using IDisposable held = await LoginStore.LockAsync(path, cancellationToken).ConfigureAwait(false);

        CredentialsFile file = LoginStore.Read(path);
        StoredLogin login = file.Find(host, license) ?? throw Ended(host, license, "it is no longer stored on this machine");
        if (Fresh(login))
            return login;

        if (string.IsNullOrWhiteSpace(login.RefreshToken))
        {
            Forget(path, file, login);
            throw Ended(host, license, "its access token expired, and Queuey gave no refresh token to renew it");
        }

        using var oauth = new OAuthClient(apiBase);
        OAuthEndpoints endpoints = await oauth.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        TokenAnswer answer = await oauth.RefreshAsync(endpoints, login.RefreshToken!, cancellationToken).ConfigureAwait(false);
        if (!answer.Succeeded)
        {
            // invalid_grant: tilkoblingen er koblet fra, har ligget ubrukt i 30 dager, er 90 dager gammel, eller et gammelt
            // refresh-token ble brukt på nytt. Den kommer ikke tilbake, så den fjernes her også.
            if (answer.Error is "invalid_grant" or "invalid_client" or "unauthorized_client")
            {
                Forget(path, file, login);
                throw Ended(host, license, "Queuey no longer accepts it: it was disconnected, went unused for too long, or reached its last day");
            }

            throw new QueueyException(
                $"{host} refused to renew the login for {license} ({answer.Error}){(answer.ErrorDescription is null ? "." : $": {TerminalText.Line(answer.ErrorDescription)}")}",
                errorCode: answer.Error);
        }

        try
        {
            Apply(login, answer);
        }
        catch (QueueyException ex) when (ex.ErrorCode == "login_scope_broader")
        {
            Forget(path, file, login);
            throw;
        }

        LoginStore.Write(path, file);
        return login;
    }

    /// <summary>The tokens and what came with them in <paramref name="answer"/>, onto <paramref name="login"/>.</summary>
    internal static void Apply(StoredLogin login, TokenAnswer answer)
    {
        // Et svar med bredere scope enn innloggingen har (operate for read), lagres aldri, heller ikke ved en fornyelse
        // (security-review av #68, K2). Ingenting er endret når dette kastes.
        if (ScopeOf(answer.Scope) is { } granted && !string.IsNullOrEmpty(login.Scope) && !WithinScope(granted, login.Scope))
            throw new QueueyException($"Queuey granted scope {granted} where the login has {login.Scope}, so it was not kept.",
                errorCode: "login_scope_broader")
            {
                SuggestedAction = "Disconnect it under Connected apps in the Queuey console, and run queuey login again.",
            };

        login.AccessToken = answer.AccessToken!;
        login.AccessTokenExpiresAt = Now() + TimeSpan.FromSeconds(answer.ExpiresIn is > 0 ? answer.ExpiresIn.Value : 3600);
        // Refresh-tokenet roterer: det nye lagres alltid. Uten et nytt i svaret gjelder det gamle fortsatt (RFC 6749 §6).
        login.RefreshToken = answer.RefreshToken ?? login.RefreshToken;
        login.Scope = ScopeOf(answer.Scope) ?? login.Scope;
        // Serveren styrer verdiene (security-review av #66, KAN 6): en ingress som ikke er https eller lokal http, og en person
        // som ikke er en kort tekst, lagres ikke.
        if (!login.IngressBaseFromFlag)
            login.IngressBase = SafeIngress(answer.IngressBase) ?? login.IngressBase;
        login.User = answer.User is { Length: <= 200 } user ? TerminalText.Line(user) : login.User;
    }

    /// <summary>
    /// <c>operate</c> or <c>read</c> from a granted scope list such as <c>operate offline_access</c>, or null when it names
    /// neither. The login is matched on this, so a server that adds a scope of its own does not make every run a new login.
    /// </summary>
    /// <summary>Whether <paramref name="granted"/> reaches no further than <paramref name="asked"/>: the same, or read for operate.</summary>
    internal static bool WithinScope(string granted, string asked)
        => granted == asked || (granted == "read" && asked == "operate");

    internal static string? ScopeOf(string? granted)
    {
        string[] scopes = (granted ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return scopes.Contains("operate") ? "operate" : scopes.Contains("read") ? "read" : null;
    }

    /// <summary>The ingress host Queuey gave, when it is https or http on this machine; null otherwise.</summary>
    internal static string? SafeIngress(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && OAuthClient.IsSafe(uri) ? value : null;

    private static void Forget(string path, CredentialsFile file, StoredLogin login)
    {
        file.Logins.Remove(login);
        LoginStore.Write(path, file);
    }

    internal static QueueyConfigurationException Ended(string host, string license, string why)
    {
        var ended = new QueueyConfigurationException($"The login to {host} for license {license} has ended: {why}.")
        {
            SuggestedAction = "Run `queuey login` to log in again.",
        };
        ended.Data[EndedMark] = true;
        return ended;
    }

    // Security-review av #66 (KAN 4): login fanget hver QueueyConfigurationException som «slutt», også tidsavbruddet på låsen, og
    // startet en ny device-flyt mens den gamle innloggingen fortsatt virket. Bare en innlogging som er borte for godt, er merket.
    // (QueueyConfigurationException er sealed i kjernen, så merket står i Data i stedet for i en egen type.)
    private const string EndedMark = "queuey.cli.loginEnded";

    /// <summary>Whether <paramref name="ex"/> says the login is gone for good, so a new one is the way on.</summary>
    internal static bool IsEnded(Exception ex) => ex.Data.Contains(EndedMark);
}

/// <summary>Finds the stored login a command connects with when it has no API key.</summary>
internal static class Logins
{
    /// <summary>
    /// <paramref name="config"/> with the login for its API host when it has no API key: the one for its license, or the
    /// only one there is when it names none. An API key that is set always wins, and nothing is read then.
    /// </summary>
    internal static ResolvedConfig Attach(ResolvedConfig config, Func<string, string?> env)
    {
        if (!string.IsNullOrWhiteSpace(config.ApiKey))
            return config;

        string path;
        try
        {
            path = LoginStore.PathOf(env);
        }
        catch (QueueyConfigurationException)
        {
            return config; // Ingen hjemmemappe og ingen QUEUEY_USER_CONFIG: ingen innlogging å finne.
        }

        if (!System.IO.File.Exists(path))
            return config;

        Uri apiBase = config.ResolvedApiBase();
        string host = LoginStore.HostKey(apiBase);
        StoredLogin[] logins = LoginStore.Read(path).For(host).ToArray();
        if (logins.Length == 0)
            return config;

        StoredLogin? login;
        if (config.LicensePublicId is { } license)
            login = logins.FirstOrDefault(l => string.Equals(l.License, license, StringComparison.Ordinal));
        else if (logins.Length == 1)
            login = logins[0];
        else
            throw new QueueyConfigurationException(
                $"You are logged in to {host} for several licenses ({string.Join(", ", logins.Select(l => l.License))}), and nothing names one.")
            {
                SuggestedAction = "Name the license with --license, QUEUEY_LICENSE or license in the profile.",
            };

        return login is null ? config : config.WithLogin(new LoginTokens(path, apiBase, login));
    }
}

/// <summary>What a license id looks like: <c>lic_</c> and an opaque id, no more than 64 characters in all.</summary>
internal static class LicenseIds
{
    internal static bool IsOne(string? value)
    {
        if (value is null || value.Length <= 4 || value.Length > 64 || !value.StartsWith("lic_", StringComparison.Ordinal))
            return false;
        foreach (char c in value)
        {
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-'))
                return false;
        }
        return true;
    }
}
