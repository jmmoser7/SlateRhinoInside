using System;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using Rhino;

namespace SlateRhinoInside;
internal sealed partial class RhinoAdapter
{
    private IntPtr grasshopperWindow;
    private NativeWindowRecovery? grasshopperRecovery;
    private Form? grasshopperForm;
    private bool grasshopperAttached;
    private object? grasshopperInterface;
    private JObject ShowGrasshopper()
    {
        // Only this documented plugin interface is used. No script text,
        // Grasshopper definition, arbitrary type or assembly path is accepted.
        grasshopperInterface = RhinoApp.GetPlugInObject("Grasshopper")
            ?? throw new ProtocolFailure("unsupported_operation", "Grasshopper is not installed in this Rhino runtime");
        CallGrasshopper("LoadEditor");
        var form = CurrentGrasshopperForm()
            ?? throw new ProtocolFailure("invalid_result", "Grasshopper did not create its native editor");
        IntPtr handle = form.Handle;
        uint thread = Native.GetWindowThreadProcessId(handle, out uint pid);
        if (!Native.IsWindow(handle) || pid != Native.GetCurrentProcessId() || thread != Native.GetCurrentThreadId())
            throw new ProtocolFailure("invalid_result", "Grasshopper editor identity is not owned by this helper UI thread");
        if (handle != grasshopperWindow || !ReferenceEquals(form, grasshopperForm))
        {
            grasshopperRecovery?.Dispose();
            grasshopperRecovery = new NativeWindowRecovery(handle);
            grasshopperWindow = handle; grasshopperForm = form; grasshopperAttached = false;
        }
        var hosting = ReadSurfaceHost(handle, grasshopperRecovery?.Parent ?? IntPtr.Zero, grasshopperRecovery?.Owner ?? IntPtr.Zero);
        // Initial loading only offers the HWND. The host attaches and places it,
        // then repeats this closed operation to reveal the authenticated surface.
        if (hosting.OwnerMatches && hosting.OwnUiThread && hosting.Enabled && hosting.ClipHasArea && hosting.ForegroundEligible)
        {
            CallGrasshopper("ShowEditor");
            var shown = ReadSurfaceHost(handle, grasshopperRecovery?.Parent ?? IntPtr.Zero, grasshopperRecovery?.Owner ?? IntPtr.Zero);
            if (!shown.OwnerMatches || !shown.ClipHasArea || !shown.ForegroundEligible) CallGrasshopper("HideEditor");
            else if (NativeSurfaceAuthority.Allows(shown.OwnerMatches, shown.OwnUiThread, shown.Visible, shown.Enabled, shown.ClipHasArea, shown.ForegroundEligible, true))
                Native.SetForegroundWindow(handle);
        }
        else CallGrasshopper("HideEditor"); // LoadEditor does not promise a hidden initial editor.
        return new JObject { ["surface_id"] = "grasshopper", ["surface"] = GrasshopperSurface(),
            ["visible"] = Native.IsWindowVisible(handle), ["hosted"] = hosting.OwnerMatches };
    }
    private void CallGrasshopper(string method)
    {
        var api = grasshopperInterface ?? throw new ProtocolFailure("source_not_bound", "Grasshopper editor is not loaded");
        var member = api.GetType().GetMethod(method, Type.EmptyTypes)
            ?? throw new ProtocolFailure("unsupported_operation", "Installed Grasshopper lacks " + method);
        member.Invoke(api, Array.Empty<object>());
    }
    private JObject HideGrasshopper()
    {
        if (GrasshopperSurface() == null) return new JObject { ["surface_id"] = "grasshopper", ["visible"] = false, ["loaded"] = false };
        CallGrasshopper("HideEditor");
        return new JObject { ["surface_id"] = "grasshopper", ["visible"] = Native.IsWindowVisible(grasshopperWindow), ["loaded"] = true };
    }
    private JObject? GrasshopperSurface()
    {
        if (grasshopperWindow == IntPtr.Zero || !Native.IsWindow(grasshopperWindow)) return null;
        uint thread = Native.GetWindowThreadProcessId(grasshopperWindow, out uint pid);
        if (pid != Native.GetCurrentProcessId() || thread != Native.GetCurrentThreadId()) return null;
        var form = CurrentGrasshopperForm();
        if (!ReferenceEquals(form, grasshopperForm) || form == null || form.IsDisposed || !form.IsHandleCreated || form.Handle != grasshopperWindow) return null;
        return new JObject { ["surface_id"] = "grasshopper", ["mode"] = "native_full_ui", ["process_ownership"] = "launched_dedicated",
            ["native_handle"] = grasshopperWindow.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture), ["can_detach"] = true };
    }
    private static Form? CurrentGrasshopperForm()
    {
        try
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Grasshopper");
            var form = assembly?.GetType("Grasshopper.Instances")?.GetProperty("DocumentEditor", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as Form;
            return form != null && !form.IsDisposed ? form : null;
        }
        catch { return null; }
    }
    private void GrasshopperAction(JObject action)
    {
        var surface = GrasshopperSurface() ?? throw new ProtocolFailure("source_not_bound", "Grasshopper surface is not loaded");
        switch ((string?)action["action"])
        {
            case "attach":
                if (grasshopperAttached || !JToken.DeepEquals(surface, action["surface"])) throw new ProtocolFailure("invalid_request", "Invalid Grasshopper surface attachment");
                grasshopperAttached = true; break;
            case "show": RequireHostedSurface(grasshopperWindow, grasshopperRecovery?.Parent ?? IntPtr.Zero, grasshopperRecovery?.Owner ?? IntPtr.Zero, false); CallGrasshopper("ShowEditor"); break;
            case "hide": CallGrasshopper("HideEditor"); break;
            case "focus": RequireHostedSurface(grasshopperWindow, grasshopperRecovery?.Parent ?? IntPtr.Zero, grasshopperRecovery?.Owner ?? IntPtr.Zero, true); Native.SetForegroundWindow(grasshopperWindow); break;
            case "blur": Native.SetFocus(IntPtr.Zero); break;
            case "place": break; // The host NativeSurface remains the only placement owner.
            default: throw new ProtocolFailure("invalid_request", "Unknown Grasshopper surface action");
        }
    }
    private void RestoreGrasshopper()
    {
        try
        {
            if (GrasshopperSurface() == null) return;
            grasshopperRecovery?.Restore();
            grasshopperAttached = false;
        }
        catch { } // A closed editor is never recreated by detach cleanup.
    }
}
