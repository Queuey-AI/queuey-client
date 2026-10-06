namespace Queuey.Client.Waas.Tests;

/// <summary>
/// Et foreslått kommandoord (et kønavn i `queuey verify &lt;kø&gt; --event …`) settes inn som det er bare når det er et rent ord.
/// Re-review av #52 (2026-10-06): en kø fra før navneregelen kunne hete «--send», og ble et flagg i forslaget.
/// </summary>
public class CommandWordsTests
{
    [Theory]
    [InlineData("--send")]
    [InlineData("-x")]
    [InlineData(".hidden")]
    [InlineData("_private")]
    [InlineData("orders; rm -rf")]
    [InlineData("two words")]
    [InlineData("")]
    [InlineData(null)]
    public void A_word_that_is_not_plain_gives_a_placeholder(string? value)
    {
        Assert.Null(CommandWords.Word(value));
    }

    [Theory]
    [InlineData("orders", "orders")]
    [InlineData("  orders  ", "orders")]
    [InlineData("9lives", "9lives")]
    [InlineData("Orders.v2-eu_1", "Orders.v2-eu_1")]
    public void A_plain_word_is_used_as_it_is(string value, string expected)
    {
        Assert.Equal(expected, CommandWords.Word(value));
    }

    [Fact]
    public void A_word_longer_than_a_name_gives_a_placeholder()
    {
        Assert.Null(CommandWords.Word(new string('a', 65)));
        Assert.Equal(new string('a', 64), CommandWords.Word(new string('a', 64)));
    }
}
