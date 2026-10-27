using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;
using Rhino.Runtime;

namespace SlateRhinoInside;
internal sealed partial class RhinoAdapter
{
    private readonly NativeCaptureReadiness captureReadiness = new NativeCaptureReadiness();
    private void RequireSchema(JObject schema)
    {
        if (!JToken.DeepEquals(schema, Contract.Schema())) throw new ProtocolFailure("unsupported_schema", "Unsupported Rhino view schema");
    }
    public JObject Snapshot(JObject schema)
    {
        RequireSchema(schema);
        var doc = Document();
        var viewport = doc.Views.ActiveView?.ActiveViewport ?? throw new ProtocolFailure("invalid_result", "No active Rhino viewport");
        using var info = new ViewportInfo(viewport);
        return new JObject { ["source"] = Scope(), ["schema"] = Contract.ViewNamespace, ["schema_version"] = 1, ["fidelity"] = "exact_native", ["state"] = new JObject {
            ["viewport"] = info.ToJSON(new SerializationOptions()),
            ["target"] = new JArray(viewport.CameraTarget.X, viewport.CameraTarget.Y, viewport.CameraTarget.Z),
            ["display_mode"] = viewport.DisplayMode.Id.ToString(), ["units"] = (int)doc.ModelUnitSystem,
            ["document_id"] = "rhino-" + doc.RuntimeSerialNumber, ["dirty"] = doc.Modified,
            ["linked_source_continuity"] = RhinoPaths.IsBoundLinkedPath(mode, doc.Path, originalPath),
            ["captured_scope"] = "camera_projection_and_display_mode" } };
    }
    public void RestoreView(JObject snapshot)
    {
        var doc = Document();
        if ((string?)snapshot["schema"] != Contract.ViewNamespace || (int?)snapshot["schema_version"] != 1 || !JToken.DeepEquals(snapshot["source"], Scope())) throw new ProtocolFailure("unsupported_schema", "View identity or source revision differs");
        var state = (JObject)snapshot["state"]!;
        if ((int?)state["units"] != (int)doc.ModelUnitSystem) throw new ProtocolFailure("unsupported_schema", "Rhino view units differ");
        string encoded = (string?)state["viewport"] ?? throw new ProtocolFailure("invalid_request", "Missing viewport state");
        if (encoded.Length > 180000) throw new ProtocolFailure("invalid_request", "Viewport state exceeds budget");
        using var value = CommonObject.FromJSON(encoded);
        if (value is not ViewportInfo info || !info.IsValidCamera || !info.IsValidFrustum) throw new ProtocolFailure("invalid_request", "Invalid Rhino projection");
        var viewport = doc.Views.ActiveView.ActiveViewport;
        viewport.PushViewProjection();
        if (!viewport.SetViewProjection(info, false)) throw new ProtocolFailure("invalid_result", "Rhino could not restore camera projection");
        if (state["target"] is JArray target && target.Count == 3) viewport.SetCameraTarget(new Point3d((double)target[0], (double)target[1], (double)target[2]), false);
        if (Guid.TryParse((string?)state["display_mode"], out Guid display))
        {
            var description = DisplayModeDescription.GetDisplayMode(display);
            if (description != null) viewport.DisplayMode = description;
        }
        doc.Views.Redraw();
    }
    private void SetCamera(JObject parameters)
    {
        var viewport = Document().Views.ActiveView.ActiveViewport;
        var eye = new Point3d((double)parameters["x"]!, (double)parameters["y"]!, (double)parameters["z"]!);
        var target = new Point3d((double)parameters["target_x"]!, (double)parameters["target_y"]!, (double)parameters["target_z"]!);
        if (eye.DistanceTo(target) < 0.001) throw new ProtocolFailure("invalid_request", "Camera eye and target must differ");
        viewport.PushViewProjection(); viewport.CameraUp = Vector3d.ZAxis;
        viewport.SetCameraLocations(target, eye);
        bool parallel = (string?)parameters["projection"] == "parallel";
        if (parallel) viewport.ChangeToParallelProjection(true);
        else viewport.ChangeToPerspectiveProjection(true, (double?)parameters["lens"] ?? 45);
        if (parallel && parameters["parallel_scale"] != null)
        {
            using var info = new ViewportInfo(viewport);
            double half = (double)parameters["parallel_scale"]! / 2;
            double aspect = viewport.FrustumAspect;
            if (!info.SetFrustum(-half * aspect, half * aspect, -half, half, info.FrustumNear, info.FrustumFar) || !viewport.SetViewProjection(info,false)) throw new ProtocolFailure("invalid_result", "Invalid parallel camera framing");
        }
        var mode = DisplayModeDescription.GetDisplayMode(DisplayModeDescription.ShadedId);
        if (mode != null) viewport.DisplayMode = mode;
        Document().Views.Redraw();
    }
    public (JObject Metadata, byte[] Bytes) Capture(JObject schema)
    {
        RequireSchema(schema);
        var doc = Document();
        captureReadiness.Require(Native.IsWindowVisible(window));
        var view = doc.Views.ActiveView ?? throw new ProtocolFailure("invalid_result", "No Rhino view to capture");
        uint counter = view.ActiveViewport.ChangeCounter;
        var snapshot = Snapshot(schema);
        Size size = view.ActiveViewport.Size;
        int width = Math.Min(1920, Math.Max(64, size.Width));
        int height = Math.Max(64, (int)Math.Round(width * Math.Max(1, size.Height) / (double)Math.Max(1, size.Width)));
        height = Math.Min(1920, height);
        var settings = new ViewCapture { Width = width, Height = height, DrawGrid = false, DrawAxes = false, DrawGridAxes = false, TransparentBackground = false, ScaleScreenItems = true };
        using var bitmap = settings.CaptureToBitmap(view) ?? throw new ProtocolFailure("invalid_result", "Rhino capture failed");
        using var memory = new MemoryStream(); bitmap.Save(memory, ImageFormat.Png);
        var metadata = new JObject { ["source"] = Scope(), ["view"] = snapshot, ["captured_at"] = DateTime.UtcNow.ToString("o"), ["width_px"] = bitmap.Width, ["height_px"] = bitmap.Height,
            ["consistency"] = counter == view.ActiveViewport.ChangeCounter ? "atomic" : "best_effort" };
        return (metadata, memory.ToArray());
    }
}
