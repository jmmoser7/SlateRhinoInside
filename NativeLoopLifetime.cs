using System;
using Rhino;

namespace SlateRhinoInside;

// Rhino's native loop can outlive its accepted main-window close when hosted.
// End only this helper's loop after the original HWND is gone AND no Rhino
// documents remain. Hiding, parking, canceled close, and Preserve do not qualify.
internal sealed class NativeLoopLifetime : IDisposable
{
    private readonly IntPtr window;
    private readonly RuntimeTrace trace;
    private System.Windows.Forms.Timer? timer;
    private uint process, thread;
    private bool verified, refused, disposed, destroyedLogged, hiddenChecked, quitPosted;

    private NativeLoopLifetime(IntPtr mainWindow, RuntimeTrace diagnostics)
    { window = mainWindow; trace = diagnostics; }

    internal static NativeLoopLifetime Start(IntPtr mainWindow, RuntimeTrace trace)
    {
        var lifetime = new NativeLoopLifetime(mainWindow, trace);
        try
        {
            lifetime.process = Native.GetCurrentProcessId();
            lifetime.thread = Native.GetWindowThreadProcessId(mainWindow, out uint windowProcess);
            lifetime.verified = mainWindow != IntPtr.Zero && Native.IsWindow(mainWindow)
                && lifetime.process != 0 && windowProcess == lifetime.process
                && lifetime.thread != 0 && lifetime.thread == Native.GetCurrentThreadId();
            if (!lifetime.verified) { lifetime.Refuse(); return lifetime; }
            // WinForms timers dispatch on their creating UI thread through the
            // existing native message pump; no worker can post a quit here.
            lifetime.timer = new System.Windows.Forms.Timer { Interval = 250 };
            lifetime.timer.Tick += lifetime.Tick;
            lifetime.timer.Start();
        }
        catch { lifetime.Refuse(); }
        return lifetime;
    }

    private void Tick(object? sender, EventArgs args)
    {
        if (disposed || refused || quitPosted || !verified) return;
        try
        {
            uint currentProcess = Native.GetCurrentProcessId(), currentThread = Native.GetCurrentThreadId();
            if (currentProcess != process || currentThread != thread) { Refuse(); return; }
            if (Native.IsWindow(window))
            {
                uint liveThread = Native.GetWindowThreadProcessId(window, out uint liveProcess);
                if (liveProcess != process || liveThread != thread) { Refuse(); return; }
                // A live or reused HWND always prevents loop shutdown. This
                // optional observation never treats a hidden window as closed.
                bool visible = Native.IsWindowVisible(window);
                if (visible) hiddenChecked = false;
                else if (!hiddenChecked)
                {
                    hiddenChecked = true;
                    try
                    {
                        if (RhinoDoc.OpenDocuments(true).Length == 0)
                            trace.Stage(RuntimeStage.MainHiddenWithNoDocs);
                    }
                    catch { } // Trace-only failure cannot change eligibility.
                }
                return;
            }
            if (!destroyedLogged)
            {
                destroyedLogged = true;
                trace.Stage(RuntimeStage.NativeMainDestroyed);
            }
            int documents = RhinoDoc.OpenDocuments(true).Length;
            var state = new NativeLoopState {
                InitiallyVerified = verified, Process = process, UiThread = thread,
                CurrentProcess = currentProcess, CurrentThread = currentThread,
                // Recheck after the SDK call; handle reuse must refuse cleanup.
                MainWindowExists = Native.IsWindow(window), OpenDocuments = documents,
                AlreadyPosted = quitPosted
            };
            if (!NativeLoopPolicy.ShouldQuit(state)) return;
            quitPosted = true;
            timer?.Stop();
            trace.Stage(RuntimeStage.OwnLoopQuitPosted);
            Native.PostQuitMessage(0);
        }
        catch { Refuse(); }
    }

    private void Refuse()
    {
        if (!refused) trace.Stage(RuntimeStage.OwnLoopGuardUnavailable);
        refused = true;
        Dispose();
    }

    public void Dispose()
    {
        disposed = true;
        try
        {
            if (timer == null) return;
            timer.Stop();
            timer.Tick -= Tick;
            timer.Dispose();
        }
        catch { }
        finally { timer = null; }
    }
}

// Pure lifetime policy stays independent of RhinoCore for --self-test.
internal struct NativeLoopState
{
    internal bool InitiallyVerified, MainWindowExists, AlreadyPosted;
    internal uint Process, UiThread, CurrentProcess, CurrentThread;
    internal int? OpenDocuments;
}

internal static class NativeLoopPolicy
{
    internal static bool ShouldQuit(NativeLoopState state) =>
        state.InitiallyVerified && !state.AlreadyPosted && !state.MainWindowExists
        && state.Process != 0 && state.Process == state.CurrentProcess
        && state.UiThread != 0 && state.UiThread == state.CurrentThread
        && state.OpenDocuments == 0;
}
