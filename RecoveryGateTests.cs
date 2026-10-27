using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SlateRhinoInside;
internal static partial class SelfTest
{
    private static void CheckQueuedRecovery(Action<bool> check)
    {
        var gate = new QueuedRecoveryGate();
        check(!gate.TryExecute()); // No callback may restore before a terminal request.
        var queued = new List<Action>();
        int restores = 0;
        void Request()
        {
            if (gate.TryRequest()) queued.Add(() => { if (gate.TryExecute()) restores++; });
        }
        Request(); Request(); // Parent-exit and pipe-finally converge on one adapter owner.
        check(queued.Count == 1);
        queued[0](); queued[0]();
        check(restores == 1 && !gate.TryRequest());

        gate = new QueuedRecoveryGate(); queued.Clear();
        Request(); gate.Dispose(); queued[0]();
        check(restores == 1 && !gate.TryRequest()); // Unsubscription cannot retract a queued callback.
        gate = new QueuedRecoveryGate(); gate.Dispose();
        check(!gate.TryRequest() && !gate.TryExecute()); // Exit races disposal before queueing.

        var raced = new QueuedRecoveryGate();
        int requests = 0;
        Parallel.For(0, 64, _ => { if (raced.TryRequest()) Interlocked.Increment(ref requests); });
        check(requests == 1 && raced.TryExecute() && !raced.TryExecute());
        raced.Dispose();
        check(!raced.TryRequest() && !raced.TryExecute());
    }
}
