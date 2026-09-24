using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Queuey.Client.Waas.Tests;

/// <summary>
/// Expand og ToTemplate kopierer fila felt for felt. Ingress på kø-nivå manglet i Expand, og apply,
/// check og dry-run droppet den stille (funnet 2026-09-23). Denne testen fyller hvert felt en kø og
/// et workspace kan ha, og krever at hvert av dem overlever begge kopiene — så neste felt noen
/// legger til, ikke kan glemmes på samme måte.
/// </summary>
public class DeploymentCopyTests
{
    [Fact]
    public void Every_queue_field_survives_expand_and_template()
    {
        DeploymentFile file = Filled();

        foreach ((string step, DeploymentFile copy) in Copies(file))
        {
            DeploymentQueue queue = copy.Queues["orders"];
            foreach (PropertyInfo property in Declarable(typeof(DeploymentQueue)))
                Assert.True(property.GetValue(queue) is not null, $"{step} dropped queues.orders.{property.Name}");
        }
    }

    [Fact]
    public void Every_workspace_field_survives_expand_and_template()
    {
        DeploymentFile file = Filled();

        foreach ((string step, DeploymentFile copy) in Copies(file))
        {
            foreach (PropertyInfo property in Declarable(typeof(DeploymentWorkspace)))
                Assert.True(property.GetValue(copy.Workspace!) is not null, $"{step} dropped workspace.{property.Name}");
        }
    }

    private static IEnumerable<(string, DeploymentFile)> Copies(DeploymentFile file)
    {
        yield return ("Expand", file.Expand(_ => null));
        yield return ("ToTemplate", DeploymentTemplate.ToTemplate(file, "staging"));
    }

    private static IEnumerable<PropertyInfo> Declarable(Type type)
        => type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite);

    /// <summary>A file with a value in every declarable property of the workspace and one queue.</summary>
    private static DeploymentFile Filled()
    {
        var queue = new DeploymentQueue();
        foreach (PropertyInfo p in Declarable(typeof(DeploymentQueue)))
            p.SetValue(queue, Sample(p.PropertyType));

        var workspace = new DeploymentWorkspace();
        foreach (PropertyInfo p in Declarable(typeof(DeploymentWorkspace)))
            p.SetValue(workspace, Sample(p.PropertyType));

        return new DeploymentFile
        {
            Tenant = "ten_abc",
            Workspace = workspace,
            Queues = { ["orders"] = queue },
        };
    }

    private static object Sample(Type type)
    {
        Type t = Nullable.GetUnderlyingType(type) ?? type;
        if (t == typeof(string)) return "deliver";
        if (t == typeof(int)) return 7;
        if (t == typeof(bool)) return true;
        if (t == typeof(QueueDelivery)) return new QueueDelivery { Url = "/orders" };
        if (t == typeof(WorkspaceDelivery)) return new WorkspaceDelivery { BaseUrl = "https://hooks.example.com" };
        return Activator.CreateInstance(t)
               ?? throw new InvalidOperationException($"No sample value for {t.Name}; add one here.");
    }
}
