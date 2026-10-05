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

    [Fact]
    public void An_option_where_a_value_was_wanted_is_remembered_with_the_option_that_wanted_it()
    {
        // `--mqtt-password -Xy9…`: passordet ble lest som valget Xy9…, og en feil skal ikke vise det (re-review 2026-10-05).
        ArgMap map = ArgMap.Parse(new[] { "--assembly", "-Xy9", "--json" }, Flags);

        Assert.Null(map.Get("assembly"));
        Assert.Equal("assembly", map.InPlaceOfAValue["Xy9"]);
        Assert.False(map.InPlaceOfAValue.ContainsKey("assembly"));
    }

    [Fact]
    public void A_value_that_starts_with_a_dash_is_a_value_after_an_equals_sign()
    {
        ArgMap map = ArgMap.Parse(new[] { "--assembly=-Xy9", "--json" }, Flags);

        Assert.Equal("-Xy9", map.Get("assembly"));
        Assert.Empty(map.InPlaceOfAValue);
    }
}
