using Queuey.Client;
using Queuey.Edge.Mqtt;

namespace Queuey.Edge.Mqtt.Tests;

/// <summary>Routing without a broker: filter matching, first-match-wins, the lane from a topic segment, CLI shorthand.</summary>
public class MqttTopicsTests
{
    [Theory]
    [InlineData("plant/+/alarms", "plant/press-2/alarms", true)]
    [InlineData("plant/+/alarms", "plant/press-2/state", false)]
    [InlineData("plant/#", "plant/press-2/alarms/high", true)]
    [InlineData("plant/#", "plant", true)] // spec: the parent level itself matches
    [InlineData("plant/press-2/alarms", "plant/press-2/alarms", true)]
    [InlineData("plant/+", "plant/a/b", false)]
    public void Filter_matching_follows_the_mqtt_spec(string filter, string topic, bool expected)
        => Assert.Equal(expected, MqttTopics.Matches(filter, topic));

    [Fact]
    public void First_matching_route_wins_and_segment_becomes_the_lane()
    {
        var o = new MqttSourceOptions();
        o.Routes.Add(new MqttRoute { TopicFilter = "plant/+/alarms", Queue = "alarms", GroupKeySegment = 1 });
        o.Routes.Add(new MqttRoute { TopicFilter = "plant/#", Queue = "everything" });

        var route = MqttTopics.Resolve(o, "plant/press-2/alarms")!;
        Assert.Equal("alarms", route.Queue);
        Assert.Equal("press-2", MqttTopics.GroupKey(route, "plant/press-2/alarms"));

        Assert.Equal("everything", MqttTopics.Resolve(o, "plant/press-2/state")!.Queue);
        Assert.Null(MqttTopics.Resolve(o, "office/temp"));
        Assert.Null(MqttTopics.GroupKey(new MqttRoute { GroupKeySegment = 9 }, "a/b"));
    }

    [Fact]
    public void Cli_shorthand_parses_filters_queues_and_segments()
    {
        var routes = MqttRoute.ParseMany("plant/+/alarms=alarms@1; plant/+/state = machine-state ;sensors/#=telemetry-events");
        Assert.Equal(3, routes.Count);
        Assert.Equal(("plant/+/alarms", "alarms", 1), (routes[0].TopicFilter, routes[0].Queue, routes[0].GroupKeySegment));
        Assert.Equal(("plant/+/state", "machine-state", (int?)null), (routes[1].TopicFilter, routes[1].Queue, routes[1].GroupKeySegment));
        Assert.Equal("telemetry-events", routes[2].Queue);

        Assert.Throws<QueueyConfigurationException>(() => MqttRoute.ParseMany("nofilter"));
        Assert.Throws<QueueyConfigurationException>(() => MqttRoute.ParseMany("a/b=q@x"));
    }

    [Fact]
    public void Options_validate_at_composition()
    {
        var o = new MqttSourceOptions();
        Assert.Throws<QueueyConfigurationException>(o.Validate);
        o.Routes.Add(new MqttRoute { TopicFilter = "a/#", Queue = "" });
        Assert.Throws<QueueyConfigurationException>(o.Validate);
        o.Routes[0].Queue = "q";
        o.Validate();
    }
}
