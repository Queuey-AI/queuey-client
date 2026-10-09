using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Queuey.Client.Cli;

namespace Queuey.Client.Cli.Tests;

/// <summary>
/// queuey keys (2026-10-09): mint med --write .env, som skriver nøkkelen i en fil git ignorerer og aldri viser hemmeligheten,
/// 202 når en person skal avgjøre, og list og revoke.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class KeysCommandTests : IDisposable
{
    private const string Secret = "s3cr3t-Signing-Value";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "queuey-keys-tests", Guid.NewGuid().ToString("N"));

    private readonly Func<string, string[], (int Exit, string Output)?> _git = EnvFile.Git;

    public KeysCommandTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        EnvFile.Git = _git;
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static RecordingHandler Minting(int status = 200, string secret = Secret) => new(req => req.Key switch
    {
        "POST /hmacclients/queues/que_1" when status == 202 => RecordingHandler.Json(HttpStatusCode.Accepted, new
        {
            status = "pending_approval",
            approvalUrl = "https://app.test/inbox/op_1",
            expiresAt = "2026-10-10T12:00:00Z",
            policyRule = "keys.prod",
            message = "A person approves new signing keys for a prod workspace.",
        }),
        "POST /hmacclients/queues/que_1" => RecordingHandler.Json(HttpStatusCode.OK, new
        {
            clientPublicId = "hcl_1", clientName = "queuey-cli", keyId = "hsk_01NEW", secret, queuePublicId = "que_1",
        }),
        _ => throw new InvalidOperationException(req.Key),
    });

    private static Task<CliRun> Run(RecordingHandler api, params string[] args)
        => CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With(args)), api);

    private void Git(params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = _dir, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in args)
            start.ArgumentList.Add(arg);
        using Process git = Process.Start(start)!;
        git.WaitForExit();
        Assert.Equal(0, git.ExitCode);
    }

    [Fact]
    public async Task A_file_in_a_git_repository_that_git_does_not_ignore_gets_no_secret_and_nothing_is_minted()
    {
        Git("init", "-q");
        RecordingHandler api = Minting();

        CliRun run = await Run(api, "keys", "mint", "--queue", "que_1", "--write", Path.Combine(_dir, ".env"));

        Assert.Equal(ExitCodes.Configuration, run.Exit);
        Assert.Contains("is in a git repository, and git does not ignore it. Nothing was minted.", run.Stderr);
        Assert.Contains(".gitignore", run.Stderr);
        Assert.Empty(api.Requests);
        Assert.False(File.Exists(Path.Combine(_dir, ".env")));
    }

    [Fact]
    public async Task An_ignored_env_file_gets_the_key_merged_in_and_keeps_every_other_line_and_its_mode()
    {
        Git("init", "-q");
        File.WriteAllText(Path.Combine(_dir, ".gitignore"), ".env\n");
        string env = Path.Combine(_dir, ".env");
        File.WriteAllText(env, "# app\nDATABASE_URL=postgres://localhost/app\nexport QUEUEY_SIGNING_KEY_ID=hsk_01OLD\nQUEUEY_SIGNING_SECRET='old-secret'\nPORT=5000\nQUEUEY_SIGNING_SECRET=dup\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(env, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        CliRun run = await Run(Minting(), "keys", "mint", "--queue", "que_1", "--write", env, "--json");

        Assert.True(run.Exit == ExitCodes.Success, run.Stdout + run.Stderr);
        Assert.Equal(
            "# app\nDATABASE_URL=postgres://localhost/app\nexport QUEUEY_SIGNING_KEY_ID=hsk_01NEW\nQUEUEY_SIGNING_SECRET=" + Secret + "\nPORT=5000\n",
            File.ReadAllText(env));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(env) & (UnixFileMode)0x1FF);

        JsonElement json = JsonDocument.Parse(run.Stdout).RootElement;
        Assert.Equal("hsk_01NEW", json.GetProperty("keyId").GetString());
        Assert.Equal("hsk_01OLD", json.GetProperty("replacedKeyId").GetString());
        if (!OperatingSystem.IsWindows())
        {
            // Security-review av #67 (BØR A): en .env andre kunne lese, er strammet til 0600, og svaret sier fra hva.
            Assert.Equal("0644", json.GetProperty("tightenedFrom").GetString());
            Assert.Contains("had mode 0644; it is 0600 now", run.Stderr);
        }
        Assert.False(json.TryGetProperty("secret", out _));
        Assert.DoesNotContain(Secret, run.Stdout + run.Stderr);
        Assert.Contains("queuey keys revoke hsk_01OLD", run.Stderr);
    }

    [Fact]
    public async Task A_new_file_outside_a_repository_is_readable_only_by_the_user_and_the_secret_is_never_printed()
    {
        string env = Path.Combine(_dir, ".env");

        CliRun run = await Run(Minting(), "keys", "mint", "--queue", "que_1", "--write", env);

        Assert.True(run.Exit == ExitCodes.Success, run.Stdout + run.Stderr);
        Assert.Equal($"QUEUEY_SIGNING_KEY_ID=hsk_01NEW\nQUEUEY_SIGNING_SECRET={Secret}\n", File.ReadAllText(env));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(env) & (UnixFileMode)0x1FF);
        Assert.Contains("Wrote QUEUEY_SIGNING_KEY_ID and QUEUEY_SIGNING_SECRET", run.Stdout);
        Assert.DoesNotContain(Secret, run.Stdout + run.Stderr);
    }

    [Fact]
    public async Task Without_write_nothing_is_minted_and_the_answer_names_the_variables_and_where_a_person_sees_the_key()
    {
        // Kenneth 2026-10-09: hemmeligheten skal ikke stå i terminalen. Uten --write mintes ingenting.
        var api = new RecordingHandler(req => throw new InvalidOperationException("Nothing is minted: " + req.Key));

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[]
        {
            "keys", "mint", "--queue", "que_1", "--tenant", "ten_1", "--api-key", "qak_kid.secret", "--license", "lic_1",
            "--config", Path.Combine(_dir, "none.json"), "--json",
        }), api);

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Empty(api.Requests);
        JsonElement json = JsonDocument.Parse(run.Stdout).RootElement;
        Assert.False(json.GetProperty("minted").GetBoolean());
        Assert.Equal(new[] { "QUEUEY_SIGNING_KEY_ID", "QUEUEY_SIGNING_SECRET" }, json.GetProperty("variables").EnumerateArray().Select(v => v.GetString()).ToArray());
        Assert.Equal("https://app.queuey.ai/console/t/ten_1/q/que_1?panel=security", json.GetProperty("consoleUrl").GetString());
        Assert.Contains("--write .env", json.GetProperty("suggested").GetString());
    }

    [Fact]
    public async Task Show_secret_is_the_one_way_to_the_secret_in_the_terminal_and_it_warns()
    {
        CliRun run = await Run(Minting(), "keys", "mint", "--queue", "que_1", "--show-secret");

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Contains($"QUEUEY_SIGNING_SECRET = {Secret}", run.Stdout);
        Assert.Contains("Warning: --show-secret prints the secret", run.Stderr);
    }

    [Fact]
    public async Task A_mint_queuey_gives_to_a_person_exits_5_with_the_link_and_writes_nothing()
    {
        string env = Path.Combine(_dir, ".env");

        CliRun run = await Run(Minting(status: 202), "keys", "mint", "--queue", "que_1", "--write", env, "--json");

        Assert.Equal(ExitCodes.PendingApproval, run.Exit);
        JsonElement json = JsonDocument.Parse(run.Stdout).RootElement;
        Assert.Equal("pending_approval", json.GetProperty("status").GetString());
        Assert.Equal("https://app.test/inbox/op_1", json.GetProperty("approvalUrl").GetString());
        Assert.False(File.Exists(env));
    }

    [Fact]
    public async Task Mint_works_with_a_login_as_a_bearer()
    {
        string home = Path.Combine(_dir, "home");
        Directory.CreateDirectory(home);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        LoginStore.Write(Path.Combine(home, "credentials.json"), new CredentialsFile
        {
            Logins =
            {
                new StoredLogin
                {
                    ApiBase = "https://api.test", License = "lic_1", Scope = "operate", AccessToken = "at-opaque",
                    AccessTokenExpiresAt = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(30), RefreshToken = "rt-opaque",
                },
            },
        });
        RecordingHandler api = Minting();

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[]
        {
            "keys", "mint", "--queue", "que_1", "--write", Path.Combine(_dir, ".env"), "--api-base", "https://api.test",
            "--config", Path.Combine(_dir, "no-queuey.json"),
        }), api, new Dictionary<string, string> { [UserProfiles.PathVariable] = Path.Combine(home, "config.json") });

        Assert.True(run.Exit == ExitCodes.Success, run.Stdout + run.Stderr);
        Assert.Equal("Bearer at-opaque", Assert.Single(api.Headers)["Authorization"]);
        Assert.Equal("lic_1", api.Headers[0]["X-License-PublicId"]);
    }

    [Fact]
    public async Task List_shows_each_key_without_a_secret_and_revoke_names_the_key()
    {
        var api = new RecordingHandler(req => req.Key switch
        {
            "GET /hmacclients/queues/que_1" => RecordingHandler.Json(HttpStatusCode.OK, new[]
            {
                new { clientPublicId = "hcl_1", clientName = "shop", clientIsActive = true, keyId = "hsk_01A", keyIsActive = true,
                      lastUsedAtUtc = (DateTimeOffset?)new DateTimeOffset(2026, 10, 9, 8, 0, 0, TimeSpan.Zero), revokedAtUtc = (DateTimeOffset?)null },
            }),
            "POST /hmacclients/hmac-signing-keys/hsk_01A/revoke" => RecordingHandler.NoContent(),
            _ => throw new InvalidOperationException(req.Key),
        });

        CliRun list = await Run(api, "keys", "list", "--queue", "que_1");
        Assert.Equal(ExitCodes.Success, list.Exit);
        Assert.Contains("hsk_01A  shop  active, last used 2026-10-09 08:00 UTC", list.Stdout);

        CliRun revoke = await Run(api, "keys", "revoke", "hsk_01A", "--reason", "leaked", "--json");
        Assert.Equal(ExitCodes.Success, revoke.Exit);
        Assert.True(JsonDocument.Parse(revoke.Stdout).RootElement.GetProperty("revoked").GetBoolean());
        Assert.Equal("leaked", api.Requests.Single(r => r.Method == HttpMethod.Post).Json.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task When_git_fails_inside_a_repository_folder_nothing_is_minted()
    {
        // Security-review av #67 (BØR B): git som feiler (safe.directory, en ødelagt .git), er ikke det samme som «ikke et repo».
        Directory.CreateDirectory(Path.Combine(_dir, ".git"));
        EnvFile.Git = (_, _) => (128, "");
        RecordingHandler api = Minting();

        CliRun run = await Run(api, "keys", "mint", "--queue", "que_1", "--write", Path.Combine(_dir, ".env"));

        Assert.Equal(ExitCodes.Configuration, run.Exit);
        Assert.Contains("Nothing was minted.", run.Stderr);
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task A_link_or_a_file_this_user_cannot_read_is_refused_before_anything_is_minted()
    {
        if (OperatingSystem.IsWindows())
            return;
        string real = Path.Combine(_dir, "real.env"), link = Path.Combine(_dir, ".env");
        File.WriteAllText(real, "A=1\n");
        File.CreateSymbolicLink(link, real);
        RecordingHandler api = Minting();

        CliRun linked = await Run(api, "keys", "mint", "--queue", "que_1", "--write", link);
        Assert.Equal(ExitCodes.Usage, linked.Exit);
        Assert.Contains("which is a link", linked.Stderr);

        string locked = Path.Combine(_dir, "locked.env");
        File.WriteAllText(locked, "A=1\n");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        if (UserProfiles.CurrentUser() != 0) // root leser alt
        {
            CliRun unreadable = await Run(api, "keys", "mint", "--queue", "que_1", "--write", locked);
            Assert.Equal(ExitCodes.Usage, unreadable.Exit);
            Assert.Contains("cannot read and write", unreadable.Stderr);
        }

        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task A_secret_with_characters_a_dot_env_cannot_hold_is_never_written_or_shown()
    {
        // Security-review av #67 (KAN C): et linjeskift i svaret kunne lagt til linjer i .env.
        string env = Path.Combine(_dir, ".env");

        CliRun run = await Run(Minting(secret: "abc\nQUEUEY_API_KEY=qak_evil"), "keys", "mint", "--queue", "que_1", "--write", env);

        Assert.Equal(ExitCodes.RuntimeError, run.Exit);
        Assert.Contains("cannot hold safely", run.Stderr);
        Assert.False(File.Exists(env));
        Assert.DoesNotContain("qak_evil", run.Stdout + run.Stderr);
    }

    [Theory]
    [InlineData("qak_kid.secret")]
    [InlineData("hk_short")]
    [InlineData("hsk_with.dot")]
    public async Task Revoke_takes_only_a_signing_key_id_and_never_shows_anything_else(string value)
    {
        var api = new RecordingHandler(_ => throw new InvalidOperationException("Nothing is sent."));

        CliRun run = await Run(api, "keys", "revoke", value);

        Assert.Equal(ExitCodes.Usage, run.Exit);
        Assert.Contains("takes a signing key's id (hsk_…)", run.Stderr);
        Assert.DoesNotContain(value, run.Stdout + run.Stderr);
    }

    [Fact]
    public void Merging_keeps_windows_line_endings_and_quotes_a_value_that_needs_it()
    {
        string merged = EnvFile.Merge("A=1\r\nB=2", new[] { ("QUEUEY_SIGNING_SECRET", "a b$c") }, out _);

        Assert.Equal("A=1\r\nB=2\r\nQUEUEY_SIGNING_SECRET='a b$c'\r\n", merged);
    }
}
