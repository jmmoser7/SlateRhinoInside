using System;
using System.Diagnostics;
using System.Drawing;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.ApplicationSettings;
using Rhino.UI;

namespace SlateRhinoInside;

internal sealed partial class RhinoAdapter
{
    private string privateUiScheme = "", uiScaleStatus = "waiting_for_hosted_presentation";
    private uint uiScaleThread;
    private readonly Stopwatch uiScaleClock = Stopwatch.StartNew();
    private long nextUiScaleSample;
    private bool uiScaleSuspended, uiScaleStopped;
    private Size? originalToolbarBitmap, originalToolbarTab, appliedToolbarBitmap, appliedToolbarTab;
    private int? originalPromptFont, appliedPromptFont;
    private readonly NativeUiScalePolicy uiScalePolicy = new NativeUiScalePolicy();

    private void InitializeUiScale(string privateScheme)
    {
        privateUiScheme = privateScheme;
        uiScaleThread = Native.GetCurrentThreadId();
        RhinoApp.Idle += OnUiScaleIdle;
    }

    // Slate remains the sole window geometry owner. Observe its actual native
    // size on Rhino's UI thread; no placement, OS DPI, RUI save or model command.
    private void OnUiScaleIdle(object? sender, EventArgs args)
    {
        if (!attached || uiScaleSuspended || uiScaleStopped || uiScaleClock.ElapsedMilliseconds < nextUiScaleSample) return;
        nextUiScaleSample = uiScaleClock.ElapsedMilliseconds + 250;
        try
        {
            if (!PrivateUiScaleScope()) { uiScaleStatus = "private_scheme_or_ui_thread_mismatch"; return; }
            var state = ReadSurfaceHost(window, originalParent, originalOwner);
            if (!state.OwnerMatches || !state.OwnUiThread || !state.Visible || !state.Enabled || !state.ClipHasArea) return;
            if (!Native.GetWindowRect(window, out var bounds)) return;
            var size = new Size(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
            if (!uiScalePolicy.Observe(size, out double scale, out bool first)) return;
            if (first)
            {
                originalToolbarBitmap = Toolbar.BitmapSize;
                originalToolbarTab = Toolbar.TabSize;
                originalPromptFont = AppearanceSettings.CommandPromptFontSize;
                uiScaleStatus = "baseline"; // Factor 1 on first hosted size; no setters.
                return;
            }
            if (!UiScaleValuesUnchanged())
            {
                uiScaleStopped = true; uiScaleStatus = "manual_change_preserved"; return;
            }
            ApplyUiScale(scale);
        }
        catch (Exception)
        {
            // Keep native interaction alive after an unsupported setting. Any
            // successful partial writes retain conditional recovery below.
            uiScaleStopped = true; uiScaleStatus = "setting_failed";
        }
    }

    private bool PrivateUiScaleScope()
    {
        uint thread = Native.GetWindowThreadProcessId(window, out uint process);
        return NativeUiScalePolicy.Allows(privateUiScheme, RhinoApp.SchemeName,
            process, Native.GetCurrentProcessId(), thread, Native.GetCurrentThreadId(), uiScaleThread);
    }

    private bool UiScaleValuesUnchanged() => originalToolbarBitmap.HasValue && originalToolbarTab.HasValue && originalPromptFont.HasValue
        && Toolbar.BitmapSize == (appliedToolbarBitmap ?? originalToolbarBitmap.Value)
        && Toolbar.TabSize == (appliedToolbarTab ?? originalToolbarTab.Value)
        && AppearanceSettings.CommandPromptFontSize == (appliedPromptFont ?? originalPromptFont.Value);

    private void ApplyUiScale(double scale)
    {
        var bitmap = NativeUiScalePolicy.ScaleSize(originalToolbarBitmap!.Value, scale);
        var tab = NativeUiScalePolicy.ScaleSize(originalToolbarTab!.Value, scale);
        int prompt = NativeUiScalePolicy.PromptFontSize(originalPromptFont!.Value, scale);
        // Recheck the exact helper-owned scheme immediately before every setter.
        if (!PrivateUiScaleScope()) return;
        if (Toolbar.BitmapSize != bitmap) { Toolbar.BitmapSize = bitmap; appliedToolbarBitmap = Toolbar.BitmapSize; }
        if (!PrivateUiScaleScope()) return;
        if (Toolbar.TabSize != tab) { Toolbar.TabSize = tab; appliedToolbarTab = Toolbar.TabSize; }
        if (!PrivateUiScaleScope()) return;
        if (AppearanceSettings.CommandPromptFontSize != prompt) { AppearanceSettings.CommandPromptFontSize = prompt; appliedPromptFont = AppearanceSettings.CommandPromptFontSize; }
        uiScaleStatus = "scaled";
    }

    private void ResumeUiScale() { uiScaleSuspended = false; uiScalePolicy.ResetPending(); }
    private void SuspendUiScale() { uiScaleSuspended = true; uiScalePolicy.ResetPending(); }
    private void RestoreUiScale()
    {
        try
        {
            if (PrivateUiScaleScope() && originalToolbarBitmap.HasValue && appliedToolbarBitmap.HasValue && Toolbar.BitmapSize == appliedToolbarBitmap.Value)
                Toolbar.BitmapSize = originalToolbarBitmap.Value;
            if (PrivateUiScaleScope() && originalToolbarTab.HasValue && appliedToolbarTab.HasValue && Toolbar.TabSize == appliedToolbarTab.Value)
                Toolbar.TabSize = originalToolbarTab.Value;
            if (PrivateUiScaleScope() && originalPromptFont.HasValue && appliedPromptFont.HasValue && AppearanceSettings.CommandPromptFontSize == appliedPromptFont.Value)
                AppearanceSettings.CommandPromptFontSize = originalPromptFont.Value;
            appliedToolbarBitmap = appliedToolbarTab = null; appliedPromptFont = null;
            uiScalePolicy.Restored();
        }
        catch { } // Manual changes and scheme mismatches are never overwritten.
    }

    private JObject UiScaleState()
    {
        var result = new JObject {
            ["status"] = uiScaleStatus, ["private_scheme"] = privateUiScheme,
            ["factor"] = uiScalePolicy.Factor,
            ["baseline_width"] = uiScalePolicy.Baseline.Width, ["baseline_height"] = uiScalePolicy.Baseline.Height,
            ["scope"] = "native_toolbar_images_tabs_and_command_prompt_font",
            ["initial_ui"] = "fresh_private_factory_scheme", ["os_dpi_changed"] = false,
            ["full_ui_font_scaling"] = false };
        try
        {
            string actualScheme = RhinoApp.SchemeName;
            uint thread = Native.GetWindowThreadProcessId(window, out uint process);
            uint helper = Native.GetCurrentProcessId(), current = Native.GetCurrentThreadId();
            bool scopeMatches = NativeUiScalePolicy.Allows(privateUiScheme, actualScheme, process, helper, thread, current, uiScaleThread);
            // Retain the legacy combined field; expose its independent gates
            // so a false result is not mistaken for a scheme-name mismatch.
            result["scheme_matches"] = scopeMatches;
            result["private_ui_scope_matches"] = scopeMatches;
            result["actual_scheme"] = actualScheme.Length <= 256 ? actualScheme : actualScheme.Substring(0, 256);
            result["actual_scheme_truncated"] = actualScheme.Length > 256;
            result["private_scheme_name_matches"] = NativeUiScalePolicy.SchemeMatches(privateUiScheme, actualScheme);
            result["ui_thread_scope_matches"] = NativeUiScalePolicy.ThreadMatches(process, helper, thread, current, uiScaleThread);
            result["toolbar_bitmap_width"] = Toolbar.BitmapSize.Width;
            result["toolbar_tab_width"] = Toolbar.TabSize.Width;
            result["command_prompt_font_size"] = AppearanceSettings.CommandPromptFontSize;
            result["readback_status"] = "available";
        }
        catch (Exception error)
        {
            // Optional UI diagnostics must never break the existing model
            // inspector when an installed Rhino build cannot read a setting.
            result["readback_status"] = "unavailable";
            result["readback_error"] = error.GetType().Name;
        }
        return result;
    }
}

// Pure policy is testable without RhinoCore. Every resize uses the original
// hosted size, never the previous resize, avoiding cumulative drift.
internal sealed class NativeUiScalePolicy
{
    private const string Prefix = "SlateRhinoInside-";
    private Size pending;
    internal Size Baseline { get; private set; }
    internal double Factor { get; private set; } = 1;
    internal static string SchemeName(Guid id) => Prefix + id.ToString("N");
    internal static bool SchemeMatches(string expected, string actual) =>
        expected.StartsWith(Prefix, StringComparison.Ordinal)
        && Guid.TryParseExact(expected.Substring(Prefix.Length), "N", out var id)
        && id != Guid.Empty && expected == SchemeName(id)
        && (string.Equals(expected, actual, StringComparison.Ordinal)
            || string.Equals("Scheme: " + expected, actual, StringComparison.Ordinal));
    internal static bool ThreadMatches(uint process, uint helper, uint thread, uint currentThread, uint initialThread) =>
        process != 0 && process == helper && thread != 0 && thread == currentThread && thread == initialThread;
    internal static bool Allows(string expected, string actual, uint process, uint helper, uint thread, uint currentThread, uint initialThread) =>
        SchemeMatches(expected, actual) && ThreadMatches(process, helper, thread, currentThread, initialThread);
    internal bool Observe(Size size, out double factor, out bool first)
    {
        factor = Factor; first = false;
        if (size.Width < 128 || size.Height < 96) return false;
        if (Baseline.IsEmpty) { Baseline = size; pending = size; first = true; return true; }
        if (pending != size) { pending = size; return false; } // Two stable samples.
        double next = Math.Max(0.5, Math.Min(2, Math.Min(size.Width / (double)Baseline.Width, size.Height / (double)Baseline.Height)));
        if (Math.Abs(next - Factor) < 0.025) return false;
        factor = Factor = next; return true;
    }
    internal void ResetPending() => pending = Size.Empty;
    internal void Restored() { Factor = 1; ResetPending(); }
    internal static int Dimension(int value, double scale, int minimum, int maximum) => value <= 0 ? value : Math.Max(minimum, Math.Min(maximum, (int)Math.Round(value * scale)));
    // Rhino's raw prompt-font integer is the baseline. Do not infer point
    // units or impose an absolute ceiling that could shrink an enlargement.
    internal static int PromptFontSize(int original, double scale)
    {
        if (double.IsNaN(scale) || scale < 0.5 || scale > 2) throw new ArgumentOutOfRangeException(nameof(scale));
        return original <= 0 ? original : (int)Math.Max(1, Math.Min(int.MaxValue, Math.Round(original * scale)));
    }
    internal static Size ScaleSize(Size value, double scale) => new Size(Dimension(value.Width, scale, 8, 256), Dimension(value.Height, scale, 8, 256));
}
