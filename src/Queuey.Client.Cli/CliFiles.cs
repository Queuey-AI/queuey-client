using System;
using System.IO;

namespace Queuey.Client.Cli;

/// <summary>
/// Reads and writes the files a command line names — a deployment file, an event, queuey.json, a pulled file. One that
/// cannot be read or written (a directory, a missing permission, a missing folder) becomes a <see cref="CliFileException"/>,
/// which <see cref="CliEntry"/> reports with exit 3 instead of a stack trace.
/// </summary>
// Re-review 2026-10-05: CliEntry gjorde hver IOException til file_unreadable, også en som ikke gjaldt en fil brukeren hadde
// navngitt. Nå er det bare lesingene og skrivingene her som blir file_unreadable og file_unwritable.
internal static class CliFiles
{
    public static string ReadAllText(string path) => Read(path, File.ReadAllText);

    public static byte[] ReadAllBytes(string path) => Read(path, File.ReadAllBytes);

    public static void WriteAllText(string path, string contents)
    {
        try
        {
            File.WriteAllText(path, contents);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CliFileException("file_unwritable", ex.Message,
                "Check that the folder exists and that this user may write the file there.", ex);
        }
    }

    private static T Read<T>(string path, Func<string, T> read)
    {
        try
        {
            return read(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CliFileException("file_unreadable", ex.Message, "Check that the path names a file this user can read.", ex);
        }
    }
}

/// <summary>A file the command line named could not be read or written. <see cref="Code"/> is the error code to report.</summary>
internal sealed class CliFileException : Exception
{
    public CliFileException(string code, string message, string action, Exception inner)
        : base(message, inner)
    {
        Code = code;
        Action = action;
    }

    /// <summary><c>file_unreadable</c> or <c>file_unwritable</c>.</summary>
    public string Code { get; }

    /// <summary>What to check.</summary>
    public string Action { get; }
}
