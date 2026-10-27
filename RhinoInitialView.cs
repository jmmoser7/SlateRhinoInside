using System;
using System.Drawing;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.FileIO;

namespace SlateRhinoInside;
internal static class InitialViewPolicy
{
    internal static bool ShouldInitialize(bool? authored, bool hasModelView, bool cameraValid, bool frustumValid) =>
        authored == false || !hasModelView || !cameraValid || !frustumValid;
}
internal sealed partial class RhinoAdapter
{
    private JObject initialView = new JObject { ["initialized"] = false, ["reason"] = "not_bound" };
    private static bool? HasAuthoredViews(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            if (new FileInfo(path).Length > 268435456) return null;
            using var file = File3dm.Read(path, File3dm.TableTypeFilter.Settings, File3dm.ObjectTypeFilter.None);
            return file == null ? (bool?)null : file.AllViews.Count > 0;
        }
        catch { return null; }
    }
    private static JObject InitializeBoundView(RhinoDoc doc, string path)
    {
        bool? authored = HasAuthoredViews(path);
        var models = doc.Views.GetViewList(ViewTypeFilter.Model);
        var active = doc.Views.ActiveView;
        var view = active != null && models.Any(model => model.MainViewport.Id == active.MainViewport.Id) ? active : models.FirstOrDefault();
        bool camera = false, frustum = false;
        if (view != null && view.Document.RuntimeSerialNumber == doc.RuntimeSerialNumber)
        {
            using var snapshot = new ViewportInfo(view.ActiveViewport);
            camera = snapshot.IsValidCamera; frustum = snapshot.IsValidFrustum;
        }
        bool repair = InitialViewPolicy.ShouldInitialize(authored, view != null, camera, frustum);
        if (!repair) return new JObject { ["initialized"] = false, ["reason"] = authored == true ? "authored_valid_view_preserved" : "unknown_authored_status_valid_view_preserved",
            ["authored_views"] = authored, ["camera_valid"] = camera, ["frustum_valid"] = frustum };
        bool dirty = doc.Modified;
        string reason = authored == false ? "source_has_no_authored_views" : view == null ? "missing_model_view" : "invalid_camera_or_frustum";
        view ??= doc.Views.Add("Perspective", DefinedViewportProjection.Perspective, new Rectangle(0, 0, 960, 640), false)
            ?? throw new ProtocolFailure("invalid_result", "Rhino could not create a default modeling view");
        if (view.Document.RuntimeSerialNumber != doc.RuntimeSerialNumber)
            throw new ProtocolFailure("source_mismatch", "Initial modeling view belongs to another document");
        doc.Views.ActiveView = view;
        var viewport = view.ActiveViewport;
        if (!viewport.SetProjection(DefinedViewportProjection.Perspective, "Perspective", true))
            throw new ProtocolFailure("invalid_result", "Rhino could not initialize the default perspective projection");
        var display = DisplayModeDescription.GetDisplayMode(DisplayModeDescription.ShadedId);
        if (display != null) viewport.DisplayMode = display;
        bool framed = viewport.ZoomExtents();
        using var verified = new ViewportInfo(viewport);
        if (!verified.IsValidCamera || !verified.IsValidFrustum)
            throw new ProtocolFailure("invalid_result", "Default native camera/frustum could not be verified");
        doc.Views.Redraw(); doc.Modified = dirty;
        return new JObject { ["initialized"] = true, ["reason"] = reason, ["authored_views"] = authored,
            ["viewport_id"] = viewport.Id.ToString(), ["view"] = viewport.Name, ["camera_valid"] = true, ["frustum_valid"] = true,
            ["framed"] = framed, ["model_view_count"] = doc.Views.GetViewList(ViewTypeFilter.Model).Length };
    }
}
