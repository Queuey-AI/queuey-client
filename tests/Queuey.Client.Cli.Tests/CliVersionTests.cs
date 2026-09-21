using Queuey.Client.Cli;

namespace Queuey.Client.Cli.Tests;

/// <summary>
/// <c>queuey --version</c> is how an agent checks that an install worked, so
/// it prints the version people compare against NuGet and the release page —
/// never the SourceLink commit suffix, and never nothing.
/// </summary>
public sealed class CliVersionTests
{
    [Fact]
    public void The_commit_suffix_SourceLink_adds_is_left_off()
    {
        Assert.Equal("0.1.0-preview.8", CliVersion.Format("0.1.0-preview.8+120d20fd28ac0c6bd1aae86a10253b959cbd3f69"));
    }

    [Fact]
    public void A_version_without_build_metadata_is_printed_as_it_is()
    {
        Assert.Equal("0.1.0-preview.8", CliVersion.Format("0.1.0-preview.8"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_version_says_so_rather_than_printing_nothing(string? raw)
    {
        Assert.Equal("unknown", CliVersion.Format(raw));
    }

    [Fact]
    public void The_running_tool_has_a_version()
    {
        Assert.NotEqual("unknown", CliVersion.Current);
        Assert.DoesNotContain("+", CliVersion.Current, StringComparison.Ordinal);
    }
}
