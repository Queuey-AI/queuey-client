using System;
using System.Collections.Generic;
using Queuey.Client.Cli;

namespace Queuey.Client.Cli.Tests;

public class ArgMapTests
{
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "dry-run", "json" };

    [Fact]
    public void Parses_key_value_and_key_equals_value()
    {
        ArgMap map = ArgMap.Parse(new[] { "--assembly", "app.dll", "--only=a,b" }, Flags);

        Assert.Equal("app.dll", map.Get("assembly"));
        Assert.Equal("a,b", map.Get("only"));
    }

    [Fact]
    public void Boolean_flag_is_present_with_null_value()
    {
        ArgMap map = ArgMap.Parse(new[] { "--dry-run" }, Flags);

        Assert.True(map.Has("dry-run"));
        Assert.Null(map.Get("dry-run"));
    }

    [Fact]
    public void Collects_positionals()
    {
        ArgMap map = ArgMap.Parse(new[] { "orders", "--event", "order.created" }, Flags);

        Assert.Equal("orders", map.FirstPositional);
        Assert.Equal("order.created", map.Get("event"));
    }

    [Fact]
    public void Flag_does_not_swallow_the_following_option()
    {
        ArgMap map = ArgMap.Parse(new[] { "--dry-run", "--assembly", "app.dll" }, Flags);

        Assert.True(map.Has("dry-run"));
        Assert.Equal("app.dll", map.Get("assembly"));
    }

    [Fact]
    public void Value_option_followed_by_another_option_has_no_value()
    {
        ArgMap map = ArgMap.Parse(new[] { "--assembly", "--json" }, Flags);

        Assert.True(map.Has("assembly"));
        Assert.Null(map.Get("assembly"));
        Assert.True(map.Has("json"));
    }

    [Fact]
    public void Value_option_at_end_has_no_value()
    {
        ArgMap map = ArgMap.Parse(new[] { "--assembly" }, Flags);

        Assert.True(map.Has("assembly"));
        Assert.Null(map.Get("assembly"));
    }
}
