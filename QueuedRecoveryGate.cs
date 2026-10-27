using System;
using System.Diagnostics;
using System.Threading;

namespace SlateRhinoInside;

// Both terminal transport loss and parent exit share this adapter-level gate.
// Execution and adapter disposal occur on the same recorded Rhino UI thread.
internal sealed class QueuedRecoveryGate : IDisposable
{
    private int state; // 0 ready, 1 queued, 2 executed, 3 disposed
    internal bool TryRequest() => Interlocked.CompareExchange(ref state, 1, 0) == 0;
    internal bool TryExecute() => Interlocked.CompareExchange(ref state, 2, 1) == 1;
    public void Dispose() => Interlocked.Exchange(ref state, 3);
}

// Observe the Process object already authenticated by ExpectedPeer. Its held
// handle identifies that process lifetime even if Windows later reuses its PID.
internal sealed class PeerExitSubscription : IDisposable
{
    private readonly Process process;
    private readonly Action requestRecovery;
    private int notified, disposed;
    internal PeerExitSubscription(Process process, Action requestRecovery)
    {
        this.process = process;
        this.requestRecovery = requestRecovery;
        process.Exited += OnExit;
        try
        {
            process.EnableRaisingEvents = true;
            if (process.HasExited) OnExit(null, EventArgs.Empty);
        }
        catch { Dispose(); throw; }
    }
    private void OnExit(object? sender, EventArgs args)
    {
        if (Volatile.Read(ref disposed) != 0 || Interlocked.Exchange(ref notified, 1) != 0) return;
        // This callback only queues the existing UI restoration owner. It does
        // not inspect Rhino, restore HWNDs, close documents or end the process.
        try { requestRecovery(); }
        catch (Exception error) { Console.Error.WriteLine("Parent recovery request: " + error.GetType().Name); }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) process.Exited -= OnExit;
    }
}
