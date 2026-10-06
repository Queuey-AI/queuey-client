using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Queuey.Client.Waas;

// Herding før tag (review av #53, 2026-10-06): en feil i brukerfila eller i deploy-filas profiler viste JSON-stien fra parseren
// (ex.Path), og den har med navnene i fila: en nøkkel limt inn der et feltnavn eller profilnavn skulle stått, sto i feilen.
// Nå vises et ledd bare når det er et felt fila har, eller et navn med formen til et profilnavn eller variabelnavn, som
// aldri ser ut som en nøkkel. Resten blir «…», og linjen sier fortsatt hvor feilen er.

/// <summary>The JSON path of a parse error, with every name in it that may be something pasted in its place masked.</summary>
internal static class JsonErrorPaths
{
    /// <summary>What a masked name is shown as.</summary>
    internal const string Masked = "…";

    /// <summary>
    /// <paramref name="path"/> (<c>$.profiles.dev.apiKey</c>, as System.Text.Json writes it) with each name shown only when
    /// the test for its depth accepts it: <paramref name="shownAt"/>[0] for the first name after <c>$</c>, and so on. A name
    /// deeper than the tests reach, and anything the path holds that is not a name or an index, is masked. Null for no path.
    /// </summary>
    internal static string? Mask(string? path, params Func<string, bool>[] shownAt)
    {
        if (string.IsNullOrEmpty(path) || path![0] != '$')
            return null;

        var shown = new StringBuilder("$");
        int depth = 0;
        int i = 1;
        while (i < path.Length)
        {
            string? name;
            if (path[i] == '.')
            {
                int end = path.IndexOfAny(new[] { '.', '[' }, i + 1);
                if (end < 0) end = path.Length;
                name = path.Substring(i + 1, end - i - 1);
                i = end;
            }
            else if (path[i] == '[' && i + 1 < path.Length && path[i + 1] == '\'')
            {
                // ['a.b'] for et navn med tegn som ikke kan stå etter et punktum. Et slikt navn har aldri formen til et felt eller
                // et profilnavn, så det vises ikke; det leses bare for å finne hvor neste ledd begynner.
                int end = path.IndexOf("']", i + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    shown.Append('.').Append(Masked);
                    break;
                }

                name = null;
                i = end + 2;
            }
            else if (path[i] == '[')
            {
                int end = path.IndexOf(']', i + 1);
                if (end < 0 || end == i + 1 || !path.Substring(i + 1, end - i - 1).All(c => c >= '0' && c <= '9'))
                {
                    shown.Append('.').Append(Masked);
                    break;
                }

                shown.Append(path, i, end - i + 1);
                i = end + 1;
                continue;
            }
            else
            {
                shown.Append('.').Append(Masked);
                break;
            }

            bool show = name is not null && depth < shownAt.Length && shownAt[depth](name);
            shown.Append('.').Append(show ? name : Masked);
            depth++;
        }

        return shown.ToString();
    }

    /// <summary>A test that accepts the field names <paramref name="fields"/>, in any casing, as the parser reads them.</summary>
    internal static Func<string, bool> Fields(params string[] fields)
    {
        var known = new HashSet<string>(fields, StringComparer.OrdinalIgnoreCase);
        return known.Contains;
    }
}
