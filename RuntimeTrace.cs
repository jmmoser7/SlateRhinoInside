using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SlateRhinoInside;
internal enum RuntimeStage
{
    TraceStarted, BeforeRun, AfterRun, BeforeAdapterDispose, AfterAdapterDispose,
    BeforeCoreDispose, AfterCoreDispose, BeforeMainReturn, ProcessExit,
    NativeMainDestroyed, OwnLoopQuitPosted, OwnLoopGuardUnavailable, MainHiddenWithNoDocs, EscapeHookAlive, EscapeState
}

// Optional lifecycle evidence only. This never controls Rhino, documents, or
// process lifetime, and records no launch arguments, tokens, paths, or geometry.
internal sealed class RuntimeTrace
{
    private const int MaximumEvents = 16, MaximumBytes = 16 * 1024;
    private readonly object gate = new object();
    private readonly uint process = Native.GetCurrentProcessId();
    private FileStream? stream;
    private int events, bytes, inputDiagnostics;

    internal static RuntimeTrace Start(string approvedRoot)
    {
        var trace = new RuntimeTrace();
        try
        {
            string root = Path.GetFullPath(RhinoPaths.ForNativeApi(approvedRoot));
            if (root.StartsWith(@"\\", StringComparison.Ordinal)
                || new DriveInfo(Path.GetPathRoot(root)!).DriveType != DriveType.Fixed
                || !Directory.Exists(root) || !LocalPath(root)) return trace;
            string directory = Path.Combine(root, ".slate-rhino-logs");
            if (File.Exists(directory) || !LocalPath(directory)) return trace;
            Directory.CreateDirectory(directory);
            if (!LocalPath(directory)) return trace;
            string path = Path.Combine(directory, "helper-" + trace.process + ".jsonl");
            // CreateNew never follows or overwrites a pre-existing log file.
            trace.stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            AppDomain.CurrentDomain.ProcessExit += (_, _) => {
                trace.Stage(RuntimeStage.ProcessExit);
                trace.Close();
            };
            trace.Stage(RuntimeStage.TraceStarted);
        }
        catch { trace.Close(); }
        return trace;
    }

    // At most four records: one hook-alive marker and three Escape-only gate
    // snapshots. Never record arbitrary keys; reserve lifecycle log capacity.
    internal void EscapeDiagnostic(RuntimeStage stage, int? code = null)
    {
        lock (gate)
        {
            if (inputDiagnostics >= 4) return;
            inputDiagnostics++;
            Stage(stage, code);
        }
    }

    internal void Stage(RuntimeStage stage, int? code = null)
    {
        lock (gate)
        {
            try
            {
                if (stream == null || events >= MaximumEvents) return;
                var record = new JObject {
                    ["stage"] = stage.ToString(), ["utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    ["pid"] = process, ["managed_thread"] = Thread.CurrentThread.ManagedThreadId
                };
                if (code.HasValue) record["code"] = code.Value;
                byte[] line = Encoding.UTF8.GetBytes(record.ToString(Formatting.None) + "\n");
                if (line.Length > MaximumBytes - bytes) return;
                stream.Write(line, 0, line.Length);
                stream.Flush();
                events++; bytes += line.Length;
            }
            catch { Close(); }
        }
    }

    private static bool LocalPath(string path)
    {
        for (string? current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current))
                && ((uint)File.GetAttributes(current) & (0x400u | 0x1000u | 0x40000u | 0x400000u)) != 0)
                return false;
        return true;
    }

    private void Close()
    {
        lock (gate)
        {
            try { stream?.Dispose(); } catch { }
            stream = null;
        }
    }
}
