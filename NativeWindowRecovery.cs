using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SlateRhinoInside;

// This snapshot is only crash/disconnect recovery. Slate's existing NativeSurface
// remains the normal attachment/placement owner and restores before clean detach.
internal sealed class NativeWindowRecovery : IDisposable
{
    private readonly IntPtr window, style, extendedStyle;
    private readonly uint process, thread;
    private readonly Placement placement;
    private IntPtr region;
    private bool disposed;
    internal IntPtr Parent { get; }
    internal IntPtr Owner { get; }
    internal NativeWindowRecovery(IntPtr handle)
    {
        window = handle;
        thread = Native.GetWindowThreadProcessId(handle, out process);
        if (!CanRecover(Native.IsWindow(handle), process, Native.GetCurrentProcessId(), thread, Native.GetCurrentThreadId()))
            throw new InvalidOperationException("Native recovery snapshot requires this helper's UI thread");
        style = Native.GetWindowLongPtr(handle, -16); extendedStyle = Native.GetWindowLongPtr(handle, -20);
        Parent = (style.ToInt64() & 0x40000000L) != 0 ? Native.GetParent(handle) : IntPtr.Zero;
        Owner = Native.GetWindow(handle, 4);
        var saved = new Placement { Length = (uint)Marshal.SizeOf<Placement>() };
        if (!GetWindowPlacement(handle, ref saved)) throw new Win32Exception();
        placement = saved;
        region = CreateRectRgn(0, 0, 0, 0);
        if (region == IntPtr.Zero) throw new Win32Exception();
        // GetWindowRgn returns ERROR when the ordinary window has no custom region.
        if (GetWindowRgn(handle, region) == 0) { DeleteObject(region); region = IntPtr.Zero; }
    }
    internal void Restore()
    {
        uint actualThread = Native.GetWindowThreadProcessId(window, out uint actualProcess);
        if (disposed || actualProcess != process || actualThread != thread
            || !CanRecover(Native.IsWindow(window), actualProcess, Native.GetCurrentProcessId(), actualThread, Native.GetCurrentThreadId())) return;
        Native.SetParent(window, Native.IsWindow(Parent) ? Parent : IntPtr.Zero);
        Native.SetWindowLongPtr(window, -16, style); Native.SetWindowLongPtr(window, -20, extendedStyle);
        if ((style.ToInt64() & 0x40000000L) == 0)
            Native.SetWindowLongPtr(window, -8, Native.IsWindow(Owner) ? Owner : IntPtr.Zero);
        if (region == IntPtr.Zero) SetWindowRgn(window, IntPtr.Zero, true);
        else
        {
            IntPtr copy = CreateRectRgn(0, 0, 0, 0);
            if (copy != IntPtr.Zero)
            {
                if (CombineRgn(copy, region, IntPtr.Zero, 5) == 0 || SetWindowRgn(window, copy, true) == 0) DeleteObject(copy);
                // After successful SetWindowRgn, Windows owns the copied region.
            }
        }
        var restore = placement;
        SetWindowPlacement(window, ref restore);
        Native.SetWindowPos(window, IntPtr.Zero, 0, 0, 0, 0, 0x20 | 0x1 | 0x2 | 0x4 | 0x10);
    }
    internal static bool CanRecover(bool live, uint recordedProcess, uint currentProcess, uint recordedThread, uint currentThread) =>
        live && recordedProcess != 0 && recordedThread != 0 && recordedProcess == currentProcess && recordedThread == currentThread;
    public void Dispose() { if (disposed) return; disposed = true; if (region != IntPtr.Zero) { DeleteObject(region); region = IntPtr.Zero; } }
    [StructLayout(LayoutKind.Sequential)] private struct Point { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Placement
    {
        internal uint Length, Flags, ShowCommand;
        internal Point Minimum, Maximum;
        internal Native.Rect Normal;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowPlacement(IntPtr hwnd, ref Placement value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPlacement(IntPtr hwnd, ref Placement value);
    [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowRgn(IntPtr hwnd, IntPtr region);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern int CombineRgn(IntPtr destination, IntPtr source1, IntPtr source2, int mode);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
}
