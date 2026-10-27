using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Display;
using Rhino.Geometry;

namespace SlateRhinoInside;

// Coordinates use a top-left paper origin, in millimeters. No drawing code is accepted.
internal sealed class LayoutTemplateSpec
{
    internal string Name = "";
    internal double Width, Height;
    internal readonly List<LayoutDetailSpec> Details = new List<LayoutDetailSpec>();
    internal readonly List<LayoutAnnotationSpec> Annotations = new List<LayoutAnnotationSpec>();
    internal static LayoutTemplateSpec Parse(JObject parameters)
    {
        var spec = new LayoutTemplateSpec { Name = (string)parameters["name"]!,
            Width = (double)parameters["page_width_mm"]!, Height = (double)parameters["page_height_mm"]! };
        if (string.IsNullOrWhiteSpace(spec.Name) || spec.Name.Length > 128 || spec.Name.Any(char.IsControl))
            throw new ProtocolFailure("invalid_request", "Layout name must be bounded text");
        if (!Finite(spec.Width) || !Finite(spec.Height) || spec.Width < 50 || spec.Width > 2000 || spec.Height < 50 || spec.Height > 2000)
            throw new ProtocolFailure("invalid_request", "Page dimensions must be 50..2000 millimeters");
        string json = (string)parameters["details"]!;
        if (json.Length > 4096) throw new ProtocolFailure("invalid_request", "Detail definitions exceed 4096 characters");
        var rows = JArray.Parse(json, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        if (rows.Count < 1 || rows.Count > 8) throw new ProtocolFailure("invalid_request", "Use 1..8 detail viewports");
        var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (row is not JObject item || item.Count != 6 || item.Properties().Any(p => !new[] { "title", "x", "y", "width", "height", "projection" }.Contains(p.Name)))
                throw new ProtocolFailure("invalid_request", "Each detail requires title,x,y,width,height,projection only");
            if (item["title"]?.Type != JTokenType.String || item["projection"]?.Type != JTokenType.String
                || new[] { "x", "y", "width", "height" }.Any(key => item[key]?.Type != JTokenType.Float && item[key]?.Type != JTokenType.Integer))
                throw new ProtocolFailure("invalid_request", "Detail title/projection are text; rectangle values are numeric");
            var detail = new LayoutDetailSpec { Title = (string)item["title"]!, X = (double)item["x"]!, Y = (double)item["y"]!,
                Width = (double)item["width"]!, Height = (double)item["height"]!, Projection = (string)item["projection"]! };
            if (string.IsNullOrWhiteSpace(detail.Title) || detail.Title.Length > 128 || detail.Title.Any(char.IsControl) || !titles.Add(detail.Title))
                throw new ProtocolFailure("invalid_request", "Detail titles must be unique bounded text");
            if (!new[] { "top", "front", "east", "perspective" }.Contains(detail.Projection))
                throw new ProtocolFailure("invalid_request", "Projection must be top,front,east,perspective");
            if (new[] { detail.X, detail.Y, detail.Width, detail.Height }.Any(v => !Finite(v))
                || detail.X < 0 || detail.Y < 0 || detail.Width < 5 || detail.Height < 5
                || detail.X + detail.Width > spec.Width || detail.Y + detail.Height > spec.Height)
                throw new ProtocolFailure("invalid_request", "Detail rectangle must fit within the paper");
            spec.Details.Add(detail);
        }
        spec.Annotations.AddRange(LayoutAnnotationSpec.Parse(parameters["annotations"], spec.Width, spec.Height));
        return spec;
    }
    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
internal sealed class LayoutDetailSpec
{
    internal string Title = "", Projection = "";
    internal double X, Y, Width, Height;
}
internal sealed partial class RhinoAdapter
{
    private static JObject CreateLayoutTemplate(RhinoDoc doc, LayoutTemplateSpec spec)
    {
        var pages = doc.Views.GetPageViews();
        if (pages.Length >= 64 || pages.Any(p => string.Equals(p.PageName, spec.Name, StringComparison.OrdinalIgnoreCase)))
            throw new ProtocolFailure("invalid_request", "Use a new page name within the 64-page budget");
        if (LiveObjectCount(doc) + spec.Details.Count + spec.Annotations.Count > 20000)
            throw new ProtocolFailure("invalid_request", "Draft geometry budget reached");
        // Preserve document page units and existing page geometry. Only this new page uses the conversion.
        double units = RhinoMath.UnitScale(UnitSystem.Millimeters, doc.PageUnitSystem);
        if (doc.PageUnitSystem == UnitSystem.None || double.IsNaN(units) || double.IsInfinity(units) || units <= 0)
            throw new ProtocolFailure("invalid_request", "Document must have a defined physical page unit system");
        using var artwork = LayoutArtwork.Prepare(doc, spec, units);
        JObject? progress = null;
        var result = WorkflowMutation(doc, "layout " + spec.Name, () => {
            var page = doc.Views.AddPageView(spec.Name, spec.Width * units, spec.Height * units)
                ?? throw new ProtocolFailure("invalid_result", "Rhino refused the page layout");
            var details = new JArray();
            var annotations = new JArray();
            progress = new JObject { ["page_id"] = page.MainViewport.Id.ToString(), ["page_name"] = page.PageName,
                ["details"] = details, ["annotations"] = annotations };
            foreach (var definition in spec.Details)
            {
                var projection = definition.Projection switch { "top" => DefinedViewportProjection.Top,
                    "front" => DefinedViewportProjection.Front, "east" => DefinedViewportProjection.Right,
                    _ => DefinedViewportProjection.Perspective };
                var lowerLeft = new Point2d(definition.X * units, (spec.Height - definition.Y - definition.Height) * units);
                var upperRight = new Point2d((definition.X + definition.Width) * units, (spec.Height - definition.Y) * units);
                var detail = page.AddDetailView(definition.Title, lowerLeft, upperRight, projection)
                    ?? throw new ProtocolFailure("invalid_result", "Rhino refused a detail viewport");
                details.Add(new JObject { ["object_id"] = detail.Id.ToString(), ["viewport_id"] = detail.Viewport.Id.ToString(),
                    ["title"] = definition.Title, ["projection"] = definition.Projection });
                detail.Viewport.ZoomExtents();
                if (!detail.CommitViewportChanges()) throw new ProtocolFailure("invalid_result", "Rhino refused detail framing");
            }
            artwork.AddTo(doc, page.MainViewport.Id, annotations);
            page.SetPageAsActive(); doc.Views.ActiveView = page; page.MainViewport.ZoomExtents();
            return new JObject { ["complete"] = true, ["page_id"] = page.MainViewport.Id.ToString(), ["page_name"] = page.PageName,
                ["page_width_mm"] = spec.Width, ["page_height_mm"] = spec.Height, ["detail_count"] = details.Count, ["details"] = details,
                ["annotation_count"] = annotations.Count, ["annotations"] = annotations };
        });
        if ((bool?)result["complete"] != true && progress != null) result["created"] = progress;
        return result;
    }
}
