using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Queuey.Client.Cli;

namespace Queuey.Client.Cli.Tests;

/// <summary>
/// Ukjente valg (review 2026-09-24). Før ignorerte hver kommando et valg den ikke kjente, så `apply
/// --plan` i en CLI fra før --plan ble en ekte apply, og det samme ble skrivefeilen `--paln`. Nå
/// deklarerer hver kommando valgene sine, og et ukjent valg gir exit 2 og lista over de gyldige.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class CommandOptionsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "queuey-options-cli-tests", Guid.NewGuid().ToString("N"));

    public CommandOptionsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string DeployFile(string json = """{ "tenant": "ten_abc", "workspace": { "retentionDays": 30 }, "queues": { "orders": { "maxAttempts": 5 } } }""")
    {
        string path = Path.Combine(_dir, "queuey.deploy.json");
        File.WriteAllText(path, json);
        return path;
    }

    /// <summary>Svarer på alt som en server med dry-run ville — og husker om noe ble sendt.</summary>
    private static RecordingHandler PlanningServer() => new(req => req switch
    {
        { Method.Method: "GET" } when req.Path.EndsWith("/queues", StringComparison.Ordinal)
            => RecordingHandler.Json(HttpStatusCode.OK, new[] { new { publicId = "que_orders", displayName = "orders", mode = "Deliver", hasDeliveryTarget = true } }),
        { Method.Method: "PUT", Path: "/queues" }
            => RecordingHandler.Json(HttpStatusCode.OK, new { dryRun = true, publicId = "que_orders", displayName = "orders", created = false, hasDeliveryTarget = true }),
        _ => RecordingHandler.Json(HttpStatusCode.OK, new { dryRun = true, target = "x", changes = Array.Empty<object>(), notes = Array.Empty<string>() }),
    });

    // ── skrivefeil og flyttede valg ──────────────────────────────────────────

    [Theory]
    [InlineData("apply")]
    [InlineData("plan")]
    public async Task A_typo_in_an_option_fails_with_the_valid_options_and_sends_nothing(string command)
    {
        RecordingHandler api = PlanningServer();

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With(command, "--file", DeployFile(), "--paln")), api);

        Assert.Equal(ExitCodes.Usage, run.Exit);
        Assert.Empty(api.Requests);
        Assert.Contains($"Unknown option --paln for queuey {command}.", run.Stderr);
        Assert.Contains($"Valid options for queuey {command}: --file", run.Stderr);
        Assert.Equal(string.Empty, run.Stdout);
    }

    [Fact]
    public async Task Apply_plan_fails_as_an_unknown_option_that_points_at_the_plan_verb()
    {
        // En eldre CLI kjører `apply --plan` som en ekte apply; denne sier hvor planen er flyttet.
        RecordingHandler api = PlanningServer();

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("apply", "--file", DeployFile(), "--plan")), api);

        Assert.Equal(ExitCodes.Usage, run.Exit);
        Assert.Empty(api.Requests);
        Assert.Contains("Unknown option --plan for queuey apply.", run.Stderr);
        Assert.Contains("`apply --plan` is now `queuey plan`", run.Stderr);
    }

    [Fact]
    public async Task Apply_plan_as_an_argument_fails_too()
    {
        RecordingHandler api = PlanningServer();

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("apply", "plan", "--file", DeployFile())), api);

        Assert.Equal(ExitCodes.Usage, run.Exit);
        Assert.Empty(api.Requests);
        Assert.Contains("Unexpected argument 'plan' for queuey apply.", run.Stderr);
        Assert.Contains("`queuey plan`", run.Stderr);
    }

    [Fact]
    public async Task With_json_an_unknown_option_is_a_json_error_on_stdout()
    {
        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("apply", "--file", DeployFile(), "--paln", "--json")));

        Assert.Equal(ExitCodes.Usage, run.Exit);
        Assert.Equal(string.Empty, run.Stderr);
        JsonElement error = JsonDocument.Parse(run.Stdout).RootElement.GetProperty("error");
        Assert.Equal("unknown_option", error.GetProperty("code").GetString());
        Assert.Equal("--paln", error.GetProperty("option").GetString());
        string[] valid = error.GetProperty("validOptions").EnumerateArray().Select(v => v.GetString()!).ToArray();
        Assert.Contains("--dry-run", valid);
        Assert.Contains("--tenant", valid);
        Assert.DoesNotContain("--plan", valid);
    }

    [Theory]
    [InlineData("--dry-run")]
    [InlineData("--check")]
    [InlineData("--continue-on-error")]
    public async Task Plan_takes_the_options_apply_takes_but_not_its_modes(string option)
    {
        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("plan", "--file", DeployFile(), option)));

        Assert.Equal(ExitCodes.Usage, run.Exit);
        Assert.Contains($"Unknown option {option} for queuey plan.", run.Stderr);
    }

    [Fact]
    public async Task Plan_is_a_verb_that_sends_every_write_as_a_dry_run()
    {
        RecordingHandler api = PlanningServer();

        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(CliHarness.With("plan", "--file", DeployFile(), "--json")), api);

        Assert.Equal(ExitCodes.Success, run.Exit);
        Assert.True(JsonDocument.Parse(run.Stdout).RootElement.GetProperty("wouldSucceed").GetBoolean());
        Assert.NotEmpty(api.Writes);
        Assert.All(api.Writes, w => Assert.Equal("?dryRun=true", w.Uri.Query));
    }

    // ── alle kommandoene ─────────────────────────────────────────────────────

    public static TheoryData<string[]> EveryCommand => new()
    {
        new[] { "advise" }, new[] { "whoami" }, new[] { "sync" }, new[] { "queue", "plan" }, new[] { "queue", "sync" },
        new[] { "apply" }, new[] { "plan" }, new[] { "verify", "orders" }, new[] { "schema" }, new[] { "pull" },
        new[] { "keys", "mint" }, new[] { "credentials", "set" }, new[] { "credentials", "list" },
        new[] { "publish", "orders" }, new[] { "create-tenant" }, new[] { "create-queue" }, new[] { "metrics" },
        new[] { "issues" }, new[] { "listen" }, new[] { "replay" },
        new[] { "edge", "status" }, new[] { "edge", "publish", "orders" }, new[] { "edge", "run" }, new[] { "edge", "kick" },
        new[] { "edge", "drain" }, new[] { "edge", "retry" }, new[] { "edge", "discard" }, new[] { "edge", "recover" },
        new[] { "edge", "reset" },
    };

    [Theory]
    [MemberData(nameof(EveryCommand))]
    public async Task Every_command_rejects_an_option_it_does_not_take(string[] command)
    {
        // Før noe annet: ingen fil leses, ingen nøkkel kreves og ingenting sendes.
        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(command.Append("--no-such-option").ToArray()));

        Assert.Equal(ExitCodes.Usage, run.Exit);
        Assert.Contains($"Unknown option --no-such-option for queuey {string.Join(" ", command.Where(w => w != "orders"))}.", run.Stderr);
    }

    [Theory]
    [InlineData("--json=true", true)]
    [InlineData("--json=TRUE", true)]
    [InlineData("--json=false", false)]
    public async Task A_switch_given_true_or_false_means_what_it_says(string json, bool asJson)
    {
        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[] { "apply", "--file", Path.Combine(_dir, "nope.json"), json }));

        Assert.Equal(ExitCodes.Usage, run.Exit);
        if (asJson)
            Assert.Equal("missing_file", JsonDocument.Parse(run.Stdout).RootElement.GetProperty("error").GetProperty("code").GetString());
        else
            Assert.Contains("No deployment file at", run.Stderr);
    }

    [Fact]
    public async Task A_switch_given_another_value_fails()
    {
        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[] { "apply", "--file", DeployFile(), "--dry-run=yes" }));

        Assert.Equal(ExitCodes.Usage, run.Exit);
        Assert.Contains("--dry-run is a switch for queuey apply", run.Stderr);
    }

    [Fact]
    public async Task The_retired_env_option_says_what_to_use_instead()
    {
        CliRun run = await CliHarness.RunAsync(() => CliEntry.RunAsync(new[] { "whoami", "--env", "development" }));

        Assert.Equal(ExitCodes.Usage, run.Exit);
        Assert.Contains("--api-base", run.Stderr);
    }

    /// <summary>
    /// Den andre veien: hvert valg brukstekstene nevner for en kommando, tar kommandoen imot. Ellers kunne
    /// en for streng deklarasjon stille gjort et dokumentert valg til en feil.
    /// </summary>
    [Fact]
    public void Every_option_the_usage_text_documents_is_accepted_by_its_command()
    {
        var sections = new Dictionary<string, CommandOptions[]>(StringComparer.Ordinal)
        {
            ["ADVISE"] = new[] { AdviseCommand.Options },
            ["SYNC"] = new[] { SyncCommand.Options },
            ["QUEUE"] = new[] { QueueCommand.PlanOptions, QueueCommand.SyncOptions },
            ["APPLY"] = new[] { ApplyCommand.Options },
            ["PLAN"] = new[] { PlanCommand.Options },
            ["VERIFY"] = new[] { VerifyCommand.Options },
            ["SCHEMA"] = new[] { SchemaCommand.Options },
            ["PULL"] = new[] { PullCommand.Options },
            ["KEYS"] = new[] { KeysCommand.MintOptions },
            ["CREDENTIALS"] = new[] { CredentialsCommand.SetOptions, CredentialsCommand.ListOptions },
            ["PUBLISH"] = new[] { PublishCommand.Options },
            ["CREATE-TENANT"] = new[] { CreateTenantCommand.Options },
            ["CREATE-QUEUE"] = new[] { CreateQueueCommand.Options },
            ["METRICS"] = new[] { MetricsCommand.Options },
            ["ISSUES"] = new[] { IssuesCommand.Options },
            ["LISTEN"] = new[] { ListenCommand.Options },
            ["REPLAY"] = new[] { ReplayCommand.Options },
            ["EDGE"] = EdgeCommand.Verbs.Values.ToArray(),
            ["WHOAMI"] = new[] { WhoAmICommand.Options },
        };

        var missing = new List<string>();
        foreach ((string section, CommandOptions[] commands) in sections)
        {
            foreach (string option in OptionsIn(Section(section)))
            {
                if (!commands.Any(c => c.Accepts(option)))
                    missing.Add($"{section}: --{option}");
            }
        }

        Assert.True(missing.Count == 0, "documented but rejected: " + string.Join(", ", missing));
    }

    private static string Section(string name)
    {
        string text = Usage.Text.Replace("\r\n", "\n");
        int start = text.IndexOf("\n" + name + "\n", StringComparison.Ordinal);
        Assert.True(start >= 0, $"no {name} section in the usage text");
        int end = Regex.Match(text[(start + name.Length + 2)..], @"\n[A-Z][A-Z -]+\n").Index;
        return text.Substring(start, end + name.Length + 2);
    }

    private static IEnumerable<string> OptionsIn(string text)
        => Regex.Matches(text, @"(?<![\w-])--([a-z][a-z-]*)").Select(m => m.Groups[1].Value).Distinct();
}
