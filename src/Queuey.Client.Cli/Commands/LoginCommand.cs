using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Queuey.Client;

namespace Queuey.Client.Cli;

// queuey login (PR 4 i login-plan.md, vedtatt 2026-10-09): device-flyten (RFC 8628), som `stripe login`. En agent kjører ofte
// et annet sted enn nettleseren, og ser ofte utdata først når kommandoen er ferdig. Derfor venter `queuey login` bare i en
// terminal eller med --wait. Ellers skriver den lenken og avslutter med 5, «venter på en person», og koden lagres, så neste
// `queuey login` fortsetter der den slapp.

/// <summary>
/// <c>queuey login</c>: logs in with the device flow. Prints a link and a code, opens the browser in a terminal, and waits
/// for a person to approve it in the Queuey console. Without a terminal it exits 5 with the link instead, unless
/// <c>--wait</c>; the code is kept, so running it again picks it up. With <c>--profile</c> the profile gets the license, the
/// hosts and the workspace for its environment.
/// </summary>
internal static class LoginCommand
{
    internal static readonly CommandOptions Options = new(
        "login", flags: new[] { "json", "wait", "no-browser" }, values: new[] { "profile", "scope" });

    internal static readonly string[] Scopes = { "operate", "read" };

    /// <summary>Whether a person is at a terminal. A seam: the tests are never at one.</summary>
    internal static Func<bool> IsTerminal { get; set; } = () => !Console.IsOutputRedirected && !Console.IsInputRedirected;

    /// <summary>Opens a link in the browser and says whether it could. A seam, so a test never starts one.</summary>
    internal static Func<Uri, bool> OpenBrowser { get; set; } = DefaultOpenBrowser;

    /// <summary>The wait between polls. A seam, so a test does not sleep.</summary>
    internal static Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    public static async Task<int> RunAsync(string[] args)
    {
        if (!Options.TryParse(args, out ArgMap map, out int failure)) return failure;
        if (map.Has("help") || map.Has("h")) { Console.WriteLine(Usage.Text); return ExitCodes.Success; }

        bool json = map.Has("json");
        string scope = (map.Get("scope") ?? "operate").Trim().ToLowerInvariant();
        if (!Scopes.Contains(scope))
            return CliErrors.Usage(map, "invalid_value", $"--scope takes operate or read; got '{CliErrors.Shown(scope)}'.",
                "operate (the default) lets the login change what the person may change; read only looks.");

        // Blindtesten 2026-10-09 (funn 6): ingress-verten Queuey ga, var lokalt en gammel tunnel. --ingress-base overstyrer den,
        // også for innloggingen, og sjekkes før noe sendes.
        if (Clean(map.Get("ingress-base")) is { } ingressFlag
            && !(Uri.TryCreate(ingressFlag, UriKind.Absolute, out Uri? ingressUri) && (ingressUri.Scheme == Uri.UriSchemeHttp || ingressUri.Scheme == Uri.UriSchemeHttps)))
            return CliErrors.Usage(map, "invalid_value", $"--ingress-base takes an absolute http or https URL; got '{CliErrors.Shown(ingressFlag)}'.",
                "Such as http://localhost:5084 for a Queuey on this machine.");

        string? profile = CliHost.Profile(map);
        (ConnectionProfile? existing, Uri apiBase, string? license) = Connection(map, profile);
        string host = LoginStore.HostKey(apiBase);
        string path = LoginStore.PathOf(CliHost.Env);
        var run = new LoginRun(map, json, scope, profile, existing, apiBase, host, path);

        DateTimeOffset started = LoginTokens.Now();
        using var oauth = new OAuthClient(apiBase);

        // Er tilkoblingen alt gyldig, sies det, og kommandoen avslutter med 0: en agent kan kjøre den uten å sjekke først.
        if (await ValidLoginAsync(run, oauth, license) is { } valid)
            return await run.FinishAsync(oauth, valid.Login, valid.Workspaces, already: true);

        OAuthEndpoints endpoints = await oauth.DiscoverAsync(CancellationToken.None);
        (PendingLogin? waiting, bool resumed, StoredLogin? meanwhile) = await PendingOrNewAsync(run, oauth, endpoints, started);
        if (meanwhile is not null || waiting is null)
            return await run.FinishAsync(oauth, meanwhile!, workspaces: null, already: false);
        PendingLogin pending = waiting;

        bool interactive = !json && IsTerminal() && WorkspaceCreation.DetectedCi(CliHost.Env) is null;
        bool wait = map.Has("wait") || interactive;

        // En kode som alt venter, spørres én gang først: har personen godkjent siden sist, er innloggingen ferdig nå.
        if (resumed)
        {
            PollOutcome first = await PollAsync(run, oauth, endpoints, pending);
            if (first.Login is { } done)
                return await run.FinishAsync(oauth, done, workspaces: null, already: false);
            if (first.Failed is { } failed)
                return failed;
        }

        run.ShowLink(pending, waiting: wait);
        if (interactive && !map.Has("no-browser") && SafeLink(pending.Link) is { } link && OpenBrowser(link))
            Console.Error.WriteLine("Opened the link in your browser.");

        if (!wait)
            return ExitCodes.PendingApproval;

        if (!json)
            Console.Error.WriteLine("Waiting for approval…");
        while (true)
        {
            PollOutcome outcome = await PollAsync(run, oauth, endpoints, pending);
            if (outcome.Login is { } done)
                return await run.FinishAsync(oauth, done, workspaces: null, already: false);
            if (outcome.Failed is { } failed)
                return failed;
        }
    }

    /// <summary>
    /// The stored login for this host, scope and license when Queuey still takes it: renewed when its token has expired, and
    /// checked by listing the workspaces it reaches. One Queuey no longer takes is removed, and null is returned.
    /// </summary>
    private static async Task<(StoredLogin Login, IReadOnlyList<LoginWorkspace> Workspaces)?> ValidLoginAsync(
        LoginRun run, OAuthClient oauth, string? license)
    {
        StoredLogin[] candidates = LoginStore.Read(run.Path).For(run.Host)
            .Where(l => l.Scope == run.Scope && (license is null || l.License == license)).ToArray();
        if (candidates.Length != 1)
            return null; // Ingen, eller flere uten en lisens som skiller dem: en ny innlogging lar personen velge.

        var tokens = new LoginTokens(run.Path, run.ApiBase, candidates[0]);
        string token;
        try
        {
            token = await tokens.AccessTokenAsync(CancellationToken.None);
        }
        catch (QueueyConfigurationException ex) when (LoginTokens.IsEnded(ex))
        {
            return null; // Tilkoblingen er slutt og fjernet; en ny innlogging følger.
        }

        IReadOnlyList<LoginWorkspace>? workspaces = await oauth.WorkspacesAsync(token, tokens.Login.License, CancellationToken.None);
        if (workspaces is not null)
            return (tokens.Login, workspaces);

        await ForgetAsync(run.Path, run.Host, tokens.Login.License);
        return null;
    }

    /// <summary>
    /// The device code waiting for this host and scope, or a new one, stored so the next run can pick it up. Or, when another
    /// queuey login finished one for this host and scope while this run waited for the lock, that login.
    /// </summary>
    private static async Task<(PendingLogin? Pending, bool Resumed, StoredLogin? Meanwhile)> PendingOrNewAsync(
        LoginRun run, OAuthClient oauth, OAuthEndpoints endpoints, DateTimeOffset started)
    {
        using (await LoginStore.LockAsync(run.Path, CancellationToken.None))
        {
            CredentialsFile file = LoginStore.Read(run.Path);
            DateTimeOffset now = LoginTokens.Now();

            // To samtidige `queuey login` (security-review av #66, BØR 1): den andre ventet på låsen mens den første løste inn koden.
            // Den skal bruke den innloggingen, ikke be om en ny kode.
            if (FinishedMeanwhile(file, run.Host, run.Scope, started) is { } meanwhile)
                return (null, false, meanwhile);

            int before = file.Pending.Count;
            file.Pending.RemoveAll(p => p.ExpiresAt <= now);
            PendingLogin? waiting = file.Pending.FirstOrDefault(p => p.ApiBase == run.Host && p.Scope == run.Scope);
            if (waiting is not null)
            {
                if (file.Pending.Count != before)
                    LoginStore.Write(run.Path, file);
                return (waiting, true, null);
            }

            DeviceAuthorization device = await oauth.StartDeviceAsync(endpoints, run.Scope, CancellationToken.None);
            var pending = new PendingLogin
            {
                ApiBase = run.Host,
                Scope = run.Scope,
                DeviceCode = device.DeviceCode,
                UserCode = device.UserCode,
                VerificationUri = device.VerificationUri,
                VerificationUriComplete = device.VerificationUriComplete,
                Interval = device.Interval,
                CreatedAt = now,
                ExpiresAt = now + TimeSpan.FromSeconds(device.ExpiresIn),
            };
            file.Pending.Add(pending);
            LoginStore.Write(run.Path, file);
            return (pending, false, null);
        }
    }

    private sealed record PollOutcome(StoredLogin? Login, int? Failed);

    /// <summary>
    /// The newest login for <paramref name="host"/> stored at or after <paramref name="since"/> with no more than the
    /// <paramref name="asked"/> scope: the one another queuey login finished meanwhile. One with the same scope or a narrower
    /// one (read for operate) counts; a broader one (operate for read) never does. Null when there is none.
    /// </summary>
    // Security-review av #66, runde 2: en innlogging Queuey ga et smalere scope, ble meldt som «ended without a login». Den gjelder.
    // Security-review KAN 2 (2026-10-09): et bredere scope enn det som ble bedt om, tas aldri i bruk.
    internal static StoredLogin? FinishedMeanwhile(CredentialsFile file, string host, string asked, DateTimeOffset since)
        => file.For(host).Where(l => l.LoggedInAt >= since && LoginTokens.WithinScope(l.Scope, asked))
            .OrderByDescending(l => l.LoggedInAt).FirstOrDefault();

    /// <summary>
    /// Asks once whether the code is approved, no sooner than the interval after the last time any process asked, or after it
    /// was made (RFC 8628 §3.5). Approved: the login, stored, and the code gone. Declined or expired: the error, written, and
    /// the code gone. Still waiting: neither.
    /// </summary>
    // Security-review av #66 (BØR 1): to prosesser som spurte om samme kode, løste den inn to ganger, og OpenIddict trekker
    // tilbake hele autorisasjonen når en device-kode brukes på nytt. Nå spørres det under låsen, etter at fila er lest på nytt: er
    // koden borte, har en annen prosess løst den inn, og innloggingen den lagret, brukes.
    private static async Task<PollOutcome> PollAsync(LoginRun run, OAuthClient oauth, OAuthEndpoints endpoints, PendingLogin pending)
    {
        DateTimeOffset next = (pending.LastPolledAt ?? pending.CreatedAt) + TimeSpan.FromSeconds(pending.Interval);
        DateTimeOffset now = LoginTokens.Now();
        if (next > now && now < pending.ExpiresAt)
            await Delay(next - now, CancellationToken.None);

        string? ended = null, description = null;
        StoredLogin? login = null, replaced = null;
        using (await LoginStore.LockAsync(run.Path, CancellationToken.None))
        {
            CredentialsFile file = LoginStore.Read(run.Path);
            PendingLogin? stored = file.Pending.FirstOrDefault(p => p.DeviceCode == pending.DeviceCode);
            if (stored is null)
            {
                StoredLogin? theirs = FinishedMeanwhile(file, run.Host, run.Scope, pending.CreatedAt);
                return theirs is not null
                    ? new PollOutcome(theirs, null)
                    : new PollOutcome(null, run.CodeEnded("code_ended",
                        "Another queuey login ended this login code without a login: it was declined, or it expired."));
            }

            // En annen prosess kan ha spurt imens: dens tidspunkt og intervall gjelder.
            pending.LastPolledAt = stored.LastPolledAt;
            pending.Interval = stored.Interval;
            now = LoginTokens.Now();
            if (now < pending.ExpiresAt && (pending.LastPolledAt ?? pending.CreatedAt) + TimeSpan.FromSeconds(pending.Interval) > now)
                return new PollOutcome(null, null);

            if (now >= pending.ExpiresAt)
            {
                ended = "expired_token";
            }
            else
            {
                TokenAnswer answer = await oauth.PollAsync(endpoints, pending.DeviceCode, CancellationToken.None);
                if (answer.Succeeded)
                {
                    (login, replaced) = run.Store(file, pending, answer);
                }
                else if (answer.Error is "authorization_pending" or "slow_down")
                {
                    // RFC 8628 §3.5: slow_down øker intervallet med 5 sekunder, for dette og hvert senere spørsmål.
                    stored.LastPolledAt = pending.LastPolledAt = LoginTokens.Now();
                    stored.Interval = pending.Interval = pending.Interval + (answer.Error == "slow_down" ? 5 : 0);
                }
                else
                {
                    (ended, description) = (answer.Error ?? "unknown", answer.ErrorDescription);
                }
            }

            if (ended is not null)
                file.Pending.Remove(stored);
            LoginStore.Write(run.Path, file);
        }

        if (login is not null)
        {
            await run.RevokeReplacedAsync(replaced);
            return new PollOutcome(login, null);
        }

        return ended is null ? new PollOutcome(null, null) : new PollOutcome(null, run.CodeEnded(ended, description));
    }

    /// <summary>Removes the stored login for <paramref name="license"/> on <paramref name="host"/>.</summary>
    private static async Task ForgetAsync(string path, string host, string license)
    {
        using (await LoginStore.LockAsync(path, CancellationToken.None))
        {
            CredentialsFile file = LoginStore.Read(path);
            if (file.Logins.RemoveAll(l => l.ApiBase == host && l.License == license) > 0)
                LoginStore.Write(path, file);
        }
    }

    /// <summary>
    /// The profile as it is (null when it is not there yet), the API host and the license a login is for. With a profile,
    /// by the profile's rule (F2.7): a flag, then the profile, and a <c>QUEUEY_</c> variable that disagrees with the profile is
    /// an error, so a variable left in the shell from another environment never ends up in the profile. Without one, as
    /// every command: <c>--api-base</c>, <c>QUEUEY_API_BASE</c>, or Queuey's own host.
    /// </summary>
    // Security-review av #66 (BØR 2): login tok QUEUEY_API_BASE når profilen ikke hadde noen vert, og skrev den inn i profilen.
    internal static (ConnectionProfile? Existing, Uri ApiBase, string? License) Connection(ArgMap map, string? profile)
    {
        if (profile is null)
            return (null, ApiBase(map), Clean(map.Get("license")) ?? Clean(CliHost.Env("QUEUEY_LICENSE")));

        ConnectionProfile? existing = UserProfiles.TryLoad(profile, CliHost.Env, out string path);
        ResolvedConfig resolved = CliConfig.ResolveProfile(map, CliHost.Env, profile, existing ?? new ConnectionProfile(), path);
        return (existing, resolved.ResolvedApiBase(), resolved.LicensePublicId);
    }

    /// <summary>The API host without a profile: <c>--api-base</c>, <c>QUEUEY_API_BASE</c>, or Queuey's own.</summary>
    internal static Uri ApiBase(ArgMap map)
    {
        string? chosen = Clean(map.Get("api-base")) ?? Clean(CliHost.Env("QUEUEY_API_BASE"));
        if (chosen is null)
            return new ResolvedConfig().ResolvedApiBase();
        if (!Uri.TryCreate(chosen, UriKind.Absolute, out Uri? uri))
            throw new QueueyConfigurationException($"Invalid absolute URL for the API host: '{CliErrors.Shown(chosen)}'.");
        return new ResolvedConfig { ApiBaseOverride = uri }.ResolvedApiBase();
    }

    /// <summary>The link, when it is one a browser may be given: https, or http on this machine. Null otherwise.</summary>
    internal static Uri? SafeLink(string link)
        => Uri.TryCreate(link, UriKind.Absolute, out Uri? uri)
           && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            ? uri
            : null;

    private static bool DefaultOpenBrowser(Uri link)
    {
        try
        {
            // UseShellExecute åpner lenken med systemets standardvalg (open på macOS, xdg-open på Linux), uten et skall som
            // tolker den.
            using Process? process = Process.Start(new ProcessStartInfo(link.AbsoluteUri) { UseShellExecute = true });
            return process is not null || OperatingSystem.IsWindows();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    internal static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value!.Trim();

    /// <summary>
    /// The ingress host when Queuey runs on this machine (<paramref name="apiBase"/> is loopback) and the ingress is on another
    /// host, which a local stack seldom has; null otherwise.
    /// </summary>
    internal static string? IngressElsewhere(Uri apiBase, string? ingress)
        => apiBase.IsLoopback && Uri.TryCreate(ingress, UriKind.Absolute, out Uri? uri) && !uri.IsLoopback ? ingress : null;

    /// <summary>One run of <c>queuey login</c>: what it was asked for, and how it writes what it did.</summary>
    private sealed class LoginRun
    {
        public LoginRun(ArgMap map, bool json, string scope, string? profile, ConnectionProfile? existing, Uri apiBase, string host, string path)
        {
            Map = map;
            Json = json;
            Scope = scope;
            Profile = profile;
            Existing = existing;
            ApiBase = apiBase;
            Host = host;
            Path = path;
        }

        public ArgMap Map { get; }
        public bool Json { get; }
        public string Scope { get; }
        public string? Profile { get; }
        public ConnectionProfile? Existing { get; }
        public Uri ApiBase { get; }
        public string Host { get; }
        public string Path { get; }

        /// <summary>The link and the code, on stdout, for the person.</summary>
        public void ShowLink(PendingLogin pending, bool waiting)
        {
            string link = TerminalText.Line(pending.Link);
            string code = TerminalText.Line(pending.UserCode);
            int minutes = Math.Max(1, (int)Math.Ceiling((pending.ExpiresAt - LoginTokens.Now()).TotalMinutes));
            string then = waiting
                ? "This command waits until the link is approved."
                : "When it is approved, run `queuey login --wait`, or `queuey login` again, to finish.";

            if (Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    status = "waiting_for_person",
                    link = pending.Link,
                    verificationUri = pending.VerificationUri,
                    userCode = pending.UserCode,
                    expiresAt = pending.ExpiresAt,
                    apiHost = Host,
                    scope = Scope,
                    action = $"Show the person the link and the code, and ask them to approve it in the Queuey console. {then}",
                }, CliHost.JsonLine));
                return;
            }

            Console.WriteLine($"Open this link to log in to Queuey, and check that the page shows the code {code}:");
            Console.WriteLine($"  {link}");
            Console.WriteLine($"{then} The code expires in {minutes} minute{(minutes == 1 ? "" : "s")}.");
        }

        /// <summary>
        /// The approved login, put in <paramref name="file"/> in place of an earlier one for the same host and license, with the
        /// code removed. The caller holds the lock and writes the file. A license that is not a license id is refused.
        /// </summary>
        public (StoredLogin Login, StoredLogin? Replaced) Store(CredentialsFile file, PendingLogin pending, TokenAnswer answer)
        {
            // Serveren styrer verdien, og den vises og står i profilen (security-review av #66, KAN 6).
            if (!LicenseIds.IsOne(answer.License))
            {
                file.Pending.RemoveAll(p => p.DeviceCode == pending.DeviceCode);
                LoginStore.Write(Path, file);
                throw new QueueyException(
                    $"{Host} approved the login without a license id (lic_…) in the token answer, so it was not stored. The value is not shown.",
                    errorCode: "login_answer_invalid");
            }

            var login = new StoredLogin
            {
                ApiBase = Host,
                License = answer.License!,
                Scope = Scope,
                LoggedInAt = LoginTokens.Now(),
            };
            LoginTokens.Apply(login, answer);

            // Et svar med bredere scope enn det som ble bedt om (operate for read), lagres ikke: tokenet ville kunne mer enn
            // personen godkjente at denne maskinen skulle få (security-review KAN 2, 2026-10-09).
            if (!LoginTokens.WithinScope(login.Scope, Scope))
            {
                file.Pending.RemoveAll(p => p.DeviceCode == pending.DeviceCode);
                LoginStore.Write(Path, file);
                throw new QueueyException(
                    $"{Host} granted scope {login.Scope} when {Scope} was asked for, so the login was not kept.",
                    errorCode: "login_scope_broader")
                {
                    SuggestedAction = "Disconnect it under Connected apps in the Queuey console, and run queuey login again.",
                };
            }

            StoredLogin? replaced = file.Find(Host, login.License);
            if (replaced is not null)
                file.Logins.Remove(replaced);
            file.Logins.Add(login);
            file.Pending.RemoveAll(p => p.DeviceCode == pending.DeviceCode);
            return (login, replaced);
        }

        /// <summary>
        /// Revokes the connection a new login replaced (another scope, or one that no longer worked), which would otherwise stay
        /// under Connected apps until it expires. Only an attempt: the new one is stored either way.
        /// </summary>
        public async Task RevokeReplacedAsync(StoredLogin? replaced)
        {
            if (replaced?.RefreshToken is not { } old)
                return;
            try
            {
                using var oauth = new OAuthClient(ApiBase);
                await oauth.RevokeAsync(await oauth.DiscoverAsync(CancellationToken.None), old, "refresh_token", CancellationToken.None);
            }
            catch (Exception ex) when (ex is QueueyException or System.Net.Http.HttpRequestException or TaskCanceledException)
            {
                // Bare et forsøk.
            }
        }

        /// <summary>The code is spent: declined, expired, or unknown to Queuey. The caller has removed it; this writes the error.</summary>
        public int CodeEnded(string error, string? description = null)
        {
            (string message, string action) = error switch
            {
                "access_denied" => ("The login was declined in the Queuey console.",
                    "Run `queuey login` again if that was a mistake."),
                "expired_token" => ("The login code expired before anyone approved it.",
                    "Run `queuey login` for a new code, and approve it within its time."),
                "code_ended" => (description!, "Run `queuey login` for a new code."),
                _ => ($"{Host} ended the login code ({TerminalText.Line(error)}){(description is null ? "." : $": {TerminalText.Line(description)}")}",
                    "Run `queuey login` for a new code."),
            };
            return CliErrors.Write(Json, error, message, action, status: null, ExitCodes.RuntimeError, "Login failed");
        }

        /// <summary>
        /// Says the login is done: with a profile, after writing the license, the hosts and the workspace for the profile's
        /// environment into it.
        /// </summary>
        public async Task<int> FinishAsync(OAuthClient oauth, StoredLogin login, IReadOnlyList<LoginWorkspace>? workspaces, bool already)
        {
            string? tenant = null, note = null, workspaceName = null, workspaceEnvironment = null, profileFile = null;
            bool profileHasKey = false;

            if (Clean(Map.Get("ingress-base")) is { } flaggedIngress)
                login = await KeepIngressAsync(login, LoginStore.HostKey(new Uri(flaggedIngress, UriKind.Absolute)));
            if (IngressElsewhere(ApiBase, login.IngressBase) is { } elsewhere)
                Console.Error.WriteLine($"Warning: Queuey is on this machine ({Host}), and its ingress host is {TerminalText.Line(elsewhere)}, " +
                                        "another host. If your local ingress is not there, log in again with --ingress-base " +
                                        "http://localhost:<port>.");

            if (Profile is not null)
            {
                workspaces ??= await oauth.WorkspacesAsync(login.AccessToken, login.License, CancellationToken.None)
                               ?? throw LoginTokens.Ended(Host, login.License, "Queuey did not take the new token");
                (tenant, note) = Workspace(login.License, workspaces);
                if (tenant is not null && workspaces.FirstOrDefault(w => w.PublicId == tenant) is { } chosen)
                    (workspaceName, workspaceEnvironment) = (chosen.DisplayName, chosen.EffectiveEnvironment);

                string ingress = LoginStore.HostKey(Clean(Map.Get("ingress-base")) is { } flagged && Uri.TryCreate(flagged, UriKind.Absolute, out Uri? i)
                    ? i
                    : new ResolvedConfig
                    {
                        ApiBaseOverride = ApiBase,
                        IngressBaseOverride = Uri.TryCreate(login.IngressBase ?? Existing?.IngressBase, UriKind.Absolute, out Uri? given) ? given : null,
                    }.ResolvedIngressBase());
                (profileFile, profileHasKey, tenant) = UserProfiles.WriteLogin(Profile, CliHost.Env, login.License, Host, ingress, tenant);
                if (profileHasKey)
                    (workspaceName, workspaceEnvironment, note) = (null, null, null);
            }

            if (Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    status = "logged_in",
                    alreadyLoggedIn = already,
                    ingressBase = login.IngressBase,
                    apiHost = Host,
                    license = login.License,
                    scope = login.Scope,
                    user = login.User,
                    profile = Profile,
                    profileFile,
                    tenant,
                    workspace = tenant is null ? null : new { publicId = tenant, displayName = workspaceName, environment = workspaceEnvironment },
                    profileApiKeyWins = profileHasKey,
                    note,
                }, CliHost.JsonLine));
            }
            else
            {
                string who = login.User is null ? "" : $" as {TerminalText.Line(login.User)}";
                // Bare et smalere scope kan stå her: et bredere lagres aldri (Store, FinishedMeanwhile).
                if (login.Scope != Scope)
                    Console.Error.WriteLine($"Note: Queuey granted only scope {login.Scope}, narrower than {Scope}: this login can look, not change.");
                Console.WriteLine($"{(already ? "Already logged in" : "Logged in")} to {Host}{who}, license {login.License}, scope {login.Scope}.");
                if (Profile is not null)
                    Console.WriteLine(tenant is null
                        ? $"Profile {Profile} ({profileFile}): license and hosts written, no workspace."
                        : $"Profile {Profile} ({profileFile}): workspace {tenant} ({TerminalText.Line(workspaceName ?? tenant)}, {workspaceEnvironment}).");
                else
                    Console.WriteLine("Commands without an API key use this login for this host. For a workspace per environment: queuey login --profile dev");
                if (note is not null)
                    Console.WriteLine(note);
            }

            if (profileHasKey)
                Console.Error.WriteLine($"Note: profile {Profile} in {profileFile} has an apiKey, which wins over the login, so only the fields " +
                                        "it lacked were filled. Remove the apiKey there to use the login.");
            return ExitCodes.Success;
        }

        /// <summary>The login with <paramref name="ingress"/> as its ingress host, given with --ingress-base, which renewals keep.</summary>
        private async Task<StoredLogin> KeepIngressAsync(StoredLogin login, string ingress)
        {
            using (await LoginStore.LockAsync(Path, CancellationToken.None))
            {
                CredentialsFile file = LoginStore.Read(Path);
                StoredLogin stored = file.Find(Host, login.License) ?? login;
                stored.IngressBase = ingress;
                stored.IngressBaseFromFlag = true;
                if (file.Logins.Contains(stored))
                    LoginStore.Write(Path, file);
                return stored;
            }
        }

        /// <summary>
        /// The workspace for the profile: <c>--tenant</c>; the profile's own when the login still reaches it; otherwise the one
        /// workspace marked with the profile's environment (dev, test, staging or prod). With none or several, no workspace,
        /// and a note that says what to do.
        /// </summary>
        private (string? Tenant, string? Note) Workspace(string license, IReadOnlyList<LoginWorkspace> workspaces)
        {
            if (Clean(Map.Get("tenant")) is { } flagged)
                return (CliConfig.WorkspaceId(flagged, "--tenant"), null);

            if (Clean(Existing?.Tenant) is { } kept && Existing?.License == license
                && workspaces.Any(w => w.PublicId == kept && !w.Archived))
                return (kept, null);

            string? environment = Profile is "dev" or "test" or "staging" or "prod" ? Profile : null;
            if (environment is null)
                return (null, $"Profile {Profile} is not named after an environment (dev, test, staging or prod), so no workspace was picked. " +
                              $"Name one: queuey login --profile {Profile} --tenant ten_…");

            LoginWorkspace[] matching = workspaces.Where(w => !w.Archived && w.EffectiveEnvironment == environment).ToArray();
            return matching.Length switch
            {
                1 => (matching[0].PublicId, null),
                0 => (null, $"License {license} has no {environment} workspace yet. " +
                            (environment is "dev" or "test"
                                ? $"`queuey apply --profile {Profile}` creates one when queuey.deploy.json says {environment}; or make one: "
                                : "Make one: ") +
                            $"queuey create-tenant --name {WorkspaceCreation.NameFor(environment)} --environment {environment} " +
                            $"--license {license}. Then run `queuey login --profile {Profile}` again to write it into the profile."),
                _ => (null, $"License {license} has several {environment} workspaces: " +
                            string.Join(", ", matching.Select(w => $"{w.PublicId} ({TerminalText.Line(w.DisplayName ?? "")})")) +
                            $". Pick one: queuey login --profile {Profile} --tenant ten_…"),
            };
        }
    }
}

/// <summary><c>queuey logout</c>: revokes the login for the API host with Queuey (RFC 7009) and removes it from this machine.</summary>
internal static class LogoutCommand
{
    internal static readonly CommandOptions Options = new("logout", flags: new[] { "json" }, values: new[] { "profile" });

    public static async Task<int> RunAsync(string[] args)
    {
        if (!Options.TryParse(args, out ArgMap map, out int failure)) return failure;
        if (map.Has("help") || map.Has("h")) { Console.WriteLine(Usage.Text); return ExitCodes.Success; }

        bool json = map.Has("json");
        string? profile = CliHost.Profile(map);
        (_, Uri apiBase, string? license) = LoginCommand.Connection(map, profile);
        string host = LoginStore.HostKey(apiBase);
        string path = LoginStore.PathOf(CliHost.Env);

        // Fjernes her først, så ingen kommando bruker tokenene mens Queuey blir fortalt det.
        List<StoredLogin> removed;
        using (await LoginStore.LockAsync(path, CancellationToken.None))
        {
            CredentialsFile file = LoginStore.Read(path);
            removed = file.For(host).Where(l => license is null || l.License == license).ToList();
            int pending = file.Pending.RemoveAll(p => p.ApiBase == host);
            file.Logins.RemoveAll(removed.Contains);
            if (removed.Count > 0 || pending > 0)
                LoginStore.Write(path, file);
        }

        string licenses = string.Join(", ", removed.Select(l => l.License));
        if (removed.Count == 0)
        {
            if (json)
                Console.WriteLine(JsonSerializer.Serialize(new { status = "not_logged_in", apiHost = host, licenses = Array.Empty<string>() }, CliHost.JsonLine));
            else
                Console.WriteLine($"Not logged in to {host}{(license is null ? "" : $" for license {license}")}.");
            return ExitCodes.Success;
        }

        string? notRevoked = null;
        try
        {
            using var oauth = new OAuthClient(apiBase);
            OAuthEndpoints endpoints = await oauth.DiscoverAsync(CancellationToken.None);
            foreach (StoredLogin login in removed)
            {
                // Refresh-tokenet først: å trekke det tilbake ender hele tilkoblingen (RFC 7009 §2.1). Access-tokenet etterpå, så
                // det ikke virker ut timen heller.
                bool revoked = login.RefreshToken is null || await oauth.RevokeAsync(endpoints, login.RefreshToken, "refresh_token", CancellationToken.None);
                revoked &= await oauth.RevokeAsync(endpoints, login.AccessToken, "access_token", CancellationToken.None);
                if (!revoked)
                    notRevoked = endpoints.Revocation is null ? $"{host} offers no revocation endpoint" : $"{host} did not confirm it";
            }
        }
        catch (Exception ex) when (ex is QueueyException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            notRevoked = $"{host} could not be reached ({ex.GetType().Name})";
        }

        if (notRevoked is not null)
            return CliErrors.Write(json, "revoke_failed",
                $"Logged out of {host} on this machine ({licenses}), but {notRevoked}, so the connection may still be listed in Queuey.",
                "Disconnect it under Connected apps in the Queuey console.", status: null, ExitCodes.RuntimeError, "Logout incomplete",
                new Dictionary<string, object?> { ["licenses"] = removed.Select(l => l.License).ToArray(), ["removedLocally"] = true });

        if (json)
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                status = "logged_out",
                apiHost = host,
                licenses = removed.Select(l => l.License).ToArray(),
                revoked = true,
            }, CliHost.JsonLine));
        else
            Console.WriteLine($"Logged out of {host} ({licenses}). Queuey has ended the connection.");
        return ExitCodes.Success;
    }
}
