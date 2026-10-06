using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Queuey.Client.Waas.Tests;

/// <summary>
/// En deploy-fil får ikke lese CLI-ens egne QUEUEY_-innstillinger, men navn som slutter på _URL eller _DELIVERY_KIND er
/// unntatt, fordi `pull --as` skriver dem (DeploymentVariables, herding før tag 2026-10-06). Derfor får en innstilling aldri
/// et navn med en av de endelsene. Denne testen leser kildekoden og holder den til det, så en ny innstilling som
/// QUEUEY_WEBHOOK_URL feiler her og ikke i en deploy-fil.
/// </summary>
public class QueueySettingsTests
{
    // Navnene Queuey sine egne verktøy skriver inn i en deploy-fil (DeploymentTemplate), og workspacet, som er en offentlig
    // id. Kildekoden nevner dem som navn, og en fil skal kunne lese dem.
    private static readonly string[] FileVariables =
    {
        "QUEUEY_TENANT", DeploymentTemplate.EnvironmentVariable, DeploymentTemplate.BaseUrlVariable,
    };

    [Fact]
    public void Every_queuey_setting_the_source_reads_is_one_a_deployment_file_may_not_read()
    {
        string[] names = SettingsInTheSource();

        // Vakt mot at lesingen stille finner ingenting, så testen består uten å ha sjekket noe.
        Assert.Contains("QUEUEY_API_KEY", names);
        Assert.Contains("QUEUEY_USER_CONFIG", names);

        string[] readable = names.Where(n => DeploymentVariables.RefusalOf(n) is null).Except(FileVariables).ToArray();
        Assert.True(readable.Length == 0,
            $"A deployment file could read {string.Join(", ", readable)}. A setting of the CLI's own must not end in _URL or " +
            "_DELIVERY_KIND, which DeploymentVariables lets a file read because pull --as writes such names.");
    }

    /// <summary>
    /// Each QUEUEY_ name the source reads or names on its own: an argument (<c>getEnv("QUEUEY_API_KEY")</c>) or a value
    /// (<c>PathVariable = "QUEUEY_USER_CONFIG"</c>). A name inside a longer text, as in a help text, is not one.
    /// </summary>
    private static string[] SettingsInTheSource()
    {
        string src = Path.Combine(RepoRoot(), "src");
        var setting = new Regex(@"(?<=[(,=]\s*)""(QUEUEY_[A-Z0-9_]+)""");

        return Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => setting.Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
    }

    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Queuey.Client.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find the repository root (Queuey.Client.sln).");
    }
}
