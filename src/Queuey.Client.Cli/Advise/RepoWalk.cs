using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Queuey.Client.Cli.Advise;

/// <summary>
/// How much one scan of a repository may take: files, folders, bytes read, time, and the length of a line it matches. A
/// scan that reaches a limit stops there and says so, rather than reading on.
/// </summary>
/// <param name="MaxFiles">Files it opens, at most.</param>
/// <param name="MaxDirectories">Folders it lists, at most.</param>
/// <param name="MaxFileBytes">The largest file it reads; a larger one is skipped.</param>
/// <param name="MaxTotalBytes">Bytes it reads in all.</param>
/// <param name="Deadline">How long it may take.</param>
/// <param name="MaxLineLength">The longest line it matches; a longer one, such as minified code, is skipped.</param>
/// <param name="MaxEntriesPerDirectory">
/// Entries it takes from one folder, at most, in the order the file system lists them. A larger folder is read in part.
/// </param>
public sealed record ScanBudget(
    int MaxFiles, int MaxDirectories, int MaxFileBytes, long MaxTotalBytes, TimeSpan Deadline, int MaxLineLength,
    int MaxEntriesPerDirectory = 10_000)
{
    public static ScanBudget Default { get; } = new(
        MaxFiles: 6000,
        MaxDirectories: 20_000,
        MaxFileBytes: 512 * 1024,
        MaxTotalBytes: 64L * 1024 * 1024,
        Deadline: TimeSpan.FromSeconds(15),
        MaxLineLength: 4096,
        MaxEntriesPerDirectory: 10_000);
}

/// <summary>
/// Walks a repository the way advise may read it: never through a symbolic link, only regular files, each read with a
/// bound whatever its length says, and within a <see cref="ScanBudget"/>. What it leaves out, it says in
/// <see cref="Limits"/>.
/// </summary>
/// <remarks>
/// A cloned repository can hold links that point anywhere: to <c>/dev/zero</c>, which reads forever; to its own folder,
/// twice, which branches without end; to the home folder, which would scan someone's files into the evidence. None of
/// them is followed, not even a link that stays inside the repository. A file of zero length is not opened either: that
/// is an empty file, or a pipe or a device, where opening can wait for ever.
/// </remarks>
internal sealed class RepoWalk
{
    private readonly string _root;
    private readonly ScanBudget _budget;
    private readonly Func<string, bool> _skipDirectory;
    private readonly Func<string, bool> _interesting;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<string> _stops = new();

    private long _bytes;
    private int _directories;
    private int _files;
    private int _links;
    private int _unsafeNames;
    private int _large;
    private int _partFolders;

    /// <param name="root">The repository's folder.</param>
    /// <param name="budget">What the walk may take.</param>
    /// <param name="skipDirectory">Whether to leave a folder out, by its name.</param>
    /// <param name="interesting">Whether to open a file, by its name.</param>
    public RepoWalk(string root, ScanBudget budget, Func<string, bool> skipDirectory, Func<string, bool> interesting)
    {
        _root = Path.GetFullPath(root);
        _budget = budget;
        _skipDirectory = skipDirectory;
        _interesting = interesting;
    }

    /// <summary>True once the deadline has passed; the walk says so in <see cref="Limits"/>.</summary>
    public bool Expired
    {
        get
        {
            if (_clock.Elapsed <= _budget.Deadline)
                return false;
            Stop($"it stopped after {Seconds(_budget.Deadline)}, the longest a scan may take");
            return true;
        }
    }

    /// <summary>
    /// The files to read, in a stable order: regular files the predicate wants, under folders it does not skip, never
    /// through a link. Stops at the budget's files, folders or deadline.
    /// </summary>
    public IEnumerable<string> Files()
    {
        var stack = new Stack<string>();
        stack.Push(_root);

        while (stack.Count > 0)
        {
            if (Expired)
                yield break;

            string dir = stack.Pop();
            if (++_directories > _budget.MaxDirectories)
            {
                Stop($"it listed {_budget.MaxDirectories} folders, the most a scan lists");
                yield break;
            }

            List<FileSystemInfo> entries = List(dir);
            if (Expired)
                yield break;

            // Ordnet etter navn, så det som leses, kommer i samme rekkefølge hver gang.
            entries.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            var folders = new List<string>();
            foreach (FileSystemInfo entry in entries)
            {
                if (Expired)
                    yield break;

                if (IsLink(entry))
                {
                    _links++;
                    continue;
                }

                // Et navn med kontrolltegn eller retningsoverstyring kan få terminalen til å vise noe annet enn det står.
                if (TerminalText.HasUnsafeCharacters(entry.Name))
                {
                    _unsafeNames++;
                    continue;
                }

                if (entry is DirectoryInfo)
                {
                    if (!_skipDirectory(entry.Name))
                        folders.Add(entry.FullName);
                    continue;
                }

                if (!_interesting(entry.Name))
                    continue;

                if (++_files > _budget.MaxFiles)
                {
                    Stop($"it opened {_budget.MaxFiles} files, the most a scan opens");
                    yield break;
                }

                yield return entry.FullName;
            }

            for (int i = folders.Count - 1; i >= 0; i--)
                stack.Push(folders[i]);
        }
    }

    /// <summary>
    /// A folder's entries, streamed rather than loaded whole: at most the budget's entries per folder, with the deadline
    /// checked while they come. A folder with more is read in part, which <see cref="Limits"/> says.
    /// </summary>
    /// <remarks>
    /// Which entries a folder over the limit gives depends on the order the file system lists them in; those that are read
    /// are then sorted, so the walk is the same every time for the same folder. The limit is far above any source folder.
    /// </remarks>
    private List<FileSystemInfo> List(string dir)
    {
        var entries = new List<FileSystemInfo>();
        try
        {
            foreach (FileSystemInfo entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
            {
                if (entries.Count >= _budget.MaxEntriesPerDirectory)
                {
                    _partFolders++;
                    break;
                }
                if (entries.Count % 256 == 0 && Expired)
                    break;
                entries.Add(entry);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // En mappe som ikke kan listes, hoppes over; det som kom før feilen, brukes.
        }
        return entries;
    }

    /// <summary>
    /// The text of a regular file, read with a bound: the empty string for a link, an empty file, a file larger than the
    /// budget allows, one that cannot be read, or once the budget is spent.
    /// </summary>
    public string Read(string path)
    {
        if (Expired)
            return string.Empty;

        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (IsLink(info) || !info.Exists || info.Length == 0)
                return string.Empty;
            if (info.Length > _budget.MaxFileBytes)
            {
                _large++;
                return string.Empty;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return string.Empty;
        }

        long left = _budget.MaxTotalBytes - _bytes;
        if (left <= 0)
        {
            Stop($"it read {Size(_budget.MaxTotalBytes)}, the most a scan reads");
            return string.Empty;
        }

        int cap = (int)Math.Min(_budget.MaxFileBytes, left);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 16 * 1024,
                FileOptions.SequentialScan);
            byte[] buffer = new byte[cap + 1];
            int read = 0;
            while (read < buffer.Length)
            {
                int n = stream.Read(buffer, read, buffer.Length - read);
                if (n == 0)
                    break;
                read += n;
                if (Expired)
                    return string.Empty;
            }

            _bytes += read;
            if (read > cap)
            {
                // Lengre enn lengden sa, eller over det som er igjen: lest til grensen og forkastet.
                if (cap < _budget.MaxFileBytes)
                    Stop($"it read {Size(_budget.MaxTotalBytes)}, the most a scan reads");
                else
                    _large++;
                return string.Empty;
            }

            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(buffer, 0, read).TrimStart('﻿');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// What the walk left out, in words: the limit it stopped at, and the links, unsafe names and large files it skipped.
    /// Empty when it read everything it wanted.
    /// </summary>
    public IReadOnlyList<string> Limits
    {
        get
        {
            var limits = new List<string>(_stops);
            if (_links > 0)
                limits.Add($"{Count(_links, "symbolic link")} not followed: advise never follows a link, not even inside the repository");
            if (_unsafeNames > 0)
                limits.Add($"{Count(_unsafeNames, "path")} with control or direction characters in the name skipped");
            if (_large > 0)
                limits.Add($"{Count(_large, "file")} larger than {Size(_budget.MaxFileBytes)} skipped");
            if (_partFolders > 0)
                limits.Add($"{Count(_partFolders, "folder")} with more than {_budget.MaxEntriesPerDirectory} entries read in part, " +
                           "the first the file system listed");
            return limits;
        }
    }

    /// <summary>
    /// The text of one file, read to <paramref name="max"/> bytes and no further whatever its length says, or null when it
    /// holds more. Outside any budget: for the one file a caller names. An I/O or access error goes to the caller.
    /// </summary>
    internal static string? ReadBounded(string path, int max)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 16 * 1024,
            FileOptions.SequentialScan);
        byte[] buffer = new byte[max + 1];
        int read = 0;
        while (read < buffer.Length)
        {
            int n = stream.Read(buffer, read, buffer.Length - read);
            if (n == 0)
                break;
            read += n;
        }
        return read > max ? null : new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(buffer, 0, read).TrimStart('\uFEFF');
    }

    /// <summary>A size in the units a person reads: bytes, KiB or MiB.</summary>
    internal static string SizeText(long bytes) => Size(bytes);

    private void Stop(string reason)
    {
        if (!_stops.Contains(reason))
            _stops.Add(reason);
    }

    /// <summary>
    /// Whether an entry is a link, a symbolic link or a junction, read on the entry itself and never through it. Another
    /// reparse point, such as a OneDrive placeholder, is the user's file, and is read (review of #56, K-2).
    /// </summary>
    internal static bool IsLink(FileSystemInfo entry)
    {
        try
        {
            return entry.LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;   // det som ikke kan sjekkes, følges ikke
        }
    }

    private static string Count(int n, string what) => n == 1 ? $"1 {what}" : $"{n} {what}s";

    private static string Size(long bytes) => bytes >= 1024 * 1024 && bytes % (1024 * 1024) == 0
        ? $"{bytes / (1024 * 1024)} MiB"
        : bytes >= 1024 && bytes % 1024 == 0 ? $"{bytes / 1024} KiB" : $"{bytes} bytes";

    private static string Seconds(TimeSpan span) => span.TotalSeconds >= 1
        ? $"{span.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} s"
        : $"{span.TotalMilliseconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture)} ms";

    /// <summary>
    /// The limits two scans of the same repository reached, each once. The two skip different folders, so a count can
    /// differ ("3 symbolic links" and "4 symbolic links"): the larger one is kept.
    /// </summary>
    internal static IReadOnlyList<string> Union(params IReadOnlyList<string>[] limits)
    {
        var kept = new List<(string Key, int Count, string Text)>();
        foreach (string limit in limits.SelectMany(l => l))
        {
            int space = limit.IndexOf(' ');
            bool counted = space > 0 && int.TryParse(limit[..space], NumberStyles.None, CultureInfo.InvariantCulture, out _);
            string rest = counted ? Normalized(limit[(space + 1)..]) : limit;
            int count = counted ? int.Parse(limit[..space], CultureInfo.InvariantCulture) : 0;
            int at = kept.FindIndex(k => k.Key == rest);
            if (at < 0)
                kept.Add((rest, count, limit));
            else if (count > kept[at].Count)
                kept[at] = (rest, count, limit);
        }
        return kept.Select(k => k.Text).ToArray();

        // "1 path" og "2 paths" er samme grense.
        static string Normalized(string rest)
        {
            int space = rest.IndexOf(' ');
            string first = space < 0 ? rest : rest[..space];
            return (first.EndsWith("s", StringComparison.Ordinal) ? first[..^1] : first) + (space < 0 ? "" : rest[space..]);
        }
    }
}
