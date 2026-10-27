using System;

namespace SlateRhinoInside;
internal static class NativeSurfaceAuthority
{
    // HWND_TOP is zero. Raise only within the ordinary Z-order, retaining the
    // host-owned geometry and foreground; this never makes a surface topmost.
    internal const uint RaiseFlags = 0x0001 | 0x0002 | 0x0010; // NOSIZE | NOMOVE | NOACTIVATE
    internal static bool MatchesOwner(IntPtr window, uint process, uint helperProcess, IntPtr host,
        IntPtr originalHost, bool hostValid, uint hostProcess, int expectedHostProcess) =>
        window != IntPtr.Zero && helperProcess != 0 && process == helperProcess
        && hostValid && host != IntPtr.Zero && host != window && host != originalHost
        && expectedHostProcess > 0 && hostProcess == (uint)expectedHostProcess;
    internal static bool Allows(bool ownerMatches, bool ownUiThread, bool visible, bool enabled, bool clipHasArea, bool foregroundEligible, bool focus) =>
        ownerMatches && ownUiThread && enabled && clipHasArea && foregroundEligible && (!focus || visible);
    internal static bool CanRaise(NativeSurfaceHostState state) => Allows(state.OwnerMatches, state.OwnUiThread,
        state.Visible, state.Enabled, state.ClipHasArea, state.ForegroundEligible, true);
}
internal struct NativeSurfaceHostState
{
    internal IntPtr Host, OriginalHost;
    internal uint Process, Thread, HostProcess;
    internal bool Child, HostValid, OwnerMatches, OwnUiThread, Visible, Enabled, ClipHasArea, ForegroundEligible;
}
internal sealed partial class RhinoAdapter
{
    private NativeSurfaceHostState ReadSurfaceHost(IntPtr surface, IntPtr originalParent, IntPtr originalOwner)
    {
        uint thread = Native.GetWindowThreadProcessId(surface, out uint process);
        bool child = (Native.GetWindowLongPtr(surface, -16).ToInt64() & 0x40000000L) != 0;
        IntPtr host = child ? Native.GetAncestor(surface, 1) : Native.GetWindow(surface, 4);
        Native.GetWindowThreadProcessId(host, out uint hostProcess);
        bool valid = Native.IsWindow(host);
        IntPtr original = child ? originalParent : originalOwner;
        int region = Native.GetWindowRgnBox(surface, out var bounds);
        Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint foregroundProcess);
        return new NativeSurfaceHostState { Host = host, OriginalHost = original, Process = process, Thread = thread,
            HostProcess = hostProcess, Child = child, HostValid = valid,
            OwnerMatches = NativeSurfaceAuthority.MatchesOwner(Native.IsWindow(surface) ? surface : IntPtr.Zero,
                process, Native.GetCurrentProcessId(), host, original, valid, hostProcess, options.HostPid),
            OwnUiThread = thread != 0 && thread == Native.GetCurrentThreadId(),
            Visible = Native.IsWindowVisible(surface), Enabled = Native.IsWindowEnabled(surface),
            ForegroundEligible = foregroundProcess != 0 && (foregroundProcess == (uint)options.HostPid || foregroundProcess == Native.GetCurrentProcessId()),
            ClipHasArea = region > 1 && bounds.Right > bounds.Left && bounds.Bottom > bounds.Top || child && region == 0 };
    }
    private void RequireHostedSurface(IntPtr surface, IntPtr originalParent, IntPtr originalOwner, bool focus)
    {
        var state = ReadSurfaceHost(surface, originalParent, originalOwner);
        if (!NativeSurfaceAuthority.Allows(state.OwnerMatches, state.OwnUiThread, state.Visible, state.Enabled, state.ClipHasArea, state.ForegroundEligible, focus))
            throw new ProtocolFailure("invalid_request", "Native surface is parked or is not owned by its authenticated Slate host");
    }
    private void RaiseHostedPresentation()
    {
        var state = ReadSurfaceHost(window, originalParent, originalOwner);
        bool presented = NativeSurfaceAuthority.CanRaise(state);
        if (presented && !Native.SetWindowPos(window, IntPtr.Zero, 0, 0, 0, 0, NativeSurfaceAuthority.RaiseFlags))
            throw new ProtocolFailure("invalid_result", "Rhino could not raise its authenticated hosted surface");
        captureReadiness.ObserveHostedPresentation(presented);
    }
}
