using System;
using System.Linq;
using System.Text.Json;
using Queuey.Client;

namespace Queuey.Client.Cli;

/// <summary>The resolved connection config for a CLI invocation.</summary>
internal sealed class ResolvedConfig
{
    public QueueyEnvironment Environment { get; init; } = QueueyEnvironment.Production;
    public Uri? ApiBaseOverride { get; init; }
    public Uri? IngressBaseOverride { get; init; }
    public string? ApiKey { get; init; }
    public string? TenantPublicId { get; init; }
    public string? LicensePublicId { get; init; }
    public string? Source { get; init; }

    /// <summary>The profile the connection came from (<c>--profile</c> or <c>QUEUEY_PROFILE</c>), or null without one.</summary>
    public string? Profile { get; init; }

    /// <summary>The file the profile's connection was read from, for messages.</summary>
    public string? ProfileFile { get; init; }

    /// <summary>The workspace the profile's connection names itself, before a flag or the deployment file has a say.</summary>
    public string? ProfileTenant { get; init; }

    /// <summary>
    /// The <c>queuey login</c> the CLI connects with when no API key is set, or null. A key that is set always wins, so this is
    /// null whenever <see cref="ApiKey"/> is set.
    /// </summary>
    public LoginTokens? Login { get; init; }

    /// <summary>The ingress signing key a publish signs with when no API key is set (<see cref="CliHost.WithIngressSigning"/>), or null.</summary>
    public string? SigningKeyId { get; init; }

    /// <summary>The secret of <see cref="SigningKeyId"/>. Never shown.</summary>
    public string? SigningSecret { get; init; }

    /// <summary>Applies the resolved values onto a <see cref="QueueyOptions"/>.</summary>
    public void Apply(QueueyOptions options)
    {
        options.Environment = Environment;
        if (ApiBaseOverride != null) options.ApiBaseAddress = ApiBaseOverride;
        if (IngressBaseOverride != null) options.IngressBaseAddress = IngressBaseOverride;
        options.ApiKey = ApiKey;
        options.AccessTokenProvider = string.IsNullOrWhiteSpace(ApiKey) && Login is { } login ? login.AccessTokenAsync : null;
        options.SigningKeyId = SigningKeyId;
        options.SigningSecret = SigningSecret;
        options.TenantPublicId = TenantPublicId;
        options.LicensePublicId = LicensePublicId;
        options.Source = Source;
    }

    /// <summary>
    /// The same config connecting with <paramref name="login"/>: its license when none is named, and the ingress host Queuey
    /// gave with it when none is set.
    /// </summary>
    public ResolvedConfig WithSigning(string keyId, string secret, string from) => new()
    {
        Environment = Environment,
        ApiBaseOverride = ApiBaseOverride,
        IngressBaseOverride = IngressBaseOverride,
        ApiKey = ApiKey,
        TenantPublicId = TenantPublicId,
        LicensePublicId = LicensePublicId,
        Source = Source,
        Profile = Profile,
        ProfileFile = ProfileFile,
        ProfileTenant = ProfileTenant,
        IngressBaseFrom = IngressBaseFrom,
        Login = Login,
        SigningKeyId = keyId,
        SigningSecret = secret,
        SigningFrom = from,
    };

    /// <summary>
    /// Where the ingress host came from, as a person reads it: a flag, a variable, <c>queuey.json</c>, a profile, or the login.
    /// Null means Queuey's own default.
    /// </summary>
    public string? IngressBaseFrom { get; init; }

    /// <summary><see cref="IngressBaseFrom"/>, or Queuey's default when nothing set it.</summary>
    public string IngressSource() => IngressBaseFrom ?? "Queuey's default ingress host";

    /// <summary>Where <see cref="SigningKeyId"/> came from: the environment or <c>.env</c>. Null without one.</summary>
    public string? SigningFrom { get; init; }

    private static Uri? LoginIngress(LoginTokens login)
        => Uri.TryCreate(login.Login.IngressBase, UriKind.Absolute, out Uri? ingress) ? ingress : null;

    public ResolvedConfig WithLogin(LoginTokens login) => new()
    {
        Environment = Environment,
        ApiBaseOverride = ApiBaseOverride,
        IngressBaseOverride = IngressBaseOverride ?? LoginIngress(login),
        ApiKey = ApiKey,
        TenantPublicId = TenantPublicId,
        LicensePublicId = LicensePublicId ?? login.Login.License,
        Source = Source,
        Profile = Profile,
        ProfileFile = ProfileFile,
        ProfileTenant = ProfileTenant,
        IngressBaseFrom = IngressBaseOverride is null && LoginIngress(login) is not null
            ? (login.Login.IngressBaseFromFlag ? "the login (--ingress-base when logging in)" : "the login (the ingress host Queuey gave)")
            : IngressBaseFrom,
        Login = login,
        SigningKeyId = SigningKeyId,
        SigningSecret = SigningSecret,
        SigningFrom = SigningFrom,
    };

    /// <summary>The same config pointed at another tenant — the one a deployment file names.</summary>
    public ResolvedConfig WithTenant(string? tenantPublicId) => new()
    {
        Environment = Environment,
        ApiBaseOverride = ApiBaseOverride,
        IngressBaseOverride = IngressBaseOverride,
        ApiKey = ApiKey,
        TenantPublicId = string.IsNullOrWhiteSpace(tenantPublicId) ? TenantPublicId : tenantPublicId,
        LicensePublicId = LicensePublicId,
        Source = Source,
        Profile = Profile,
        ProfileFile = ProfileFile,
        ProfileTenant = ProfileTenant,
        IngressBaseFrom = IngressBaseFrom,
        Login = Login,
        SigningKeyId = SigningKeyId,
        SigningSecret = SigningSecret,
        SigningFrom = SigningFrom,
    };

    /// <summary>
    /// What the hosts are, for whoami: <c>Production</c> for Queuey's own, <c>Local</c> when both are on this machine, and
    /// <c>Custom</c> for any other.
    /// </summary>
    // Gullflyten 2026-10-09: whoami sa Production mot localhost, fordi Production er det eneste innebygde miljøet.
    public string HostsLabel()
    {
        var production = new QueueyOptions { Environment = QueueyEnvironment.Production };
        Uri api = ResolvedApiBase(), ingress = ResolvedIngressBase();
        if (api == production.ResolveApiBaseAddress() && ingress == production.ResolveIngressBaseAddress())
            return "Production";
        return api.IsLoopback && ingress.IsLoopback ? "Local" : "Custom";
    }

    public Uri ResolvedApiBase() => ToOptions().ResolveApiBaseAddress();
    public Uri ResolvedIngressBase() => ToOptions().ResolveIngressBaseAddress();

    private QueueyOptions ToOptions()
    {
        var o = new QueueyOptions { Environment = Environment };
        if (ApiBaseOverride != null) o.ApiBaseAddress = ApiBaseOverride;
        if (IngressBaseOverride != null) o.IngressBaseAddress = IngressBaseOverride;
        return o;
    }
}

/// <summary>Resolves connection config with precedence: CLI flag &gt; environment variable &gt; queuey.json &gt; default.</summary>
internal static class CliConfig
{
    public static ResolvedConfig Resolve(ArgMap args, Func<string, string?> getEnv, string? configJson)
    {
        FileConfig file = ParseFile(configJson);
        EnsureTheFileDoesNotChooseWhereAKeyGoes(args, getEnv, file);

        return new ResolvedConfig
        {
            Environment = ParseEnvironment(First(args.Get("env"), getEnv("QUEUEY_ENV"), file.Environment)),
            ApiBaseOverride = ParseUri(First(args.Get("api-base"), getEnv("QUEUEY_API_BASE"), file.ApiBase)),
            IngressBaseOverride = ParseUri(First(args.Get("ingress-base"), getEnv("QUEUEY_INGRESS_BASE"), file.IngressBase)),
            IngressBaseFrom = !string.IsNullOrWhiteSpace(args.Get("ingress-base")) ? "--ingress-base"
                : !string.IsNullOrWhiteSpace(getEnv("QUEUEY_INGRESS_BASE")) ? "QUEUEY_INGRESS_BASE"
                : !string.IsNullOrWhiteSpace(file.IngressBase) ? $"{IngressFromFilePrefix}{args.Get("config") ?? "queuey.json"}"
                : null,
            ApiKey = First(args.Get("api-key"), getEnv("QUEUEY_API_KEY"), file.ApiKey),
            TenantPublicId = Workspace(
                (args.Get("tenant"), "--tenant"),
                (getEnv("QUEUEY_TENANT"), "QUEUEY_TENANT"),
                (file.Tenant, $"tenant in {args.Get("config") ?? "queuey.json"}")),
            LicensePublicId = First(args.Get("license"), getEnv("QUEUEY_LICENSE"), file.License),
            Source = First(args.Get("source"), getEnv("QUEUEY_SOURCE"), file.Source),
        };
    }

    /// <summary>
    /// Refuses a <c>queuey.json</c> that names a host for an API key the file does not hold itself: the key from
    /// <c>--api-key</c> or <c>QUEUEY_API_KEY</c>, the host from the file, and the host neither Queuey's own nor on this
    /// machine. <c>queuey.json</c> sits in the repository, so whoever wrote it would choose where the key is sent.
    /// <c>--api-base</c> or <c>--ingress-base</c> (or the variable) names the host instead, and then it is the caller's choice.
    /// </summary>
    // Security-review av #68 (B1, 2026-10-09): en nøkkel i miljøet gikk til verten queuey.json i et klonet repo pekte på.
    private static void EnsureTheFileDoesNotChooseWhereAKeyGoes(ArgMap args, Func<string, string?> getEnv, FileConfig file)
    {
        // Security-review av #68 runde 2 (R2-1): en apiKey i fila ga ikke unntak før, for flagget og miljøet vinner over den, og
        // en dummy-nøkkel i fila slapp nøkkelen fra miljøet gjennom til fila sin vert.
        bool keyFromOutside = !string.IsNullOrWhiteSpace(args.Get("api-key")) || !string.IsNullOrWhiteSpace(getEnv("QUEUEY_API_KEY"));
        if (!keyFromOutside)
            return;

        foreach ((string? fromFile, string flag, string variable, string field, Uri own) in new[]
                 {
                     (file.ApiBase, "api-base", "QUEUEY_API_BASE", "apiBase", new QueueyOptions().ResolveApiBaseAddress()),
                     (file.IngressBase, "ingress-base", "QUEUEY_INGRESS_BASE", "ingressBase", new QueueyOptions().ResolveIngressBaseAddress()),
                 })
        {
            if (string.IsNullOrWhiteSpace(fromFile) || !string.IsNullOrWhiteSpace(args.Get(flag)) || !string.IsNullOrWhiteSpace(getEnv(variable)))
                continue;
            if (FileChoseHost(fromFile, own) is { } host)
                throw FileChoseHostRefusal(field, args.Get("config") ?? "queuey.json", host, flag, variable,
                    "the API key comes from --api-key or QUEUEY_API_KEY");
        }
    }

    /// <summary>The prefix of <see cref="ResolvedConfig.IngressBaseFrom"/> when the ingress host came from <c>queuey.json</c>.</summary>
    internal const string IngressFromFilePrefix = "ingressBase in ";

    /// <summary>
    /// The host <paramref name="fromFile"/> names when a file should not choose it for a key: an absolute URL that is neither
    /// on this machine nor Queuey's own <paramref name="own"/>. Null otherwise.
    /// </summary>
    internal static string? FileChoseHost(string? fromFile, Uri own)
    {
        if (string.IsNullOrWhiteSpace(fromFile) || !Uri.TryCreate(fromFile!.Trim(), UriKind.Absolute, out Uri? host) || host.IsLoopback
            || string.Equals(host.GetLeftPart(UriPartial.Authority), own.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
            return null;
        return host.GetLeftPart(UriPartial.Authority);
    }

    internal static QueueyConfigurationException FileChoseHostRefusal(string field, string file, string host, string flag, string variable, string keyFrom)
        => new($"{field} in {file} names {host}, and {keyFrom}, not from that file. A file in the repository does not choose " +
               "where your key is sent, so nothing was sent.")
        {
            SuggestedAction = $"If that host is yours, name it yourself: --{flag} {host} (or {variable}). " +
                              "Or log in with queuey login, which sends a login only to the host it was made for.",
        };

    /// <summary>
    /// The connection of profile <paramref name="profile"/> (F2.7): a flag first, then the profile, never <c>queuey.json</c>.
    /// A <c>QUEUEY_</c> variable for the key, the license, the workspace or a host that is set to something else than the
    /// profile gives, is an error, never a silent pick: a profile is one environment's whole connection, and a variable left
    /// in the shell from another one would mix the two. The source, a trace label, may still come from the environment.
    /// </summary>
    public static ResolvedConfig ResolveProfile(ArgMap args, Func<string, string?> getEnv, string profile, ConnectionProfile values, string path)
    {
        string? Pick(string flag, string variable, string? fromProfile, string what)
        {
            if (args.Get(flag) is { } flagged && !string.IsNullOrWhiteSpace(flagged))
                return flagged;

            string? fromEnv = getEnv(variable);
            if (!string.IsNullOrWhiteSpace(fromEnv) && !string.Equals(fromEnv!.Trim(), fromProfile?.Trim(), StringComparison.Ordinal))
                throw new QueueyConfigurationException(
                    (string.IsNullOrWhiteSpace(fromProfile)
                        ? $"{variable} is set, and profile {profile} in {path} gives no {what}."
                        : $"{variable} is set to another {what} than profile {profile} in {path} gives.")
                    + " A profile is one environment's whole connection, so neither is picked. No value is shown.")
                {
                    SuggestedAction = $"Unset {variable} to use the profile, or run without --profile.",
                };

            return string.IsNullOrWhiteSpace(fromProfile) ? null : fromProfile!.Trim();
        }

        string? profileTenant = string.IsNullOrWhiteSpace(values.Tenant) ? null : WorkspaceId(values.Tenant!, $"tenant of profile {profile} in {path}");
        string? tenant = Pick("tenant", "QUEUEY_TENANT", profileTenant, "workspace");

        return new ResolvedConfig
        {
            Environment = QueueyEnvironment.Production,
            ApiBaseOverride = ParseUri(Pick("api-base", "QUEUEY_API_BASE", values.ApiBase, "API host")),
            IngressBaseOverride = ParseUri(Pick("ingress-base", "QUEUEY_INGRESS_BASE", values.IngressBase, "ingress host")),
            IngressBaseFrom = !string.IsNullOrWhiteSpace(args.Get("ingress-base")) ? "--ingress-base"
                : !string.IsNullOrWhiteSpace(values.IngressBase) ? $"profile {profile} in {path}"
                : null,
            ApiKey = Pick("api-key", "QUEUEY_API_KEY", values.ApiKey, "API key"),
            TenantPublicId = tenant is null ? null : WorkspaceId(tenant, args.Get("tenant") is null ? $"tenant of profile {profile}" : "--tenant"),
            LicensePublicId = Pick("license", "QUEUEY_LICENSE", values.License, "license"),
            Source = First(args.Get("source"), values.Source, getEnv("QUEUEY_SOURCE")),
            Profile = profile,
            ProfileFile = path,
            ProfileTenant = profileTenant,
        };
    }

    private static string? First(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    /// <summary>The first tenant that is set, by precedence, which has to be a workspace id (<see cref="WorkspaceId"/>).</summary>
    private static string? Workspace(params (string? Value, string Source)[] candidates)
    {
        foreach ((string? value, string source) in candidates)
            if (!string.IsNullOrWhiteSpace(value))
                return WorkspaceId(value!, source);
        return null;
    }

    /// <summary>
    /// <paramref name="value"/>, trimmed, when it is a workspace id (<c>ten_…</c>). Anything else is a usage error that
    /// names <paramref name="source"/> and never the value: an API key put there by mistake was sent in request URLs
    /// (<c>/tenants/&lt;value&gt;/…</c>) and printed by whoami.
    /// </summary>
    // Re-review 2026-10-05: en API-nøkkel i QUEUEY_TENANT, en forveksling i CI, ble brukt som workspace og skrevet ut. Sjekket
    // der en tenant leses fra kommandolinjen, miljøet eller queuey.json, ikke først når den brukes.
    internal static string WorkspaceId(string value, string source)
    {
        string trimmed = value.Trim();
        if (CliErrors.LooksLikeAWorkspaceId(trimmed))
            return trimmed;

        throw new CliUsageException("invalid_value",
            $"{source} is not a workspace id. Its value is not shown, since it may be a secret.",
            "A workspace id starts with ten_. An API key belongs in --api-key or QUEUEY_API_KEY.");
    }

    // Only Production is a built-in environment; point at a locally-running instance with
    // --api-base / --ingress-base (QUEUEY_API_BASE / QUEUEY_INGRESS_BASE) — e.g. for testing.
    private static QueueyEnvironment ParseEnvironment(string? value) => QueueyEnvironment.Production;

    private static Uri? ParseUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
            throw new QueueyConfigurationException($"Invalid absolute URL: '{value}'.");
        return uri;
    }

    private static FileConfig ParseFile(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new FileConfig();
        try
        {
            return JsonSerializer.Deserialize<FileConfig>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new FileConfig();
        }
        catch (JsonException ex)
        {
            throw new QueueyConfigurationException($"Could not parse queuey.json: {ex.Message}");
        }
    }

    private sealed class FileConfig
    {
        public string? Environment { get; set; }
        public string? ApiBase { get; set; }
        public string? IngressBase { get; set; }
        public string? ApiKey { get; set; }
        public string? Tenant { get; set; }
        public string? License { get; set; }
        public string? Source { get; set; }
    }
}
