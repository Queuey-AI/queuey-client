using System.Linq;
using System.Text.Json;

namespace Queuey.Server.Hosting;

/// <summary>What the library writes out: JSON for its files, and text from Docker, Keycloak or Queuey made safe for a terminal.</summary>
internal static class Text
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// One line without control characters: text another program wrote (an error body, a log line) cannot move the cursor,
    /// change the window's title or start a line that looks like ours.
    /// </summary>
    public static string Line(string text) => new(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
}
