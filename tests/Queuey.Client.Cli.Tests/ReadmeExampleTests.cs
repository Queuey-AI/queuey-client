using System.Text.RegularExpressions;
using Queuey.Client.Cli;

namespace Queuey.Client.Cli.Tests;

/// <summary>
/// Eksemplene på queuey.deploy.json i README-en er det en agent eller en person kopierer først. Det første
/// av dem hadde en flat <c>workspace.baseUrl</c>, som parseren avviser, uten at noe fanget det (funnet
/// 2026-10-05). Hvert eksempel kjøres derfor gjennom <c>apply --dry-run</c>, slik en bruker ville gjort.
/// </summary>
[Collection(ConsoleCollection.Name)]
public sealed class ReadmeExampleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "queuey-readme-tests", Guid.NewGuid().ToString("N"));

    public ReadmeExampleTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>The line each deployment-file example starts on, for the test's name.</summary>
    public static TheoryData<int> ExampleLines()
    {
        var lines = new TheoryData<int>();
        foreach (int line in DeploymentFileExamples().Keys)
            lines.Add(line);
        return lines;
    }

    [Fact]
    public void The_readme_has_deployment_file_examples_to_check()
    {
        // Vakt mot at uttrekket stille finner ingenting, så teorien under består uten å ha sjekket noe.
        Assert.True(DeploymentFileExamples().Count >= 3, "Expected the README's deployment-file examples to be found.");
    }

    [Theory]
    [MemberData(nameof(ExampleLines))]
    public async Task Every_deployment_file_in_the_readme_passes_a_dry_run(int line)
    {
        string path = Path.Combine(_dir, "queuey.deploy.json");
        File.WriteAllText(path, DeploymentFileExamples()[line]);

        CliRun run;
        try
        {
            run = await CliHarness.RunAsync(() => ApplyCommand.RunAsync(new[] { "--file", path, "--dry-run" }));
        }
        catch (QueueyConfigurationException ex)
        {
            Assert.Fail($"README.md line {line}: {ex.Message}");
            return;
        }

        Assert.True(run.Exit == ExitCodes.Success, $"README.md line {line}: exit {run.Exit}. {run.Stdout}{run.Stderr}");
        Assert.Contains("Nothing was sent.", run.Stdout);
    }

    /// <summary>
    /// Each <c>json</c> or <c>jsonc</c> block in README.md that is a deployment file, by the line its
    /// fence is on. Output examples (a dry run's JSON carries <c>schemaVersion</c>) are not files.
    /// </summary>
    private static Dictionary<int, string> DeploymentFileExamples()
    {
        string readme = File.ReadAllText(Path.Combine(RepoRoot(), "README.md")).Replace("\r\n", "\n");
        var examples = new Dictionary<int, string>();

        foreach (Match block in Regex.Matches(readme, "^```jsonc?\n(.*?)^```", RegexOptions.Multiline | RegexOptions.Singleline))
        {
            string body = block.Groups[1].Value;
            bool isFile = body.Contains("\"queues\"") || body.Contains("\"workspace\"");
            if (isFile && !body.Contains("\"schemaVersion\""))
                examples[readme[..block.Index].Count(c => c == '\n') + 1] = body;
        }

        return examples;
    }

    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Queuey.Client.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find the repository root (Queuey.Client.sln).");
    }
}
