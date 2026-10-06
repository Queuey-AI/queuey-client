using System;
using System.IO;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Queuey.Client.Cli;

/// <summary>
/// The lines <c>queuey listen</c> prints, written one at a time from a bounded queue, so the session never waits on
/// whoever reads them. <see cref="Gone"/> completes when nobody reads them any more: the reader closed its end of the
/// pipe, or stopped reading until the queue was full. The session then stops and frees its queue.
/// </summary>
// Review av queuey-client #50, K3b: Console.Out svelger EPIPE, og .NET ignorerer SIGPIPE, så `listen --json | head -n 5`
// ble aldri ferdig, holdt køen og ackte uten leser. Og en full pipe blokkerte handleren, så senere eventer gikk til DLQ.
internal sealed class LineWriter
{
    /// <summary>How many lines may wait for the reader before it counts as gone.</summary>
    internal const int DefaultCapacity = 1024;

    private readonly Channel<string> _lines;
    private readonly TextWriter _target;
    private readonly TaskCompletionSource<string> _gone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _pump;

    public LineWriter(TextWriter target, int capacity = DefaultCapacity)
    {
        _target = target;
        _lines = Channel.CreateBounded<string>(new BoundedChannelOptions(capacity) { SingleReader = true });
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>Completes, with why, when nobody reads the lines any more.</summary>
    public Task<string> Gone => _gone.Task;

    /// <summary>Queues one line; never waits. A queue that is full means the reader stopped reading.</summary>
    public void WriteLine(string line)
    {
        if (_gone.Task.IsCompleted)
            return;
        if (!_lines.Writer.TryWrite(line))
            _gone.TrySetResult("The output of queuey listen is not being read.");
    }

    /// <summary>Writes what is queued, within <paramref name="timeout"/>, and takes no more lines.</summary>
    public async Task CompleteAsync(TimeSpan timeout)
    {
        _lines.Writer.TryComplete();
        try { await _pump.WaitAsync(timeout).ConfigureAwait(false); }
        catch (TimeoutException) { /* a reader that does not read gets nothing more */ }
    }

    /// <summary>
    /// Where the lines go in a real run: file descriptor 1 itself, not <see cref="Console.Out"/>, which pretends a write
    /// to a closed pipe succeeded. On Windows, the console's stream.
    /// </summary>
    public static TextWriter OpenStdout()
    {
        if (CliHost.StreamOut is { } test)
            return test;

        Stream stream = OperatingSystem.IsWindows()
            ? Console.OpenStandardOutput()
            : new FileStream(new SafeFileHandle((IntPtr)1, ownsHandle: false), FileAccess.Write, bufferSize: 0);
        return new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = false };
    }

    private async Task PumpAsync()
    {
        await foreach (string line in _lines.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await _target.WriteLineAsync(line).ConfigureAwait(false);
                await _target.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                _gone.TrySetResult($"The output of queuey listen was closed: {ex.Message}");
                return;
            }
        }
    }
}
