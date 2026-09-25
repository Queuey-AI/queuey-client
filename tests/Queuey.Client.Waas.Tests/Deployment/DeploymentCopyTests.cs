using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace Queuey.Client.Waas.Tests;

/// <summary>
/// Expand og ToTemplate kopierer fila felt for felt. Ingress på kø-nivå manglet i Expand, og apply,
/// check og dry-run droppet den stille (funnet 2026-09-23). Denne testen gir hvert felt i fila sin egen
/// verdi — også signing og rateLimit inne i delivery, og i to køer — og krever at hver verdi havner på
/// samme sted i kopien. Før 2026-09-24 sjekket den bare at feltene ikke var null, så en verdi som ble
/// kopiert til feil felt eller feil kø, gikk gjennom. Et felt noen legger til senere, fylles og sjekkes
/// av seg selv.
/// </summary>
public class DeploymentCopyTests
{
    [Fact]
    public void Expand_keeps_every_value_where_it_was()
    {
        DeploymentFile file = Filled();

        AssertSameValues(Leaves(file), file.Expand(_ => null));
    }

    [Fact]
    public void ToTemplate_keeps_every_value_where_it_was_except_the_ones_it_templates()
    {
        DeploymentFile file = Filled();
        Dictionary<string, string?> expected = Leaves(file);

        // Det ToTemplate skal endre: workspacet og vertene er det som skiller miljøene.
        expected["Tenant"] = null;
        expected["Workspace.Delivery.BaseUrl"] = "${" + DeploymentTemplate.BaseUrlVariable + "}";
        expected["Queues[invoices].Delivery.Url"] = "${" + DeploymentTemplate.QueueUrlVariable("invoices", "staging") + "}";

        AssertSameValues(expected, DeploymentTemplate.ToTemplate(file, "staging"));
    }

    [Fact]
    public void Every_value_in_the_filled_file_is_its_own()
    {
        // Testen over er bare så god som verdiene er ulike: to like verdier kunne byttet plass uten at
        // den merket det. Bool har bare to verdier, så de står utenfor.
        var values = Leaves(Filled())
            .Where(l => l.Value is not null and not "true" and not "false")
            .Select(l => l.Value)
            .ToList();

        Assert.Equal(values.Count, values.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(Leaves(Filled()), l => l.Key == "Queues[orders].Delivery.Signing.CredentialRef");
        Assert.Contains(Leaves(Filled()), l => l.Key == "Workspace.Delivery.RateLimit.PerSeconds");
    }

    private static void AssertSameValues(Dictionary<string, string?> expected, DeploymentFile copy)
    {
        Dictionary<string, string?> actual = Leaves(copy);

        var wrong = expected.Keys.Union(actual.Keys)
            .Where(path => !Equals(expected.GetValueOrDefault(path), actual.GetValueOrDefault(path)))
            .Select(path => $"{path}: expected {expected.GetValueOrDefault(path) ?? "(none)"}, got {actual.GetValueOrDefault(path) ?? "(none)"}")
            .ToList();

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    }

    // ── fylling ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A file with its own value in every declarable property of the workspace and two queues. The
    /// "invoices" queue delivers to an absolute URL, which ToTemplate turns into a variable; "orders"
    /// has a relative path, which it keeps.
    /// </summary>
    private static DeploymentFile Filled()
    {
        var sample = new Sample();

        var file = new DeploymentFile
        {
            Schema = sample.Text("schema"),
            Tenant = sample.Text("ten"),
            Workspace = (DeploymentWorkspace)sample.Fill(typeof(DeploymentWorkspace)),
        };

        foreach (string name in new[] { "orders", "invoices" })
            file.Queues[name] = (DeploymentQueue)sample.Fill(typeof(DeploymentQueue));

        file.Queues["invoices"].Delivery!.Url = "https://invoices.example.com/" + sample.Text("path");
        return file;
    }

    /// <summary>Hands out a new value per call: text and numbers never repeat, bools alternate.</summary>
    private sealed class Sample
    {
        private int _next = 1;
        private bool _flag;

        public string Text(string prefix) => $"{prefix}-{_next++}";

        public object Fill(Type type)
        {
            object instance = Activator.CreateInstance(type)
                ?? throw new InvalidOperationException($"Cannot create {type.Name}.");

            foreach (PropertyInfo property in Declarable(type))
                property.SetValue(instance, Value(property.PropertyType, property.Name));

            return instance;
        }

        private object Value(Type type, string name)
        {
            Type t = Nullable.GetUnderlyingType(type) ?? type;

            if (t == typeof(string)) return Text(name);
            if (t == typeof(int)) return _next++;
            // Bool: en ikke-nullbar er false fra før, så den må være true for at en tapt verdi skal synes.
            // En nullbar veksler for seg, så to nabofelt (dlqEnabled, idempotent) som bytter plass, gir forskjell.
            if (t == typeof(bool)) return type == typeof(bool) || (_flag = !_flag);

            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
            {
                var list = (IList)Activator.CreateInstance(t)!;
                Type item = t.GetGenericArguments()[0];
                list.Add(Fill(item));
                list.Add(Fill(item));
                return list;
            }

            if (t.IsClass && t.Namespace == typeof(DeploymentFile).Namespace)
                return Fill(t);

            throw new InvalidOperationException($"No sample value for {t.Name} ({name}); add one here.");
        }
    }

    // ── lesing ───────────────────────────────────────────────────────────────

    /// <summary>Every leaf value in the file, by its path — <c>Queues[orders].Delivery.Signing.Enabled</c>.</summary>
    private static Dictionary<string, string?> Leaves(DeploymentFile file)
    {
        var leaves = new Dictionary<string, string?>(StringComparer.Ordinal);
        Walk(string.Empty, file, leaves);
        return leaves;
    }

    private static void Walk(string path, object? value, Dictionary<string, string?> leaves)
    {
        switch (value)
        {
            case null:
                leaves[path] = null;
                return;
            case string s:
                leaves[path] = s;
                return;
            case bool b:
                leaves[path] = b ? "true" : "false";
                return;
            case int i:
                leaves[path] = i.ToString(CultureInfo.InvariantCulture);
                return;
            case IDictionary dictionary:
                foreach (DictionaryEntry entry in dictionary)
                    Walk($"{path}[{entry.Key}]", entry.Value, leaves);
                return;
            case IList list:
                for (int i = 0; i < list.Count; i++)
                    Walk($"{path}[{i}]", list[i], leaves);
                return;
        }

        foreach (PropertyInfo property in Declarable(value.GetType()))
            Walk(path.Length == 0 ? property.Name : $"{path}.{property.Name}", property.GetValue(value), leaves);
    }

    private static IEnumerable<PropertyInfo> Declarable(Type type)
        => type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite && p.CanRead);
}
