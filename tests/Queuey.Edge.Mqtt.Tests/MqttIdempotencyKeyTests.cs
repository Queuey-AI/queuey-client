using System.Security.Cryptography;
using System.Text;
using Queuey.Client;
using Queuey.Edge.Mqtt;

namespace Queuey.Edge.Mqtt.Tests;

/// <summary>
/// The key a producer's event id becomes: the id where it is safe as a header, and a hash of the payload, so a
/// reused id with new content is a new event and only a byte-identical repeat is the same one.
/// </summary>
public class MqttIdempotencyKeyTests
{
    [Fact]
    public void A_string_id_at_the_top_level_is_the_key_with_the_payload_hash()
    {
        var payload = Json("""{"eventId":"node-7-000123","temp":21.5}""");

        Assert.Equal("node-7-000123:" + PayloadHash(payload), MqttIdempotencyKey.From("eventId", payload));
    }

    [Fact]
    public void A_dotted_path_reaches_a_nested_field()
    {
        var payload = Json("""{"meta":{"id":"abc-1"},"value":3}""");

        Assert.Equal("abc-1:" + PayloadHash(payload), MqttIdempotencyKey.From("meta.id", payload));
    }

    [Theory]
    [InlineData("""{"seq":42}""", "42")]
    [InlineData("""{"seq":-7}""", "-7")]
    [InlineData("""{"seq":1.50}""", "1.50")]  // the text as sent, not a re-formatted number
    [InlineData("""{"seq":1e3}""", "1e3")]
    public void A_number_id_is_its_text_as_sent(string json, string id)
    {
        var payload = Json(json);

        Assert.Equal(id + ":" + PayloadHash(payload), MqttIdempotencyKey.From("seq", payload));
    }

    [Theory]
    [InlineData("""{"temp":21.5}""")]                // missing
    [InlineData("""{"eventId":null}""")]             // null
    [InlineData("""{"eventId":""}""")]               // empty
    [InlineData("""{"eventId":{"v":1}}""")]          // object
    [InlineData("""{"eventId":[1,2]}""")]            // array
    [InlineData("""{"eventId":true}""")]             // bool
    [InlineData("""["eventId"]""")]                  // not an object at the root
    [InlineData("""{"meta":"x"}""")]                 // a path through a non-object
    [InlineData("eventId=7")]                        // not JSON
    [InlineData("""{"eventId":"7",""")]              // truncated JSON
    [InlineData("")]                                 // empty payload
    public void A_payload_without_a_usable_id_has_no_key(string body)
    {
        var path = body.Contains("meta") ? "meta.id" : "eventId";

        Assert.Null(MqttIdempotencyKey.From(path, Json(body)));
    }

    [Fact]
    public void Invalid_UTF8_has_no_key_and_does_not_throw()
    {
        var payload = Json("""{"eventId":"X"}""");
        payload[Array.IndexOf(payload, (byte)'X')] = 0xC3; // a lead byte with no continuation: not UTF-8

        Assert.Null(MqttIdempotencyKey.From("eventId", payload));
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("tab\there")]
    [InlineData("ærlig")]
    [InlineData("line\nbreak")]
    public void An_id_that_is_not_visible_ASCII_is_replaced_by_its_hash(string id)
    {
        var payload = Json($$"""{"eventId":{{System.Text.Json.JsonSerializer.Serialize(id)}}}""");

        Assert.Equal("h" + Sha256Hex(Encoding.UTF8.GetBytes(id))[..32] + ":" + PayloadHash(payload),
            MqttIdempotencyKey.From("eventId", payload));
    }

    [Fact]
    public void An_id_up_to_128_characters_is_kept_and_a_longer_one_is_hashed()
    {
        var limit = new string('a', MqttIdempotencyKey.MaxPlainIdLength);
        var over = limit + "a";

        Assert.StartsWith(limit + ":", MqttIdempotencyKey.From("id", Json($$"""{"id":"{{limit}}"}""")));
        var hashed = MqttIdempotencyKey.From("id", Json($$"""{"id":"{{over}}"}"""))!;
        Assert.Equal("h" + Sha256Hex(Encoding.UTF8.GetBytes(over))[..32], hashed[..^17]);
        Assert.Equal(1 + 32 + 1 + 16, hashed.Length);
    }

    [Fact]
    public void The_same_id_with_other_content_is_another_key()
    {
        var first = MqttIdempotencyKey.From("eventId", Json("""{"eventId":"17","temp":20}"""));
        var again = MqttIdempotencyKey.From("eventId", Json("""{"eventId":"17","temp":20}"""));
        var reused = MqttIdempotencyKey.From("eventId", Json("""{"eventId":"17","temp":21}"""));

        Assert.Equal(first, again);
        Assert.NotEqual(first, reused);
    }

    [Theory]
    [InlineData("eventId", true)]
    [InlineData("meta.id", true)]
    [InlineData("a.b.c", true)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData(".id", false)]
    [InlineData("meta.", false)]
    [InlineData("meta..id", false)]
    [InlineData("meta. .id", false)]
    public void A_route_accepts_only_a_path_without_empty_segments(string field, bool valid)
    {
        var route = new MqttRoute { TopicFilter = "site/#", Queue = "telemetry", IdempotencyKeyField = field };

        if (valid) route.Validate();
        else Assert.Contains("no empty segments", Assert.Throws<QueueyConfigurationException>(route.Validate).Message);
    }

    private static byte[] Json(string text) => Encoding.UTF8.GetBytes(text);

    private static string PayloadHash(byte[] payload) => Sha256Hex(payload)[..16];

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
