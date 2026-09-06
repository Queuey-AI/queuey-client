using System.Collections.Generic;

namespace Queuey.Client.Waas;

/// <summary>
/// What every sync run reports, whatever it applied. Lets one exception type carry a stream run or a
/// queue run without a caller having to know which it caught.
/// </summary>
public interface ISyncRunResult
{
    /// <summary>Total items the run selected — attempted plus not attempted.</summary>
    int Total { get; }

    /// <summary>Items that applied successfully.</summary>
    int Succeeded { get; }

    /// <summary>Items that were attempted and failed.</summary>
    int Failed { get; }

    /// <summary>Items the run selected but never sent, because it stopped at an earlier failure.</summary>
    IReadOnlyList<string> NotAttempted { get; }

    /// <summary>Whether everything selected succeeded, with nothing left unattempted.</summary>
    bool AllSucceeded { get; }

    /// <summary>
    /// Non-fatal observations about the state the run left behind — a queue with nowhere to deliver,
    /// for example. Never a reason to fail: the declared state landed, the workspace is just not
    /// fully wired yet.
    /// </summary>
    IReadOnlyList<string> Warnings { get; }
}
