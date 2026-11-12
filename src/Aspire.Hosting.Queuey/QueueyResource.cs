using System;
using System.Threading.Tasks;
using Aspire.Hosting.ApplicationModel;
using Queuey.Server.Hosting;

namespace Aspire.Hosting;

/// <summary>
/// A self-hosted Queuey on this machine, run by <see cref="QueueyServer.EnsureRunningAsync"/>: the installation
/// <c>queuey server</c> uses. It is not a container of the AppHost's; it keeps running, with its data, when the AppHost stops.
/// </summary>
public sealed class QueueyResource : Resource
{
    internal QueueyResource(string name, QueueyServerOptions options) : base(name) => Options = options;

    /// <summary>How the installation is run: the version, the folder, the ports.</summary>
    public QueueyServerOptions Options { get; }

    // Fylles når installasjonen kjører; WithReference venter på den.
    internal TaskCompletionSource<QueueyServerConnection> Connection { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
