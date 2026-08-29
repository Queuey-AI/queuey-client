using System;

namespace Queuey.Edge.Mqtt;

/// <summary>Pure topic helpers, kept separate so routing is testable without a broker.</summary>
public static class MqttTopics
{
    /// <summary>MQTT topic-filter matching per the spec: <c>+</c> one level, <c>#</c> the rest (last level only).</summary>
    public static bool Matches(string filter, string topic)
    {
        if (string.IsNullOrEmpty(filter) || string.IsNullOrEmpty(topic)) return false;
        var f = filter.Split('/');
        var t = topic.Split('/');
        for (var i = 0; i < f.Length; i++)
        {
            if (f[i] == "#") return i == f.Length - 1;
            if (i >= t.Length) return false;
            if (f[i] == "+") continue;
            if (!string.Equals(f[i], t[i], StringComparison.Ordinal)) return false;
        }
        return f.Length == t.Length;
    }

    /// <summary>The route whose filter matches the topic — first match in configuration order wins.</summary>
    public static MqttRoute? Resolve(MqttSourceOptions options, string topic)
    {
        foreach (var r in options.Routes)
            if (Matches(r.TopicFilter, topic)) return r;
        return null;
    }

    /// <summary>The topic segment chosen as group key, or null when not configured / out of range.</summary>
    public static string? GroupKey(MqttRoute route, string topic)
    {
        if (route.GroupKeySegment is not { } idx) return null;
        var parts = topic.Split('/');
        return idx < parts.Length && parts[idx].Length > 0 ? parts[idx] : null;
    }
}
