using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace SlateRhinoInside;
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        RuntimeTrace? trace = null;
        int exitCode = 1;
        try
        {
            if (Array.IndexOf(args, "--self-test") >= 0) return SelfTest.Run();
            var options = Options.Parse(args);
            trace = RuntimeTrace.Start(options.SourceRoot);
            RhinoInside.Resolver.Initialize(options.RhinoPath);
            exitCode = RunRhino(options, trace);
            return exitCode;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message); return 1; }
        finally { trace?.Stage(RuntimeStage.BeforeMainReturn, exitCode); }
    }

    // Keep RhinoCommon references out of Main's JIT until the official resolver is installed.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunRhino(Options options, RuntimeTrace trace)
    {
        using var peer = new ExpectedPeer(options.HostPid);
        // Never accept a scheme name from connector or board/model input.
        string privateScheme = NativeUiScalePolicy.SchemeName(Guid.NewGuid());
        var core = new Rhino.Runtime.InProcess.RhinoCore(
            new[] { "/nosplash", "/notemplate", "/scheme=" + privateScheme }, Rhino.Runtime.InProcess.WindowStyle.Normal);
        try
        {
            var adapter = new RhinoAdapter(options, trace, privateScheme);
            try
            {
                using var hostExit = peer.WatchExit(adapter.ConnectionLost);
                using var loop = NativeLoopLifetime.Start(Rhino.RhinoApp.MainWindowHandle(), trace);
                var server = new ConnectorSession(options, peer, adapter);
                Task.Run(async () =>
                {
                    try { await server.RunAsync(); }
                    catch (Exception ex) { Console.Error.WriteLine("Connector: " + ex.GetType().Name + ": " + ex.Message); }
                    finally { adapter.ConnectionLost(); }
                });
                trace.Stage(RuntimeStage.BeforeRun);
                int result = core.Run();
                trace.Stage(RuntimeStage.AfterRun, result);
                return result;
            }
            finally
            {
                trace.Stage(RuntimeStage.BeforeAdapterDispose);
                adapter.Dispose();
                trace.Stage(RuntimeStage.AfterAdapterDispose);
            }
        }
        finally
        {
            trace.Stage(RuntimeStage.BeforeCoreDispose);
            core.Dispose();
            trace.Stage(RuntimeStage.AfterCoreDispose);
        }
    }
}

internal sealed class Options
{
    public string Token = "";
    public int HostPid;
    public string SourceRoot = "";
    public string RhinoPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Rhino 8");
    public static Options Parse(string[] args)
    {
        var result = new Options();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i += 2)
        {
            if (i + 1 == args.Length) throw new ArgumentException("Missing option value");
            string name = args[i], value = args[i + 1];
            if (!seen.Add(name)) throw new ArgumentException("Duplicate option");
            switch (name)
            {
                case "--slate-token": case "--token": result.Token = value; break;
                case "--slate-pid": case "--host-pid": result.HostPid = int.Parse(value); break;
                case "--source-root": result.SourceRoot = Path.GetFullPath(value); break;
                case "--rhino-path": result.RhinoPath = Path.GetFullPath(value); break;
                default: throw new ArgumentException("Unknown option: " + name);
            }
        }
        if (result.Token.Length != 64 || !System.Text.RegularExpressions.Regex.IsMatch(result.Token, "\\A[0-9a-f]{64}\\z"))
            throw new ArgumentException("A fresh lowercase 256-bit connector token is required");
        if (result.HostPid <= 0 || !Directory.Exists(result.SourceRoot)) throw new ArgumentException("Host PID and approved existing source root are required");
        return result;
    }
}

