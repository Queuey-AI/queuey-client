using Queuey.Client.Cli;

namespace Queuey.Client.Cli.Tests;

/// <summary>
/// Filene kommandolinjen navngir, og bare de, gir file_unreadable og file_unwritable (re-review 2026-10-05). Før gjorde
/// CliEntry hver IOException til file_unreadable.
/// </summary>
public sealed class CliFilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "queuey-cli-files-tests", Guid.NewGuid().ToString("N"));

    public CliFilesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void A_file_in_a_folder_that_does_not_exist_cannot_be_written()
    {
        string path = Path.Combine(_dir, "no-such-folder", "queuey.deploy.json");

        var ex = Assert.Throws<CliFileException>(() => CliFiles.WriteAllText(path, "{}"));

        Assert.Equal("file_unwritable", ex.Code);
        Assert.Contains("folder exists", ex.Action);
    }

    [Fact]
    public void A_file_without_read_permission_cannot_be_read()
    {
        if (OperatingSystem.IsWindows())
            return;

        string path = Path.Combine(_dir, "event.json");
        File.WriteAllText(path, "{}");
        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            var text = Assert.Throws<CliFileException>(() => CliFiles.ReadAllText(path));
            var bytes = Assert.Throws<CliFileException>(() => CliFiles.ReadAllBytes(path));

            Assert.Equal("file_unreadable", text.Code);
            Assert.Equal("file_unreadable", bytes.Code);
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
