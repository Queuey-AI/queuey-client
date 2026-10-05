using System.Text.Json;
using Queuey.Client.Cli;

namespace Queuey.Client.Cli.Tests;

/// <summary>
/// A replay to a listener the server refuses (409) is shown as the server's hint, not as an error: the message says
/// what to change. A queue that doesn't forward to the listener is told to, and a queue whose payload privacy is
/// shape only is told to share its payloads in full first.
/// </summary>
public class ReplayCommandTests
{
    private const string ServerMessage =
        "Queue 'que_orders' shares only its payloads' shape (payload visibility shapeOnly), and a replay to a listener " +
        "would hand a stored event's values to queuey listen. Change the queue's payload sharing to full first.";

    [Theory]
    [InlineData("listener_replay_needs_local_forward")]
    [InlineData("listener_replay_needs_full_payload_sharing")]
    public void A_refused_replay_shows_the_servers_message_as_the_hint(string code)
    {
        var refused = new QueueyConflictException(ServerMessage, code);
        var (stdout, stderr) = (new StringWriter(), new StringWriter());

        Assert.True(ReplayCommand.IsRefusal(refused));
        ReplayCommand.WriteRefusal(refused, json: false, stdout, stderr);

        Assert.Equal($"Replay refused: {ServerMessage}{Environment.NewLine}", stderr.ToString());
        Assert.Equal("", stdout.ToString());
    }

    [Fact]
    public void With_json_a_refused_replay_is_the_api_error_envelope_on_stdout()
    {
        var refused = new QueueyConflictException(ServerMessage, "listener_replay_needs_full_payload_sharing");
        var (stdout, stderr) = (new StringWriter(), new StringWriter());

        ReplayCommand.WriteRefusal(refused, json: true, stdout, stderr);

        using var doc = JsonDocument.Parse(stdout.ToString());
        var error = doc.RootElement.GetProperty("error");
        Assert.Equal("listener_replay_needs_full_payload_sharing", error.GetProperty("code").GetString());
        Assert.Equal(ServerMessage, error.GetProperty("message").GetString());
        Assert.Equal("", stderr.ToString());
    }

    [Fact]
    public void Other_errors_are_not_a_refused_replay_and_keep_the_general_handling()
    {
        // Another 409 (an archived queue), a refusal code on another status, and an error without a code.
        Assert.False(ReplayCommand.IsRefusal(new QueueyConflictException("This queue is archived.", "queue_archived")));
        Assert.False(ReplayCommand.IsRefusal(new QueueyForbiddenException("Forbidden", "listener_replay_needs_local_forward")));
        Assert.False(ReplayCommand.IsRefusal(new QueueyConflictException("Conflict")));
    }
}
