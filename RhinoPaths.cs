using System;
using System.Linq;
using System.IO;

namespace SlateRhinoInside;
/// <summary>Translate an already authorized Windows locator at the Rhino API boundary.
/// This never establishes authority: canonical-root and reparse checks run first.</summary>
internal static class RhinoPaths
{
    // Normalize an absolute locator before filesystem existence/containment checks.
    // This does not grant authority; ResolveSource still checks the approved root.
    internal static string ForFileSystem(string path) => Path.GetFullPath(ForNativeApi(path.Replace('/', '\\')));
    // Path continuity alone does not prove file content or clean document state.
    // This performs normalization only; the host owns revision/dirty/hash checks.
    internal static bool IsBoundLinkedPath(string mode, string? current, string? original)
    {
        if (mode != "linked" || string.IsNullOrWhiteSpace(current) || string.IsNullOrWhiteSpace(original)) return false;
        try { return string.Equals(ForFileSystem(current!), ForFileSystem(original!), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
    internal static ProtocolFailure AuxiliaryFailure(ProtocolFailure error) => error.Code == "source_not_bound"
        ? new ProtocolFailure("invalid_request", "Auxiliary model or CSV input file is missing") : error;
    internal static string ForNativeApi(string validatedPath)
    {
        string path = validatedPath;
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            path = @"\\" + path.Substring(8);
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            path = path.Substring(4);
            if (!IsDriveAbsolute(path)) throw new ProtocolFailure("invalid_request", "Rhino requires a DOS or UNC model path, not a device namespace");
        }
        if (path.StartsWith(@"\\.\", StringComparison.Ordinal) || path.StartsWith(@"\\?\", StringComparison.Ordinal))
            throw new ProtocolFailure("invalid_request", "Rhino cannot use a device namespace as a model path");
        bool unc = path.StartsWith(@"\\", StringComparison.Ordinal);
        if (!unc && !IsDriveAbsolute(path)) throw new ProtocolFailure("invalid_request", "Rhino requires an absolute model path");
        string[] segments = path.Split('\\');
        if (unc && (segments.Length < 5 || segments[2].Length == 0 || segments[3].Length == 0))
            throw new ProtocolFailure("invalid_request", "Rhino requires a complete UNC model path");
        // Removing the extended prefix must not change Windows name resolution.
        if (segments.Any(segment => segment.EndsWith(".", StringComparison.Ordinal) || segment.EndsWith(" ", StringComparison.Ordinal)))
            throw new ProtocolFailure("invalid_request", "Rhino cannot address a model path containing a trailing dot or space");
        return path;
    }
    private static bool IsDriveAbsolute(string path) => path.Length >= 3 &&
        ((path[0] >= 'A' && path[0] <= 'Z') || (path[0] >= 'a' && path[0] <= 'z')) && path[1] == ':' && path[2] == '\\';
}
