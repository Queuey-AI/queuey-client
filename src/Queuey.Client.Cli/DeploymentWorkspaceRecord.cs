using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Queuey.Client.Cli;

/// <summary>
/// Writes the workspace <c>apply</c> created into the profile in the deployment file, so the next command with the profile
/// reaches it: <c>QUEUEY_TENANT</c> under <c>profiles.&lt;profile&gt;.variables</c>, and <c>"tenant": "${QUEUEY_TENANT}"</c>
/// at the top when the file has no tenant, since a profile only gives the file's <c>${VAR}</c>s their values. Nothing else
/// in the file changes: the text is edited in place, so comments and formatting stay. A value that is there is never
/// touched: a file that names a tenant, or a profile that has the variable, is left as it is.
/// </summary>
// Blindtest 2 (Kenneth 2026-10-09): workspacet apply lager, skal stå i fila, så neste kommando finner det. Id-en er ingen
// hemmelighet.
internal static class DeploymentWorkspaceRecord
{
    internal const string Variable = "QUEUEY_TENANT";

    /// <summary>What was written, for the answer: the profile, the variable and whether the tenant line was added.</summary>
    internal sealed record Written(string Path, string Profile, string Variable, string Tenant, bool AddedTenantReference)
    {
        public string Said => $"Wrote {Tenant} to profiles.{Profile} in {System.IO.Path.GetFileName(Path)}" +
                              (AddedTenantReference ? $", and \"tenant\": \"${{{Variable}}}\" at the top, which reads it" : "") + ".";

        public object ToJson() => new
        {
            path = Path, profile = Profile, variable = Variable, tenant = Tenant, addedTenantReference = AddedTenantReference,
        };
    }

    /// <summary>Writes <paramref name="tenant"/> for <paramref name="profile"/>, or returns null and says why not.</summary>
    internal static Written? Record(string path, string profile, string tenant, out string? notWritten)
    {
        notWritten = null;
        // Security-review av #71 (K2): id-en er serverens tekst, og den skrives i en fil som committes og vises i terminalen.
        if (!CliErrors.LooksLikeAWorkspaceId(tenant))
        {
            notWritten = "Queuey's answer did not have the shape of a workspace id";
            return null;
        }
        // Ingen lenke følges: fila skrives via en temp-fil og rename, som EnvFile.Write gjør.
        if (new FileInfo(path).LinkTarget is not null)
        {
            notWritten = "it is a link, which apply does not write through";
            return null;
        }
        byte[] bytes = File.ReadAllBytes(path);
        int bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;

        Scan scan;
        try
        {
            scan = Scan.Of(bytes.AsSpan(bom), profile);
        }
        catch (JsonException)
        {
            notWritten = "the file could not be read as JSON";
            return null;
        }

        if (scan.HasTenant)
        {
            notWritten = "the file names a tenant already";
            return null;
        }
        if (scan.HasVariable)
        {
            notWritten = $"profiles.{profile}.variables has {Variable} already";
            return null;
        }
        if (scan.Profile is null)
        {
            notWritten = $"the file has no profile {profile}";
            return null;
        }

        string text = Encoding.UTF8.GetString(bytes, bom, bytes.Length - bom);
        // Bytene fra leseren regnes om til tegn, og innsettingene gjøres bakfra, så de tidligere posisjonene står.
        var inserts = new List<(int At, string Text)>();
        string value = JsonEncodedText.Encode(tenant).ToString();
        if (scan.Variables is { } variables)
            inserts.Add(Member(text, bytes, bom, variables, $"\"{Variable}\": \"{value}\""));
        else
            inserts.Add(Member(text, bytes, bom, scan.Profile, $"\"variables\": {{ \"{Variable}\": \"{value}\" }}"));
        inserts.Add(Member(text, bytes, bom, scan.Root!, $"\"tenant\": \"${{{Variable}}}\"", first: true));

        inserts.Sort((a, b) => b.At.CompareTo(a.At));
        var edited = new StringBuilder(text);
        foreach ((int at, string insert) in inserts)
            edited.Insert(at, insert);

        byte[] output = Encoding.UTF8.GetBytes(edited.ToString());
        string full = Path.GetFullPath(path);
        string temp = Path.Combine(Path.GetDirectoryName(full)!, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            {
                if (bom > 0) stream.Write(bytes, 0, bom);
                stream.Write(output, 0, output.Length);
            }
            // Modusen fila hadde, beholdes: den er committet og deles, ikke privat som .env.
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temp, File.GetUnixFileMode(full));
            if (new FileInfo(full).LinkTarget is not null)
                throw new IOException($"{path} became a link while apply wrote it, so it was not written.");
            File.Move(temp, full, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { /* ble kanskje aldri laget */ }
            throw;
        }

        return new Written(path, profile, Variable, tenant, AddedTenantReference: true);
    }

    /// <summary>
    /// Where and what to insert so <paramref name="member"/> becomes a member of <paramref name="obj"/>: first or last, on its
    /// own line with the indentation of the members there, or inline in an object that is empty.
    /// </summary>
    private static (int At, string Text) Member(string text, byte[] bytes, int bom, Frame obj, string member, bool first = false)
    {
        int Char(long byteIndex) => Encoding.UTF8.GetCharCount(bytes, bom, (int)byteIndex);

        if (obj.FirstMemberStart is null)
            return (Char(obj.Start + 1), $" {member} ");

        if (first)
        {
            int at = Char(obj.FirstMemberStart.Value);
            string indent = IndentOf(text, at);
            return (at, member + "," + (indent.Length > 0 || IsLineStart(text, at) ? "\n" + indent : " "));
        }

        int after = Char(obj.LastValueEnd!.Value);
        int lastStart = Char(obj.LastMemberStart!.Value);
        string lastIndent = IndentOf(text, lastStart);
        return IsLineStart(text, lastStart)
            ? (after, ",\n" + lastIndent + member)
            : (after, ", " + member);
    }

    private static bool IsLineStart(string text, int at)
    {
        for (int i = at - 1; i >= 0; i--)
        {
            if (text[i] == '\n') return true;
            if (text[i] != ' ' && text[i] != '\t') return false;
        }
        return true;
    }

    private static string IndentOf(string text, int at)
    {
        if (!IsLineStart(text, at)) return "";
        int start = at;
        while (start > 0 && (text[start - 1] == ' ' || text[start - 1] == '\t')) start--;
        return text.Substring(start, at - start);
    }

    /// <summary>One object in the file, as byte positions: its <c>{</c>, its first and last member, and the end of its last value.</summary>
    private sealed class Frame
    {
        public long Start;
        public string? Name;
        public bool IsObject;
        public long? FirstMemberStart;
        public long? LastMemberStart;
        public long? LastValueEnd;
        public string? PendingName;
    }

    private sealed class Scan
    {
        public Frame? Root;
        public Frame? Profile;
        public Frame? Variables;
        public bool HasTenant;
        public bool HasVariable;

        public static Scan Of(ReadOnlySpan<byte> json, string profile)
        {
            var scan = new Scan();
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var stack = new List<Frame>();

            while (reader.Read())
            {
                Frame? parent = stack.Count > 0 ? stack[^1] : null;
                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName:
                        string name = reader.GetString()!;
                        parent!.FirstMemberStart ??= reader.TokenStartIndex;
                        parent.LastMemberStart = reader.TokenStartIndex;
                        parent.PendingName = name;
                        if (stack.Count == 1 && string.Equals(name, "tenant", StringComparison.OrdinalIgnoreCase))
                            scan.HasTenant = true;
                        if (stack.Count == 4 && ReferenceEquals(parent, scan.Variables) && name == Variable)
                            scan.HasVariable = true;
                        break;

                    case JsonTokenType.StartObject:
                    case JsonTokenType.StartArray:
                        var frame = new Frame
                        {
                            Start = reader.TokenStartIndex, Name = parent?.PendingName, IsObject = reader.TokenType == JsonTokenType.StartObject,
                        };
                        if (parent is { IsObject: false })
                        {
                            parent.FirstMemberStart ??= reader.TokenStartIndex;
                            parent.LastMemberStart = reader.TokenStartIndex;
                        }
                        stack.Add(frame);
                        if (stack.Count == 1) scan.Root = frame;
                        else if (frame.IsObject && stack.Count == 3 && stack[1].Name == "profiles" && frame.Name == profile) scan.Profile = frame;
                        else if (frame.IsObject && stack.Count == 4 && ReferenceEquals(stack[2], scan.Profile) && frame.Name == "variables") scan.Variables = frame;
                        break;

                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        stack.RemoveAt(stack.Count - 1);
                        if (stack.Count > 0)
                            stack[^1].LastValueEnd = reader.BytesConsumed;
                        break;

                    default:
                        if (parent is { IsObject: false })
                        {
                            parent.FirstMemberStart ??= reader.TokenStartIndex;
                            parent.LastMemberStart = reader.TokenStartIndex;
                        }
                        parent!.LastValueEnd = reader.BytesConsumed;
                        break;
                }
            }

            return scan;
        }
    }
}
