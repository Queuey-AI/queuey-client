using System.Net;
using System.Text.Json;
using Queuey.Client.Cli;

namespace Queuey.Client.Cli.Tests;

/// <summary>
/// Brukerens tilkoblinger (F2.7, 2026-10-06): ~/.queuey/config.json, eller fila QUEUEY_USER_CONFIG navngir. Den holder
/// nøkler, så den leses bare når ingen andre kan lese eller skrive den, og ingenting av den vises i en feil. I samlingen
/// til konsolltestene, fordi noen tester bytter ut hvem som eier en fil (UserProfiles.Inspect), som ProfileCommandTests leser.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class UserProfilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "queuey-user-profile-tests", Guid.NewGuid().ToString("N"));

    public UserProfilesTests()
    {
        Directory.CreateDirectory(_dir);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>To miljøer, med hver sin nøkkel og sitt workspace, mot testserverens verter.</summary>
    internal const string TwoProfiles = """
        {
          "profiles": {
            "dev":  { "apiKey": "qak_dev.key-for-dev", "license": "lic_dev", "tenant": "ten_dev",
                      "apiBase": "https://api.test", "ingressBase": "https://ingress.test" },
            "prod": { "apiKey": "qak_prod.key-for-prod", "license": "lic_prod", "tenant": "ten_prod",
                      "apiBase": "https://api.test", "ingressBase": "https://ingress.test" }
          }
        }
        """;

    /// <summary>Skriver <c>config.json</c> i <paramref name="dir"/>, med rettighetene en bruker gir den (0600) eller <paramref name="mode"/>.</summary>
    internal static string WriteUserConfig(string dir, string json, UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite)
    {
        string path = Path.Combine(dir, "config.json");
        File.WriteAllText(path, json);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, mode);
        return path;
    }

    private static Func<string, string?> Pointing(string path) => name => name == UserProfiles.PathVariable ? path : null;

    [Fact]
    public void A_profile_is_read_from_the_file_QUEUEY_USER_CONFIG_names()
    {
        string path = WriteUserConfig(_dir, TwoProfiles);

        ConnectionProfile dev = UserProfiles.Load("dev", Pointing(path), out string from);

        Assert.Equal(path, from);
        Assert.Equal("qak_dev.key-for-dev", dev.ApiKey);
        Assert.Equal("lic_dev", dev.License);
        Assert.Equal("ten_dev", dev.Tenant);
        Assert.Equal("https://api.test", dev.ApiBase);
        Assert.Equal("https://ingress.test", dev.IngressBase);
    }

    [Fact]
    public void Without_QUEUEY_USER_CONFIG_the_file_is_in_the_home_folder()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.Equal(Path.Combine(home, ".queuey", "config.json"), UserProfiles.PathOf(_ => null));
    }

    [Fact]
    public void A_missing_file_says_where_it_looked_and_how_to_write_it()
    {
        string path = Path.Combine(_dir, "nope", "config.json");

        var ex = Assert.Throws<QueueyConfigurationException>(() => UserProfiles.Load("dev", Pointing(path), out _));

        Assert.Equal($"--profile dev needs its connection in {path}, and there is no such file.", ex.Message);
        Assert.Contains("chmod 600", ex.SuggestedAction ?? "");
    }

    [Fact]
    public void A_file_others_can_read_or_write_is_not_read()
    {
        if (OperatingSystem.IsWindows())
            return;   // Windows har ikke Unix-rettighetene; der beskytter brukerprofilens ACL fila.

        foreach (UnixFileMode other in new[] { UnixFileMode.GroupRead, UnixFileMode.GroupWrite, UnixFileMode.OtherRead, UnixFileMode.OtherWrite })
        {
            string path = WriteUserConfig(_dir, TwoProfiles, UnixFileMode.UserRead | UnixFileMode.UserWrite | other);

            var ex = Assert.Throws<QueueyConfigurationException>(() => UserProfiles.Load("dev", Pointing(path), out _));

            Assert.StartsWith($"{path} holds API keys, and other users can reach it (mode 0", ex.Message);
            Assert.EndsWith("), so it was not read.", ex.Message);
            Assert.Equal($"Let only you read and write it: chmod 600 {path}", ex.SuggestedAction);
            Assert.DoesNotContain("key-for-dev", ex.Message + ex.SuggestedAction);
        }

        string readable = WriteUserConfig(_dir, TwoProfiles,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        Assert.Contains("(mode 0644)", Assert.Throws<QueueyConfigurationException>(() => UserProfiles.Load("dev", Pointing(readable), out _)).Message);

        // Strengere enn 0600 er like godt.
        string readOnly = WriteUserConfig(_dir, TwoProfiles, UnixFileMode.UserRead);
        Assert.Equal("ten_dev", UserProfiles.Load("dev", Pointing(readOnly), out _).Tenant);
        File.SetUnixFileMode(readOnly, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact]
    public void A_file_in_a_folder_others_can_write_is_not_read_unless_the_sticky_bit_guards_it()
    {
        if (OperatingSystem.IsWindows())
            return;

        string shared = Path.Combine(_dir, "shared");
        Directory.CreateDirectory(shared);
        File.SetUnixFileMode(shared, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                     | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute);
        string path = WriteUserConfig(shared, TwoProfiles);

        var ex = Assert.Throws<QueueyConfigurationException>(() => UserProfiles.Load("dev", Pointing(path), out _));

        Assert.Equal($"{shared} can be written by other users (mode 0770), who could replace {path}, so it was not read.", ex.Message);
        Assert.Equal($"Let only you write to it: chmod go-w {shared}", ex.SuggestedAction);

        // Som /tmp: sticky-biten hindrer andre i å bytte ut en fil de ikke eier.
        File.SetUnixFileMode(shared, (UnixFileMode)Convert.ToInt32("1777", 8));
        Assert.Equal("ten_dev", UserProfiles.Load("dev", Pointing(path), out _).Tenant);
        File.SetUnixFileMode(shared, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Fact]
    public void A_field_the_file_does_not_have_is_refused_without_showing_anything_of_the_file()
    {
        // «api_key» for «apiKey» ville gitt en tilkobling uten nøkkel.
        string path = WriteUserConfig(_dir, """
            {
              "profiles": {
                "dev": { "api_key": "qak_dev.key-for-dev", "tenant": "ten_dev" }
              }
            }
            """);

        var ex = Assert.Throws<QueueyConfigurationException>(() => UserProfiles.Load("dev", Pointing(path), out _));

        Assert.StartsWith($"Could not read {path} at line 3 ($.profiles.dev.…)", ex.Message);
        Assert.EndsWith("Nothing of it is shown.", ex.Message);
        Assert.DoesNotContain("key-for-dev", ex.Message + ex.SuggestedAction);
    }

    [Fact]
    public void A_key_pasted_as_a_name_in_the_file_is_masked_in_the_error()
    {
        // Herding før tag (review av #53): stien fra parseren har navnene i fila, og en nøkkel limt inn som et navn sto i feilen.
        string path = WriteUserConfig(_dir, """{ "profiles": { "dev": { "qak_kid.pasted-key": "x" } } }""");

        var asField = Assert.Throws<QueueyConfigurationException>(() => UserProfiles.Load("dev", Pointing(path), out _));

        Assert.StartsWith($"Could not read {path} at line 1 ($.profiles.dev.…)", asField.Message);
        Assert.DoesNotContain("qak_kid", asField.Message + asField.SuggestedAction);

        path = WriteUserConfig(_dir, """{ "profiles": { "qak_kid.pasted-key": { "apiKey": 5 } } }""");

        var asProfile = Assert.Throws<QueueyConfigurationException>(() => UserProfiles.Load("dev", Pointing(path), out _));

        Assert.StartsWith($"Could not read {path} at line 1 ($.profiles.….apiKey)", asProfile.Message);
        Assert.DoesNotContain("qak_kid", asProfile.Message + asProfile.SuggestedAction);
    }

    [Fact]
    public void A_link_is_followed_to_the_file_it_points_to_and_that_file_is_checked()
    {
        if (OperatingSystem.IsWindows())
            return;

        // En vanlig dotfiles-oppsett: ~/.queuey/config.json er en lenke. Det er fila den peker på, som sjekkes og leses.
        string dotfiles = Path.Combine(_dir, "dotfiles");
        Directory.CreateDirectory(dotfiles);
        File.SetUnixFileMode(dotfiles, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string real = WriteUserConfig(dotfiles, TwoProfiles);
        string link = Path.Combine(_dir, "config.json");
        File.CreateSymbolicLink(link, real);

        Assert.Equal("ten_dev", UserProfiles.Load("dev", Pointing(link), out string from).Tenant);
        Assert.Equal(link, from);

        File.SetUnixFileMode(real, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        var ex = Assert.Throws<QueueyConfigurationException>(() => UserProfiles.Load("dev", Pointing(link), out _));

        Assert.Equal($"{link} (a link to {real}) holds API keys, and other users can reach it (mode 0644), so it was not read.", ex.Message);
        Assert.Equal($"Let only you read and write it: chmod 600 {real}", ex.SuggestedAction);
    }

    [Fact]
    public void A_file_that_belongs_to_another_user_is_not_read_and_root_reads_only_its_own()
    {
        if (OperatingSystem.IsWindows())
            return;

        string path = WriteUserConfig(_dir, TwoProfiles);
        Func<string, (uint, UnixFileMode)> realInspect = UserProfiles.Inspect;
        Func<uint> realUser = UserProfiles.CurrentUser;
        uint me = realUser();
        try
        {
            // En test kan ikke gi en fil til en annen bruker uten root, så eieren byttes i sømmen.
            UserProfiles.Inspect = p => p == path ? (4242u, UnixFileMode.UserRead | UnixFileMode.UserWrite) : realInspect(p);
            var other = Assert.Throws<QueueyConfigurationException>(() => UserProfiles.Load("dev", Pointing(path), out _));
            Assert.Equal($"{path} belongs to another user (uid 4242), not the one running queuey (uid {me}), so it was not read.", other.Message);

            // root kan lese alles filer, så en queuey som kjører som root, leser bare en fil root eier.
            if (me != 0)
            {
                UserProfiles.Inspect = realInspect;
                UserProfiles.CurrentUser = () => 0;
                var asRoot = Assert.Throws<QueueyConfigurationException>(() => UserProfiles.Load("dev", Pointing(path), out _));
                Assert.Equal($"{path} belongs to another user (uid {me}), not the one running queuey (uid 0), so it was not read.", asRoot.Message);
            }
        }
        finally
        {
            UserProfiles.Inspect = realInspect;
            UserProfiles.CurrentUser = realUser;
        }
    }

    [Fact]
    public void A_folder_above_the_file_that_belongs_to_another_user_is_not_read_but_root_may_own_one()
    {
        if (OperatingSystem.IsWindows())
            return;

        string path = WriteUserConfig(_dir, TwoProfiles);
        string above = Path.GetDirectoryName(_dir)!;
        UnixFileMode open = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        Func<string, (uint, UnixFileMode)> realInspect = UserProfiles.Inspect;
        try
        {
            UserProfiles.Inspect = p => p == above ? (4242u, open) : realInspect(p);
            var ex = Assert.Throws<QueueyConfigurationException>(() => UserProfiles.Load("dev", Pointing(path), out _));
            Assert.Equal($"{above} belongs to another user (uid 4242), who could replace {path}, so it was not read.", ex.Message);

            UserProfiles.Inspect = p => p == above ? (0u, open) : realInspect(p);
            Assert.Equal("ten_dev", UserProfiles.Load("dev", Pointing(path), out _).Tenant);
        }
        finally
        {
            UserProfiles.Inspect = realInspect;
        }
    }

    [Fact]
    public void Every_folder_up_to_the_home_folder_is_checked_and_none_above_it()
    {
        if (OperatingSystem.IsWindows())
            return;

        string home = Path.Combine(_dir, "home");
        string queuey = Path.Combine(home, ".queuey");
        Directory.CreateDirectory(queuey);
        File.SetUnixFileMode(queuey, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string path = WriteUserConfig(queuey, TwoProfiles);
        Func<string, (uint, UnixFileMode)> realInspect = UserProfiles.Inspect;
        var seen = new List<string>();
        try
        {
            UserProfiles.Inspect = p => { seen.Add(p); return realInspect(p); };

            Assert.Equal(path, UserProfiles.EnsureOnlyTheUserCanReachIt(path, home));
            Assert.Equal(new[] { path, queuey, home }, seen);

            // Som ssh: en hjemmemappe andre kan skrive i, lar dem bytte ut ~/.queuey.
            File.SetUnixFileMode(home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupWrite
                                       | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
            var ex = Assert.Throws<QueueyConfigurationException>(() => UserProfiles.EnsureOnlyTheUserCanReachIt(path, home));
            Assert.Equal($"{home} can be written by other users (mode 0770), who could replace {path}, so it was not read.", ex.Message);
            Assert.Equal($"Let only you write to it: chmod go-w {home}", ex.SuggestedAction);
        }
        finally
        {
            UserProfiles.Inspect = realInspect;
            File.SetUnixFileMode(home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void The_owner_and_mode_come_from_the_file_system()
    {
        if (OperatingSystem.IsWindows())
            return;

        // Eieren leses gjennom runtimens shim (UserProfiles.Native); feltene står der de skal, ellers feiler dette.
        string path = WriteUserConfig(_dir, TwoProfiles);

        (uint owner, UnixFileMode mode) = UserProfiles.Inspect(path);

        Assert.Equal(UserProfiles.CurrentUser(), owner);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
        Assert.Equal(0u, UserProfiles.Inspect("/").Owner);
    }

    [Fact]
    public void The_libc_fallback_names_the_same_user_as_the_runtime()
    {
        if (OperatingSystem.IsWindows())
            return;

        // Reserven når runtimens shim mangler SystemNative_GetEUid (review av #55): geteuid i libc, lastet ved navn.
        Assert.Equal(UserProfiles.CurrentUser(), UserProfiles.EffectiveUserFromLibc());
    }

    [Fact]
    public void Json_that_is_not_valid_is_refused_without_showing_it()
    {
        string path = WriteUserConfig(_dir, """{ "profiles": { "dev": { "apiKey": qak_dev.key-for-dev } } }""");

        var ex = Assert.Throws<QueueyConfigurationException>(() => UserProfiles.Load("dev", Pointing(path), out _));

        Assert.StartsWith($"Could not read {path} at line 1", ex.Message);
        Assert.DoesNotContain("key-for-dev", ex.Message + ex.SuggestedAction);
    }

    [Fact]
    public void A_profile_the_file_does_not_have_names_the_ones_it_has()
    {
        string path = WriteUserConfig(_dir, TwoProfiles);

        var ex = Assert.Throws<QueueyConfigurationException>(() => UserProfiles.Load("staging", Pointing(path), out _));

        Assert.Equal($"{path} has no profile 'staging', so --profile staging has no connection. It has dev, prod.", ex.Message);
    }
}

/// <summary>
/// Tilkoblingen med en profil (F2.7): et flagg, så profilen, aldri queuey.json. En QUEUEY_-variabel som sier noe annet enn
/// profilen, er en feil som ikke viser noen av verdiene: en verdi fra et annet miljø blandes aldri inn.
/// </summary>
public class ProfileConnectionTests
{
    private const string UserFile = "/home/someone/.queuey/config.json";

    private static readonly ConnectionProfile Dev = new()
    {
        ApiKey = "qak_dev.key-for-dev",
        License = "lic_dev",
        Tenant = "ten_dev",
        ApiBase = "https://api.dev.test",
        IngressBase = "https://ingress.dev.test",
    };

    private static ResolvedConfig Resolve(ConnectionProfile profile, string[] args, params (string Name, string Value)[] env)
    {
        ArgMap map = ArgMap.Parse(args, new HashSet<string>());
        Dictionary<string, string> values = env.ToDictionary(e => e.Name, e => e.Value);
        return CliConfig.ResolveProfile(map, name => values.TryGetValue(name, out string? v) ? v : null, "dev", profile, UserFile);
    }

    [Fact]
    public void The_profile_gives_the_connection_and_a_flag_wins_for_its_value()
    {
        ResolvedConfig config = Resolve(Dev, Array.Empty<string>());

        Assert.Equal("qak_dev.key-for-dev", config.ApiKey);
        Assert.Equal("lic_dev", config.LicensePublicId);
        Assert.Equal("ten_dev", config.TenantPublicId);
        Assert.Equal(new Uri("https://api.dev.test"), config.ApiBaseOverride);
        Assert.Equal(new Uri("https://ingress.dev.test"), config.IngressBaseOverride);
        Assert.Equal("dev", config.Profile);
        Assert.Equal(UserFile, config.ProfileFile);

        ResolvedConfig flagged = Resolve(Dev, new[] { "--api-key", "qak_flag.k", "--tenant", "ten_flag", "--api-base", "https://api.flag.test" });

        Assert.Equal("qak_flag.k", flagged.ApiKey);
        Assert.Equal("ten_flag", flagged.TenantPublicId);
        Assert.Equal(new Uri("https://api.flag.test"), flagged.ApiBaseOverride);
        Assert.Equal("lic_dev", flagged.LicensePublicId);
        Assert.Equal("ten_dev", flagged.ProfileTenant);
    }

    [Theory]
    [InlineData("QUEUEY_API_KEY", "qak_prod.key-left-over", "API key")]
    [InlineData("QUEUEY_LICENSE", "lic_prod", "license")]
    [InlineData("QUEUEY_TENANT", "ten_prod", "workspace")]
    [InlineData("QUEUEY_API_BASE", "https://api.prod.test", "API host")]
    [InlineData("QUEUEY_INGRESS_BASE", "https://ingress.prod.test", "ingress host")]
    public void A_variable_left_from_another_environment_fails_and_shows_neither_value(string variable, string value, string what)
    {
        var ex = Assert.Throws<QueueyConfigurationException>(() => Resolve(Dev, Array.Empty<string>(), (variable, value)));

        Assert.Equal($"{variable} is set to another {what} than profile dev in {UserFile} gives. A profile is one environment's " +
                     "whole connection, so neither is picked. No value is shown.", ex.Message);
        Assert.Equal($"Unset {variable} to use the profile, or run without --profile.", ex.SuggestedAction);
        Assert.DoesNotContain(value, ex.Message);
        Assert.DoesNotContain("key-for-dev", ex.Message);
        Assert.DoesNotContain("dev.test", ex.Message);
    }

    [Fact]
    public void A_variable_that_says_what_the_profile_says_is_no_conflict()
    {
        ResolvedConfig config = Resolve(Dev, Array.Empty<string>(), ("QUEUEY_API_KEY", "qak_dev.key-for-dev"), ("QUEUEY_TENANT", "ten_dev"));

        Assert.Equal("qak_dev.key-for-dev", config.ApiKey);
        Assert.Equal("ten_dev", config.TenantPublicId);
    }

    [Fact]
    public void A_variable_for_a_value_the_profile_does_not_give_fails()
    {
        var noLicense = new ConnectionProfile { ApiKey = Dev.ApiKey, Tenant = Dev.Tenant };

        var ex = Assert.Throws<QueueyConfigurationException>(() => Resolve(noLicense, Array.Empty<string>(), ("QUEUEY_LICENSE", "lic_other")));

        Assert.StartsWith($"QUEUEY_LICENSE is set, and profile dev in {UserFile} gives no license.", ex.Message);
        Assert.DoesNotContain("lic_other", ex.Message);
    }

    [Fact]
    public void The_source_is_a_label_and_may_still_come_from_the_environment()
    {
        Assert.Equal("ci", Resolve(Dev, Array.Empty<string>(), ("QUEUEY_SOURCE", "ci")).Source);

        var labelled = new ConnectionProfile { ApiKey = Dev.ApiKey, Source = "laptop" };
        Assert.Equal("laptop", Resolve(labelled, Array.Empty<string>(), ("QUEUEY_SOURCE", "ci")).Source);
        Assert.Equal("flag", Resolve(labelled, new[] { "--source", "flag" }, ("QUEUEY_SOURCE", "ci")).Source);
    }

    [Fact]
    public void A_profile_tenant_that_is_not_a_workspace_id_is_refused_without_showing_it()
    {
        var pasted = new ConnectionProfile { ApiKey = Dev.ApiKey, Tenant = "qak_dev.pasted-key" };

        var ex = Assert.Throws<CliUsageException>(() => Resolve(pasted, Array.Empty<string>()));

        Assert.Equal($"tenant of profile dev in {UserFile} is not a workspace id. Its value is not shown, since it may be a secret.", ex.Message);
    }
}

/// <summary>
/// `--profile` i kommandoene (F2.7): ett flagg, eller QUEUEY_PROFILE, velger både tilkoblingen hos brukeren og verdiene i
/// deploy-fila. Mangler en av halvdelene, feiler kommandoen før noe sendes, og sier hvilken.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class ProfileCommandTests : IDisposable
{
    private const string DeployJson = """
        {
          "tenant": "${QUEUEY_TENANT}",
          "queues": { "orders": {} },
          "profiles": {
            "dev":  { "variables": { "QUEUEY_TENANT": "ten_dev" } },
            "prod": { "variables": { "QUEUEY_TENANT": "ten_prod" } }
          }
        }
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "queuey-profile-cli-tests", Guid.NewGuid().ToString("N"));
    private readonly string _home;
    private readonly string _deploy;

    public ProfileCommandTests()
    {
        _home = Path.Combine(_dir, "home");
        Directory.CreateDirectory(_home);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        _deploy = Path.Combine(_dir, "queuey.deploy.json");
        File.WriteAllText(_deploy, DeployJson);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string UserFile => Path.Combine(_home, "config.json");

    private string UserConfig(string json = UserProfilesTests.TwoProfiles) => UserProfilesTests.WriteUserConfig(_home, json);

    /// <summary>Miljøet en kommando ser: brukerens fil her, og <paramref name="extra"/>.</summary>
    private Dictionary<string, string> Env(params (string Name, string Value)[] extra)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal) { [UserProfiles.PathVariable] = UserFile };
        foreach ((string name, string value) in extra)
            env[name] = value;
        return env;
    }

    // Gjennom CliEntry, så en feil blir det svaret en skal lese, med sin exit-kode. Uten CliHarness.With: tilkoblingen er profilens.
    private static Task<CliRun> Run(RecordingHandler? api, Dictionary<string, string> env, params string[] args)
        => CliHarness.RunAsync(() => CliEntry.RunAsync(args), api, env);

    /// <summary>En server som godtar apply i det workspacet den blir spurt om.</summary>
    private static RecordingHandler ApplyServer() => new(req => req switch
    {
        { Method.Method: "GET" } when req.Path.StartsWith("/tenants/", StringComparison.Ordinal)
            => RecordingHandler.Json(HttpStatusCode.OK, Array.Empty<object>()),
        { Method.Method: "PUT", Path: "/queues" }
            => RecordingHandler.Json(HttpStatusCode.OK, new { publicId = "que_orders", displayName = "orders", created = true, hasDeliveryTarget = false }),
        _ => throw new InvalidOperationException(req.Key),
    });

    private static void AssertKey(RecordingHandler api, string key)
    {
        Assert.NotEmpty(api.Headers);
        Assert.All(api.Headers, h => Assert.Equal(key, h["X-Api-Key"]));
    }

    [Theory]
    [InlineData("dev")]
    [InlineData("prod")]
    public async Task Apply_with_a_profile_takes_its_connection_and_the_files_values_for_it(string profile)
    {
        UserConfig();
        RecordingHandler api = ApplyServer();

        CliRun run = await Run(api, Env(), "apply", "--file", _deploy, "--profile", profile);

        Assert.True(run.Exit == ExitCodes.Success, run.Stdout + run.Stderr);
        Assert.Contains($"GET /tenants/ten_{profile}/queues", api.Requests.Select(r => r.Key));
        Assert.Equal($"ten_{profile}", api.Requests.Single(r => r.Key == "PUT /queues").Json.GetProperty("tenantPublicId").GetString());
        Assert.All(api.Requests, r => Assert.Equal("api.test", r.Uri.Host));
        AssertKey(api, $"qak_{profile}.key-for-{profile}");
        Assert.Contains($"(tenant ten_{profile})", run.Stdout);
        Assert.DoesNotContain("key-for", run.Stdout + run.Stderr);
    }

    [Fact]
    public async Task QUEUEY_PROFILE_picks_a_profile_as_the_flag_does_and_the_flag_wins()
    {
        UserConfig();

        RecordingHandler byVariable = ApplyServer();
        CliRun run = await Run(byVariable, Env(("QUEUEY_PROFILE", "prod")), "apply", "--file", _deploy);
        Assert.True(run.Exit == ExitCodes.Success, run.Stdout + run.Stderr);
        Assert.Contains("GET /tenants/ten_prod/queues", byVariable.Requests.Select(r => r.Key));

        RecordingHandler byFlag = ApplyServer();
        run = await Run(byFlag, Env(("QUEUEY_PROFILE", "prod")), "apply", "--file", _deploy, "--profile", "dev");
        Assert.True(run.Exit == ExitCodes.Success, run.Stdout + run.Stderr);
        Assert.Contains("GET /tenants/ten_dev/queues", byFlag.Requests.Select(r => r.Key));
    }

    [Fact]
    public async Task Plan_with_a_profile_asks_about_the_files_values_in_the_profiles_workspace()
    {
        UserConfig();
        File.WriteAllText(_deploy, """
            {
              "tenant": "${QUEUEY_TENANT}",
              "queues": { "orders": { "delivery": { "url": "${QUEUEY_ORDERS_URL}" } } },
              "profiles": {
                "dev": { "variables": { "QUEUEY_TENANT": "ten_dev", "QUEUEY_ORDERS_URL": "https://dev.example.com/orders" } }
              }
            }
            """);
        var api = new RecordingHandler(req => req switch
        {
            { Method.Method: "GET" } when req.Path.EndsWith("/queues", StringComparison.Ordinal)
                => RecordingHandler.Json(HttpStatusCode.OK, new[] { new { publicId = "que_orders", displayName = "orders", mode = "Deliver", hasDeliveryTarget = true } }),
            { Method.Method: "PUT", Path: "/queues" }
                => RecordingHandler.Json(HttpStatusCode.OK, new { dryRun = true, publicId = "que_orders", displayName = "orders", created = false, hasDeliveryTarget = true }),
            _ => RecordingHandler.Json(HttpStatusCode.OK, new { dryRun = true, target = "queue que_orders", changes = Array.Empty<object>(), notes = Array.Empty<string>() }),
        });

        CliRun run = await Run(api, Env(), "plan", "--file", _deploy, "--profile", "dev", "--json");

        Assert.True(run.Exit == ExitCodes.Success, run.Stdout + run.Stderr);
        Assert.Equal("ten_dev", JsonDocument.Parse(run.Stdout).RootElement.GetProperty("tenant").GetString());
        Assert.Contains("GET /tenants/ten_dev/queues", api.Requests.Select(r => r.Key));
        Assert.Contains(api.Writes, w => w.Body?.Contains("https://dev.example.com/orders", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(api.Requests, r => r.Body?.Contains("${", StringComparison.Ordinal) == true);
        AssertKey(api, "qak_dev.key-for-dev");
    }

    [Fact]
    public async Task A_dry_run_with_a_profile_needs_only_the_files_half()
    {
        // Ingen brukerfil: en dry run kobler aldri til, så den trenger bare fila sine verdier. Den sier likevel fra på stderr
        // (herding før tag), så brukeren vet det før en ekte apply stopper på det.
        CliRun run = await Run(null, new Dictionary<string, string>(), "apply", "--file", _deploy, "--dry-run", "--profile", "prod");

        Assert.True(run.Exit == ExitCodes.Success, run.Stdout + run.Stderr);
        Assert.Contains("Nothing was sent.", run.Stdout);
        Assert.Contains("Warning: the dry run went on without profile prod's connection, which apply --profile prod needs: " +
                        $"--profile prod needs its connection in {CliHarness.NoUserConfig}, and there is no such file.", run.Stderr);

        // Med brukerens fil på plass er det ingenting å si fra om.
        UserConfig();
        CliRun quiet = await Run(null, Env(), "apply", "--file", _deploy, "--dry-run", "--profile", "prod");
        Assert.True(quiet.Exit == ExitCodes.Success, quiet.Stdout + quiet.Stderr);
        Assert.DoesNotContain("Warning", quiet.Stderr);
    }

    [Fact]
    public async Task Each_half_that_is_missing_fails_the_command_before_anything_is_sent_and_is_named()
    {
        RecordingHandler api = ApplyServer();

        // Ingen brukerfil.
        CliRun noUserFile = await Run(api, Env(), "apply", "--file", _deploy, "--profile", "dev");
        Assert.Equal(ExitCodes.Configuration, noUserFile.Exit);
        Assert.Contains($"--profile dev needs its connection in {UserFile}, and there is no such file.", noUserFile.Stderr);

        // Brukerfila har ikke profilen.
        UserConfig("""{ "profiles": { "prod": { "apiKey": "qak_prod.key-for-prod", "tenant": "ten_prod" } } }""");
        CliRun noConnection = await Run(api, Env(), "apply", "--file", _deploy, "--profile", "dev");
        Assert.Equal(ExitCodes.Configuration, noConnection.Exit);
        Assert.Contains($"{UserFile} has no profile 'dev', so --profile dev has no connection. It has prod.", noConnection.Stderr);

        // Deploy-fila har ikke profilen.
        UserConfig("""{ "profiles": { "staging": { "apiKey": "qak_staging.key-for-staging", "tenant": "ten_staging" } } }""");
        CliRun noValues = await Run(api, Env(), "apply", "--file", _deploy, "--profile", "staging");
        Assert.Equal(ExitCodes.Configuration, noValues.Exit);
        Assert.Contains($"{_deploy}: The deployment file has no profile 'staging', so --profile staging has no values for it. Its profiles are dev, prod.",
            noValues.Stderr);

        // Ingen deploy-fil der verify leter.
        UserConfig();
        string nowhere = Path.Combine(_dir, "missing", "queuey.deploy.json");
        CliRun noFile = await Run(api, Env(), "verify", "orders", "--event", "evt_1", "--deployment", nowhere, "--profile", "dev");
        Assert.Equal(ExitCodes.Configuration, noFile.Exit);
        Assert.Contains($"--profile dev needs the deployment file's values for dev, and there is no deployment file at '{nowhere}'.", noFile.Stderr);

        Assert.Empty(api.Requests);
        Assert.Empty(api.ManagementRequests);
    }

    [Fact]
    public async Task A_connection_and_a_file_that_name_different_workspaces_fail_and_a_tenant_flag_decides()
    {
        UserConfig();
        File.WriteAllText(_deploy, """
            { "tenant": "${QUEUEY_TENANT}", "queues": { "orders": {} },
              "profiles": { "dev": { "variables": { "QUEUEY_TENANT": "ten_other" } } } }
            """);

        RecordingHandler api = ApplyServer();
        CliRun run = await Run(api, Env(), "apply", "--file", _deploy, "--profile", "dev");

        Assert.Equal(ExitCodes.Configuration, run.Exit);
        Assert.Contains($"Profile dev in {UserFile} names workspace ten_dev, and {_deploy} names workspace ten_other for profile dev.", run.Stderr);
        Assert.Empty(api.Requests);

        // Et flagg vinner for sin verdi, og --tenant må være enig med fila, som uten profil.
        RecordingHandler flagged = ApplyServer();
        run = await Run(flagged, Env(), "apply", "--file", _deploy, "--profile", "dev", "--tenant", "ten_other");
        Assert.True(run.Exit == ExitCodes.Success, run.Stdout + run.Stderr);
        Assert.Contains("GET /tenants/ten_other/queues", flagged.Requests.Select(r => r.Key));
    }

    [Fact]
    public async Task A_value_left_in_the_shell_from_another_environment_fails_and_is_not_shown()
    {
        UserConfig();
        RecordingHandler api = ApplyServer();

        CliRun key = await Run(api, Env(("QUEUEY_API_KEY", "qak_prod.key-for-prod")), "apply", "--file", _deploy, "--profile", "dev");
        Assert.Equal(ExitCodes.Configuration, key.Exit);
        Assert.Contains($"QUEUEY_API_KEY is set to another API key than profile dev in {UserFile} gives.", key.Stderr);

        // En ${VAR} i fila som profilen og miljøet gir hver sin verdi.
        CliRun variable = await Run(api, Env(("QUEUEY_TENANT", "ten_prod")), "apply", "--file", _deploy, "--profile", "dev");
        Assert.Equal(ExitCodes.Configuration, variable.Exit);
        Assert.Contains("${QUEUEY_TENANT} has a value in profile dev and another in the environment, so neither is picked.", variable.Stderr);

        Assert.DoesNotContain("key-for", key.Stdout + key.Stderr + variable.Stdout + variable.Stderr);
        Assert.DoesNotContain("ten_prod", variable.Stdout + variable.Stderr);
        Assert.Empty(api.Requests);

        // Den samme verdien som profilen gir, er ingen konflikt.
        RecordingHandler same = ApplyServer();
        CliRun agreed = await Run(same, Env(("QUEUEY_API_KEY", "qak_dev.key-for-dev"), ("QUEUEY_TENANT", "ten_dev")),
            "apply", "--file", _deploy, "--profile", "dev");
        Assert.True(agreed.Exit == ExitCodes.Success, agreed.Stdout + agreed.Stderr);
        AssertKey(same, "qak_dev.key-for-dev");
    }

    [Fact]
    public async Task With_a_profile_queuey_json_is_not_read()
    {
        UserConfig();
        string json = Path.Combine(_dir, "queuey.json");
        File.WriteAllText(json, """{ "apiKey": "qak_json.key-from-json", "tenant": "ten_json", "apiBase": "https://json.test" }""");
        RecordingHandler api = ApplyServer();

        CliRun run = await Run(api, Env(), "apply", "--file", _deploy, "--profile", "dev", "--config", json);

        Assert.True(run.Exit == ExitCodes.Success, run.Stdout + run.Stderr);
        AssertKey(api, "qak_dev.key-for-dev");
        Assert.All(api.Requests, r => Assert.Equal("api.test", r.Uri.Host));
    }

    [Fact]
    public async Task Verify_publish_and_events_get_reach_the_profiles_workspace_with_its_key()
    {
        UserConfig();

        var verified = new RecordingHandler(req => req.Key switch
        {
            "GET /tenants/ten_dev/queues" => FlowAnswers.Queues(),
            "POST /queues/que_orders/verifications" => RecordingHandler.Json(HttpStatusCode.Created, FlowAnswers.Verification("passed", tenant: "ten_dev")),
            _ => throw new InvalidOperationException(req.Key),
        });
        CliRun verify = await Run(verified, Env(), "verify", "orders", "--event", "evt_1", "--deployment", _deploy, "--profile", "dev");
        Assert.True(verify.Exit == ExitCodes.Success, verify.Stdout + verify.Stderr);
        Assert.Contains("in ten_dev, event evt_1", verify.Stdout);

        var published = new RecordingHandler(req => req.Key switch
        {
            "GET /tenants/ten_dev/queues" => FlowAnswers.Queues(),
            "GET /queues/que_orders/config" => RecordingHandler.Json(HttpStatusCode.OK, new { ingress = new { authMode = "ApiKey", successStatusCode = 202 } }),
            "POST /events/ten_dev/orders" => RecordingHandler.Json(HttpStatusCode.Accepted, new
            {
                queuePublicId = "que_orders", eventId = "evt_7", receivedAtUtc = "2026-10-06T10:00:00Z", mode = "Deliver", replayed = false,
            }),
            _ => throw new InvalidOperationException(req.Key),
        });
        CliRun publish = await Run(published, Env(), "publish", "orders", "--data", "{}", "--deployment", _deploy, "--profile", "dev", "--json");
        Assert.True(publish.Exit == ExitCodes.Success, publish.Stdout + publish.Stderr);
        Assert.Equal("ingress.test", published.Requests.Single(r => r.Method == HttpMethod.Post).Uri.Host);
        Assert.Equal(_deploy, JsonDocument.Parse(publish.Stdout).RootElement.GetProperty("tenantFrom").GetString());

        var read = new RecordingHandler(req => req.Key switch
        {
            "GET /tenants/ten_dev/queues" => FlowAnswers.Queues(),
            "GET /events/que_orders/evt_1" => RecordingHandler.Json(HttpStatusCode.OK, new
            {
                publicId = "evt_1",
                queuePublicId = "que_orders",
                status = 2,
                source = "orders-api",
                contentType = "application/json",
                payloadText = (string?)null,
                createdAtUtc = "2026-10-06T10:00:00Z",
                completedAtUtc = "2026-10-06T10:00:01Z",
                holdReason = (string?)null,
                attemptCount = 1,
                attempts = new[]
                {
                    new { attemptNumber = 1, status = 1, responseCode = 200, durationMs = 38, decisionKind = (string?)null, errorMessage = (string?)null },
                },
                canRevealContent = false,
                payloadVisibility = "shape",
                payloadShape = "{ orderId: string }",
            }),
            _ => throw new InvalidOperationException(req.Key),
        });
        CliRun events = await Run(read, Env(), "events", "get", "evt_1", "--queue", "orders", "--deployment", _deploy, "--profile", "dev", "--json");
        Assert.True(events.Exit == ExitCodes.Success, events.Stdout + events.Stderr);

        foreach (RecordingHandler api in new[] { verified, published, read })
            AssertKey(api, "qak_dev.key-for-dev");
    }

    [Fact]
    public async Task Listen_and_credentials_take_the_files_half_from_the_deployment_file_where_they_run()
    {
        UserConfig();
        string empty = Path.Combine(_dir, "empty");
        Directory.CreateDirectory(empty);
        string before = Directory.GetCurrentDirectory();
        try
        {
            // listen og credentials har ikke --deployment: de leser ./queuey.deploy.json, som apply gjør uten --file.
            Directory.SetCurrentDirectory(_dir);

            var listed = new RecordingHandler(req => req.Method == HttpMethod.Get && req.Path.StartsWith("/tenants/ten_dev/", StringComparison.Ordinal)
                ? RecordingHandler.Json(HttpStatusCode.OK, Array.Empty<object>())
                : throw new InvalidOperationException(req.Key));
            CliRun credentials = await Run(listed, Env(), "credentials", "list", "--profile", "dev");
            Assert.True(credentials.Exit == ExitCodes.Success, credentials.Stdout + credentials.Stderr);
            Assert.Contains("0 credential(s) in ten_dev", credentials.Stdout);
            AssertKey(listed, "qak_dev.key-for-dev");

            var queues = new RecordingHandler(req => req.Key == "GET /tenants/ten_dev/queues" ? FlowAnswers.Queues() : throw new InvalidOperationException(req.Key));
            ListenTarget? target = null;
            await CliHarness.RunAsync(async () =>
            {
                Assert.True(ListenCommand.Options.TryParse(
                    new[] { "--forward-to", "http://localhost:5000", "--queue", "orders", "--profile", "dev" }, out ArgMap map, out _));
                target = await ListenCommand.ResolveTargetAsync(map, ListenCommand.Connection(map));
                return 0;
            }, queues, Env());
            Assert.Equal(new ListenTarget("queue", "que_orders", Name: "orders"), target);
            AssertKey(queues, "qak_dev.key-for-dev");

            // Uten deploy-fila der de kjører, mangler profilens andre halvdel.
            Directory.SetCurrentDirectory(empty);
            CliRun missing = await Run(null, Env(), "credentials", "list", "--profile", "dev");
            Assert.Equal(ExitCodes.Configuration, missing.Exit);
            Assert.Contains("--profile dev needs the deployment file's values for dev, and there is no deployment file at 'queuey.deploy.json'.", missing.Stderr);
        }
        finally
        {
            Directory.SetCurrentDirectory(before);
        }
    }

    [Fact]
    public async Task A_command_that_takes_no_profile_refuses_to_run_while_QUEUEY_PROFILE_is_set()
    {
        UserConfig();
        RecordingHandler api = ApplyServer();

        // Den ville ellers koblet til med queuey.json og QUEUEY_-variablene, ikke profilens miljø.
        CliRun run = await Run(api, Env(("QUEUEY_PROFILE", "dev")), CliHarness.With("replay", "evt_1", "--queue", "que_orders"));
        Assert.Equal(ExitCodes.Configuration, run.Exit);
        Assert.Contains("QUEUEY_PROFILE is set, and this command does not take a profile", run.Stderr);

        CliRun flagged = await Run(api, Env(), CliHarness.With("replay", "evt_1", "--queue", "que_orders", "--profile", "dev"));
        Assert.Equal(ExitCodes.Usage, flagged.Exit);
        Assert.Contains("Unknown option --profile for queuey replay.", flagged.Stderr);

        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task Whoami_shows_the_profiles_connection_and_the_file_it_came_from()
    {
        string userFile = UserConfig();

        CliRun human = await Run(null, Env(), "whoami", "--profile", "dev");
        Assert.Equal(ExitCodes.Success, human.Exit);
        Assert.Contains($"Profile     : dev ({userFile})", human.Stdout);
        Assert.Contains("API host    : https://api.test/", human.Stdout);
        Assert.Contains("Tenant      : ten_dev", human.Stdout);
        Assert.DoesNotContain("key-for-dev", human.Stdout + human.Stderr);

        CliRun json = await Run(null, Env(), "whoami", "--profile", "dev", "--json");
        JsonElement root = JsonDocument.Parse(json.Stdout).RootElement;
        Assert.Equal("dev", root.GetProperty("profile").GetString());
        Assert.Equal(userFile, root.GetProperty("profileFile").GetString());
        Assert.Equal("ten_dev", root.GetProperty("tenant").GetString());
        Assert.True(root.GetProperty("apiKeySet").GetBoolean());
        Assert.DoesNotContain("key-for-dev", json.Stdout);

        // Uten profil er whoami som før, med profile null.
        CliRun plain = await Run(null, Env(), CliHarness.With("whoami", "--json"));
        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(plain.Stdout).RootElement.GetProperty("profile").ValueKind);
    }

    [Theory]
    [InlineData("QAK_dev.Pasted-Key")]
    [InlineData("qak_dev.pasted-key")]
    [InlineData("dev env")]
    public async Task A_profile_name_out_of_shape_is_a_usage_error_that_does_not_show_it(string name)
    {
        CliRun flag = await Run(null, Env(), "apply", "--file", _deploy, "--profile", name);
        Assert.Equal(ExitCodes.Usage, flag.Exit);
        Assert.Contains("--profile is not a profile name. Its value is not shown.", flag.Stderr);
        Assert.DoesNotContain(name, flag.Stdout + flag.Stderr);

        CliRun variable = await Run(null, Env(("QUEUEY_PROFILE", name)), "apply", "--file", _deploy);
        Assert.Equal(ExitCodes.Usage, variable.Exit);
        Assert.Contains("QUEUEY_PROFILE is not a profile name. Its value is not shown.", variable.Stderr);
        Assert.DoesNotContain(name, variable.Stdout + variable.Stderr);
    }
}
