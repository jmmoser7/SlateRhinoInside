using System;
using System.Runtime.InteropServices;
using System.Linq;
using Rhino;

namespace SlateRhinoInside;
internal sealed partial class RhinoAdapter
{
    private bool keyboardHookSeen;
    private int escapeObserved, lastEscapeMask;
    // Commands, menus and dialogs keep native Escape. Only the validated idle
    // body can hand focus back to its authenticated Slate owner.
    private void OnNativeEscape(object? sender, EventArgs args)
    {
        try
        {
            uint uiThread = Native.GetWindowThreadProcessId(window, out uint process);
            if (uiThread == 0 || uiThread != Native.GetCurrentThreadId()
                || process != Native.GetCurrentProcessId())
            {
                trace.EscapeDiagnostic(RuntimeStage.EscapeState, -1);
                return;
            }
            ForwardIdleEscape(0x1b);
        }
        catch { trace.EscapeDiagnostic(RuntimeStage.EscapeState, -2); }
    }
    private void ForwardIdleEscape(int key)
    {
        if (!keyboardHookSeen)
        {
            keyboardHookSeen = true;
            trace.EscapeDiagnostic(RuntimeStage.EscapeHookAlive);
        }
        if (key != 0x1b) return;
        escapeObserved++;
        int commandDepth = RhinoApp.InCommand;
        var hosting = ReadSurfaceHost(window, originalParent, originalOwner);
        uint thread = hosting.Thread, process = hosting.Process, hostProcess = hosting.HostProcess;
        bool child = hosting.Child;
        IntPtr host = hosting.Host;
        var gui = new Native.GuiThreadInfo { Size = (uint)Marshal.SizeOf<Native.GuiThreadInfo>() };
        bool guiAvailable = thread != 0 && Native.GetGUIThreadInfo(thread, ref gui);
        var state = new EscapeRelayState {
            Key = key, CommandDepth = commandDepth, Attached = attached,
            NativeSelection = HasNativeSelection(RhinoDoc.ActiveDoc),
            Window = Native.IsWindow(window) ? window : IntPtr.Zero,
            WindowProcess = process, HelperProcess = Native.GetCurrentProcessId(),
            WindowVisible = Native.IsWindowVisible(window), WindowEnabled = Native.IsWindowEnabled(window),
            Host = host, OriginalHost = hosting.OriginalHost,
            HostValid = hosting.HostValid, HostProcess = hostProcess, ExpectedHostProcess = options.HostPid,
            ChildWindow = child, ActiveRoot = child ? Native.GetAncestor(host, 2) : window,
            Foreground = Native.GetForegroundWindow(), GuiAvailable = guiAvailable, Gui = gui,
            FocusValid = Native.IsWindow(gui.Focus),
            FocusInBody = gui.Focus == window || Native.IsChild(window, gui.Focus)
        };
        int mask = EscapeRelayPolicy.DiagnosticMask(state);
        if (EscapeRelayPolicy.ShouldRelay(state))
        {
            // Background synthetic key messages can be ignored by the host UI.
            // A real idle Escape authorizes this one verified focus handoff;
            // the native foreground process is eligible to request it.
            bool focused = Native.GetForegroundWindow() == host || Native.SetForegroundWindow(host);
            if (focused)
            {
                mask |= 1 << 14;
                bool down = Native.PostMessage(host, 0x0100, new IntPtr(0x1b), new IntPtr(0x00010001));
                bool up = Native.PostMessage(host, 0x0101, new IntPtr(0x1b), new IntPtr(unchecked((long)0xc0010001)));
                if (down && up) mask |= 1 << 15;
            }
        }
        lastEscapeMask = mask;
        trace.EscapeDiagnostic(RuntimeStage.EscapeState, mask);
    }
    private static bool HasNativeSelection(RhinoDoc? doc) => doc != null
        && (doc.Objects.GetSelectedObjects(true, true).Any()
            || doc.Objects.GetObjectList(Rhino.DocObjects.ObjectType.AnyObject)
                .Any(item => item.IsSelected(true) != 0));

    public void Dispose()
    {
        recovery.Dispose(); // A previously queued callback cannot touch disposed native state.
        RhinoApp.EscapeKeyPressed -= OnNativeEscape;
        RhinoApp.Idle -= OnUiScaleIdle;
        RestoreWindow();
        windowRecovery.Dispose();
        grasshopperRecovery?.Dispose();
    }
}

// Pure input policy is separate from RhinoAdapter so self-tests do not load
// RhinoCore. Native key delivery is never consumed or canceled.
internal struct EscapeRelayState
{
    internal int Key, CommandDepth, ExpectedHostProcess;
    internal uint WindowProcess, HelperProcess, HostProcess;
    internal bool Attached, WindowVisible, WindowEnabled, HostValid, ChildWindow, NativeSelection;
    internal bool GuiAvailable, FocusValid, FocusInBody;
    internal IntPtr Window, Host, OriginalHost, ActiveRoot, Foreground;
    internal Native.GuiThreadInfo Gui;
}

internal static class EscapeRelayPolicy
{
    // GUI_INMOVESIZE | GUI_INMENUMODE | GUI_SYSTEMMENUMODE | GUI_POPUPMENUMODE.
    private const uint NativeInteraction = 0x1e;
    internal static bool IsIdleEscape(int key, int commandDepth, bool attached) =>
        key == 0x1b && commandDepth == 0 && attached;

    // Fixed flags only, no key values/handles/content. Bits0..12 describe gates;
    // bit13 is eligibility; bits14/15 are successful focus/message handoff.
    internal static int DiagnosticMask(EscapeRelayState s) =>
        (s.Attached ? 1 : 0) | (s.CommandDepth == 0 ? 2 : 0)
        | (s.Window != IntPtr.Zero && s.HelperProcess != 0 && s.WindowProcess == s.HelperProcess ? 4 : 0)
        | (s.WindowVisible ? 8 : 0) | (s.WindowEnabled ? 16 : 0)
        | (s.HostValid && s.Host != IntPtr.Zero && s.Host != s.OriginalHost ? 32 : 0)
        | (s.ExpectedHostProcess > 0 && s.HostProcess == (uint)s.ExpectedHostProcess ? 64 : 0)
        | (s.GuiAvailable ? 128 : 0) | (s.ActiveRoot != IntPtr.Zero && s.Foreground == s.ActiveRoot ? 256 : 0)
        | (s.Gui.Active == s.ActiveRoot && s.ActiveRoot != IntPtr.Zero ? 512 : 0)
        | (s.FocusValid && s.Gui.Focus != IntPtr.Zero && s.FocusInBody ? 1024 : 0)
        | ((s.Gui.Flags & NativeInteraction) == 0 ? 2048 : 0)
        | (s.Gui.MenuOwner == IntPtr.Zero && s.Gui.MoveSize == IntPtr.Zero && s.Gui.Capture == IntPtr.Zero ? 4096 : 0)
        | (ShouldRelay(s) ? 8192 : 0) | (s.NativeSelection ? 1 << 16 : 0);

    internal static bool ShouldRelay(EscapeRelayState state) =>
        IsIdleEscape(state.Key, state.CommandDepth, state.Attached) && !state.NativeSelection
        && NativeSurfaceAuthority.MatchesOwner(state.Window, state.WindowProcess, state.HelperProcess,
            state.Host, state.OriginalHost, state.HostValid, state.HostProcess, state.ExpectedHostProcess)
        && state.WindowVisible && state.WindowEnabled
        && state.ActiveRoot != IntPtr.Zero && (state.ChildWindow || state.ActiveRoot == state.Window)
        && state.GuiAvailable && state.Foreground == state.ActiveRoot && state.Gui.Active == state.ActiveRoot
        && state.FocusValid && state.Gui.Focus != IntPtr.Zero && state.FocusInBody
        && (state.Gui.Flags & NativeInteraction) == 0
        && state.Gui.MenuOwner == IntPtr.Zero && state.Gui.MoveSize == IntPtr.Zero
        && state.Gui.Capture == IntPtr.Zero;
}
