using System;
using Newtonsoft.Json.Linq;

namespace SlateRhinoInside;
internal sealed partial class RhinoAdapter
{
    // Read the existing authority owner's predicates; diagnostics never grant
    // presentation, change native state, or initialize graphics.
    private JObject NativePresentationState()
    {
        try
        {
            var state = ReadSurfaceHost(window, originalParent, originalOwner);
            return new JObject {
                ["helper_pid"] = Native.GetCurrentProcessId(),
                ["expected_host_pid"] = options.HostPid,
                ["window_pid"] = state.Process, ["actual_host_pid"] = state.HostProcess,
                ["initial_ui_thread"] = uiScaleThread,
                ["current_thread"] = Native.GetCurrentThreadId(), ["window_thread"] = state.Thread,
                ["window_handle"] = window.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["host_handle"] = state.Host.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["original_host_handle"] = state.OriginalHost.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["window_exists"] = Native.IsWindow(window), ["child_window"] = state.Child,
                ["host_valid"] = state.HostValid, ["owner_matches"] = state.OwnerMatches,
                ["own_ui_thread"] = state.OwnUiThread,
                ["visible"] = state.Visible, ["enabled"] = state.Enabled,
                ["clip_has_area"] = state.ClipHasArea, ["foreground_eligible"] = state.ForegroundEligible,
                ["show_allowed"] = NativeSurfaceAuthority.Allows(state.OwnerMatches, state.OwnUiThread,
                    state.Visible, state.Enabled, state.ClipHasArea, state.ForegroundEligible, false),
                ["focus_allowed"] = NativeSurfaceAuthority.CanRaise(state),
                ["capture_presentation_latched"] = captureReadiness.Presented,
                ["readback_status"] = "available" };
        }
        catch (Exception error)
        {
            return new JObject { ["readback_status"] = "unavailable", ["readback_error"] = error.GetType().Name };
        }
    }
}
