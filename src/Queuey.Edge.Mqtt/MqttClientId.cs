using System;
using System.Security.Cryptography;
using System.Text;
using Queuey.Client;

namespace Queuey.Edge.Mqtt;

/// <summary>
/// The client id the source connects with. It is also the name of Edge's broker session: the source connects with a
/// persistent session, and the broker keeps what it queues for Edge while Edge is away under this id. A new id is a new,
/// empty session, and what the broker held for the old one is never delivered to Edge.
/// </summary>
internal static class MqttClientId
{
    /// <summary>
    /// MQTT 3.1.1, section 3.1.3.1: every broker must accept a client id of 1 to 23 bytes of <c>0-9</c>, <c>a-z</c> and
    /// <c>A-Z</c>. A broker may accept more, and Mosquitto does, but nothing longer is guaranteed.
    /// </summary>
    internal const int GuaranteedBytes = 23;

    /// <summary>An MQTT string carries its length in two bytes.</summary>
    internal const int MaxBytes = 65535;

    private const string NodePrefix = "queueyedge";

    /// <summary>
    /// The id for a node: <c>queueyedge</c> and the first 13 hex digits of the SHA-256 of the node id, 23 letters and
    /// digits in all, so every MQTT 3.1.1 broker takes it. The node id itself is up to 64 characters and may hold
    /// <c>.</c>, <c>_</c> and <c>-</c>, which the guarantee does not cover, so it is hashed rather than used as it is.
    /// </summary>
    internal static string ForNode(string nodeId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(nodeId));
        return NodePrefix + Convert.ToHexString(hash, 0, 7)[..(GuaranteedBytes - NodePrefix.Length)].ToLowerInvariant();
    }

    /// <summary>
    /// The id from before Edge kept it in the spool: <c>queuey-edge-</c> and the machine name. A spool from then has no
    /// stored id, and keeps this one so that its broker session carries over.
    /// </summary>
    internal static string ForMachine(string machineName) => "queuey-edge-" + machineName;

    /// <summary>
    /// Throws <see cref="QueueyConfigurationException"/> when a configured id cannot go in an MQTT CONNECT: empty,
    /// longer than <see cref="MaxBytes"/> in UTF-8, or holding a control character.
    /// </summary>
    /// <param name="value">The configured id; null means not configured, which is valid.</param>
    /// <param name="setting">How the operator set it, named in the message (an option, a flag or a variable).</param>
    internal static void Validate(string? value, string setting)
    {
        if (value is null) return;
        if (string.IsNullOrWhiteSpace(value))
            throw new QueueyConfigurationException(
                $"{setting} is empty. Leave it unset to let Edge choose one and keep it in the spool, or give it a value " +
                "unique on the broker.");
        if (Encoding.UTF8.GetByteCount(value) > MaxBytes)
            throw new QueueyConfigurationException($"{setting} is longer than the {MaxBytes} bytes an MQTT client id can hold.");
        foreach (var c in value)
        {
            if (!char.IsControl(c)) continue;
            throw new QueueyConfigurationException(
                $"{setting} has the control character U+{(int)c:X4}; MQTT forbids U+0000 in a client id and lets a broker " +
                "refuse the others.");
        }
    }
}
