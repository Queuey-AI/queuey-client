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

    public KeysCommandTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static RecordingHandler Minting(int status = 200) => new(req => req.Key switch
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
            clientPublicId = "hcl_1", clientName = "queuey-cli", keyId = "hk_new", secret = Secret, queuePublicId = "que_1",
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
        File.WriteAllText(env, "# app\nDATABASE_URL=postgres://localhost/app\nexport QUEUEY_SIGNING_KEY_ID=hk_old\nQUEUEY_SIGNING_SECRET='old-secret'\nPORT=5000\nQUEUEY_SIGNING_SECRET=dup\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(env, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        CliRun run = await Run(Minting(), "keys", "mint", "--queue", "que_1", "--write", env, "--json");

        Assert.True(run.Exit == ExitCodes.Success, run.Stdout + run.Stderr);
        Assert.Equal(
            "# app\nDATABASE_URL=postgres://localhost/app\nexport QUEUEY_SIGNING_KEY_ID=hk_new\nQUEUEY_SIGNING_SECRET=" + Secret + "\nPORT=5000\n",
            File.ReadAllText(env));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(env) & (UnixFileMode)0x1FF);

        JsonElement json = JsonDocument.Parse(run.Stdout).RootElement;
        Assert.Equal("hk_new", json.GetProperty("keyId").GetString());
        Assert.Equal("hk_old", json.GetProperty("replacedKeyId").GetString());
        Assert.False(json.TryGetProperty("secret", out _));
        Assert.DoesNotContain(Secret, run.Stdout + run.Stderr);
        Assert.Contains("queuey keys revoke hk_old", run.Stderr);
    }

    [Fact]
    public async Task A_new_file_outside_a_repository_is_readable_only_by_the_user_and_the_secret_is_never_printed()
    {
        string env = Path.Combine(_dir, ".env");

        CliRun run = await Run(Minting(), "keys", "mint", "--queue", "que_1", "--write", env);

        Assert.True(run.Exit == ExitCodes.Success, run.Stdout + run.Stderr);
        Assert.Equal($"QUEUEY_SIGNING_KEY_ID=hk_new\nQUEUEY_SIGNING_SECRET={Secret}\n", File.ReadAllText(env));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(env) & (UnixFileMode)0x1FF);
        Assert.Contains("Wrote QUEUEY_SIGNING_KEY_ID and QUEUEY_SIGNING_SECRET", run.Stdout);
        Assert.DoesNotContain(Secret, run.Stdout + run.Stderr);
    }

    [Fact]
    public async Task Without_write_the_key_is_shown_once_as_before_with_a_tip_to_write_it()
    {
        CliRun run = await Run(Minting(), "keys", "mint", "--queue", "que_1");

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.Contains($"SigningSecret = {Secret}", run.Stdout);
        Assert.Contains("--write .env", run.Stderr);
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
                new { clientPublicId = "hcl_1", clientName = "shop", clientIsActive = true, keyId = "hk_1", keyIsActive = true,
                      lastUsedAtUtc = (DateTimeOffset?)new DateTimeOffset(2026, 10, 9, 8, 0, 0, TimeSpan.Zero), revokedAtUtc = (DateTimeOffset?)null },
            }),
            "POST /hmacclients/hmac-signing-keys/hk_1/revoke" => RecordingHandler.NoContent(),
            _ => throw new InvalidOperationException(req.Key),
        });

        CliRun list = await Run(api, "keys", "list", "--queue", "que_1");
        Assert.Equal(ExitCodes.Success, list.Exit);
        Assert.Contains("hk_1  shop  active, last used 2026-10-09 08:00 UTC", list.Stdout);

        CliRun revoke = await Run(api, "keys", "revoke", "hk_1", "--reason", "leaked", "--json");
        Assert.Equal(ExitCodes.Success, revoke.Exit);
        Assert.True(JsonDocument.Parse(revoke.Stdout).RootElement.GetProperty("revoked").GetBoolean());
        Assert.Equal("leaked", api.Requests.Single(r => r.Method == HttpMethod.Post).Json.GetProperty("reason").GetString());
    }

    [Fact]
    public void Merging_keeps_windows_line_endings_and_quotes_a_value_that_needs_it()
    {
        string merged = EnvFile.Merge("A=1\r\nB=2", new[] { ("QUEUEY_SIGNING_SECRET", "a b$c") }, out _);

        Assert.Equal("A=1\r\nB=2\r\nQUEUEY_SIGNING_SECRET='a b$c'\r\n", merged);
    }
}
