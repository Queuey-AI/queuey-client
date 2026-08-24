using System;
using Queuey.Client;

namespace Queuey.Edge;

/// <summary>
/// The local store is at its configured limit (<c>MaxSpoolBytes</c>) or the
/// disk is full — Queuey CANNOT take custody of this event. This is the
/// honest backpressure of lossless retention: age alone never deletes an
/// accepted event, so a node disconnected for longer than its provisioned
/// storage covers must eventually refuse new work rather than silently drop
/// old work. Size the spool from the docs' autonomy table
/// (events/day × payload ≈ days of autonomy); the default 512 MB holds
/// roughly a year at 1 event/minute × 1 KB.
/// </summary>
public sealed class QueueySpoolFullException : QueueyException
{
    public QueueySpoolFullException(string message) : base(message) { }
}

/// <summary>
/// The local store is corrupt or unreadable and Queuey has halted rather
/// than gamble with events it already accepted. The spool file is preserved
/// untouched; transfer is stopped; new publishes fail with this exception
/// until an operator explicitly chooses a path:
/// <c>queuey-edge recover</c> (salvage readable rows into a fresh spool,
/// reporting exactly how many were unreadable) or
/// <c>queuey-edge reset --accept-data-loss</c> (quarantine the file and
/// start clean, with the loss acknowledged in the operator's own name).
/// Queuey never silently starts a fresh spool — that would abandon accepted
/// events without anyone deciding to.
/// </summary>
public sealed class QueueyStorageFaultedException : QueueyException
{
    public QueueyStorageFaultedException(string message) : base(message) { }
}

/// <summary>
/// The payload cannot be accepted locally: it failed serialization, or it
/// exceeds the locally configured <c>MaxPayloadBytes</c>. Thrown
/// synchronously because event construction is the caller's side of the
/// responsibility boundary — surfacing it at the call site beats
/// quarantining it days later.
/// </summary>
public sealed class QueueyPayloadRejectedException : QueueyException
{
    public QueueyPayloadRejectedException(string message, Exception? innerException = null)
        : base(message, innerException: innerException) { }
}
