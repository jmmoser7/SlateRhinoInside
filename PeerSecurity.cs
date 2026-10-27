using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace SlateRhinoInside;
internal sealed class ExpectedPeer : IDisposable
{
    private readonly Process process;
    private readonly long created;
    private readonly string sid;
    private readonly int session;
    public int Pid { get; }
    public ExpectedPeer(int pid)
    {
        process = Process.GetProcessById(pid);
        Pid = pid;
        created = process.StartTime.ToFileTimeUtc();
        session = process.SessionId;
        sid = UserSid(process.Handle);
        using var local = Process.GetCurrentProcess();
        if (session != local.SessionId || sid != UserSid(local.Handle)) throw new UnauthorizedAccessException("Peer must have current user and OS session");
        VerifyAlive();
    }
    public void VerifyAlive()
    {
        if (process.HasExited) throw new IOException("Slate host exited");
        using var current = Process.GetProcessById(Pid);
        if (current.StartTime.ToFileTimeUtc() != created || current.SessionId != session || UserSid(current.Handle) != sid)
            throw new UnauthorizedAccessException("Peer process identity changed");
    }
    public void Verify(PipeStream pipe, bool server)
    {
        VerifyAlive();
        uint pid, observedSession;
        bool ok = server ? Native.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out pid) : Native.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out pid);
        if (!ok) throw new Win32Exception();
        ok = server ? Native.GetNamedPipeClientSessionId(pipe.SafePipeHandle, out observedSession) : Native.GetNamedPipeServerSessionId(pipe.SafePipeHandle, out observedSession);
        if (!ok) throw new Win32Exception();
        if (pid != Pid || observedSession != session) throw new UnauthorizedAccessException("Wrong named-pipe peer");
        VerifyAlive();
    }
    private static string UserSid(IntPtr handle)
    {
        if (!Native.OpenProcessToken(handle, 8, out var token)) throw new Win32Exception();
        using (token) using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            return identity.User?.Value ?? throw new UnauthorizedAccessException("Missing user SID");
    }
    internal IDisposable WatchExit(Action requestRecovery) => new PeerExitSubscription(process, requestRecovery);
    public void Dispose() => process.Dispose();
}

internal static class Native
{
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetNamedPipeServerSessionId(SafePipeHandle pipe, out uint session);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint session);
    [DllImport("advapi32.dll", SetLastError = true)] internal static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("kernel32.dll")] internal static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentProcessId();
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [StructLayout(LayoutKind.Sequential)] internal struct SecurityAttributes { internal int Length; internal IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] internal bool Inherit; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint maximum, uint outBuffer, uint inBuffer, uint timeout, ref SecurityAttributes security);
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { internal int Left,Top,Right,Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct GuiThreadInfo
    {
        internal uint Size, Flags;
        internal IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        internal Rect CaretRect;
    }
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hwnd, out Rect rectangle);
    [DllImport("user32.dll")] internal static extern int GetWindowRgnBox(IntPtr hwnd, out Rect rectangle);
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowEnabled(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool IsChild(IntPtr parent, IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern void PostQuitMessage(int exitCode);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] internal static extern bool ShowWindowAsync(IntPtr hwnd, int command);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] internal static extern bool EnableWindow(IntPtr hwnd, bool enabled);
    [DllImport("user32.dll")] internal static extern IntPtr SetFocus(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern IntPtr GetMenu(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)] internal static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SetParent(IntPtr hwnd, IntPtr parent);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] internal static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}

