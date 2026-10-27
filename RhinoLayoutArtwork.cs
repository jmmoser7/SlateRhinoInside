using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace SlateRhinoInside;
internal sealed class LayoutAnnotationSpec
{
    internal string Kind = "", Text = "";
    internal double X, Y, X2, Y2, Width, Height;
    internal static IEnumerable<LayoutAnnotationSpec> Parse(JToken? token, double pageWidth, double pageHeight)
    {
        if (token == null) return Array.Empty<LayoutAnnotationSpec>();
        if (token.Type != JTokenType.String || ((string)token!).Length > 4096)
            throw new ProtocolFailure("invalid_request", "Annotations must be a JSON string of at most 4096 characters");
        var rows = JArray.Parse((string)token!, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        if (rows.Count > 32) throw new ProtocolFailure("invalid_request", "Use at most 32 page annotations");
        var result = new List<LayoutAnnotationSpec>();
        foreach (var row in rows)
        {
            if (row is not JObject item || item["kind"]?.Type != JTokenType.String)
                throw new ProtocolFailure("invalid_request", "Annotation kind is required");
            string kind = (string)item["kind"]!;
            string[] fields = kind switch { "rect" => new[] { "kind", "x", "y", "width", "height" },
                "line" => new[] { "kind", "x", "y", "x2", "y2" }, "text" => new[] { "kind", "x", "y", "text", "height" },
                _ => throw new ProtocolFailure("invalid_request", "Annotation kind must be rect,line,text") };
            if (item.Count != fields.Length || fields.Any(key => item[key] == null) || item.Properties().Any(p => !fields.Contains(p.Name)))
                throw new ProtocolFailure("invalid_request", "Annotation fields do not match its closed kind");
            double Number(string name)
            {
                if (item[name]!.Type != JTokenType.Float && item[name]!.Type != JTokenType.Integer)
                    throw new ProtocolFailure("invalid_request", "Annotation coordinates must be numeric");
                double value = (double)item[name]!;
                if (double.IsNaN(value) || double.IsInfinity(value)) throw new ProtocolFailure("invalid_request", "Annotation coordinates must be finite");
                return value;
            }
            var mark = new LayoutAnnotationSpec { Kind = kind, X = Number("x"), Y = Number("y") };
            if (mark.X < 0 || mark.Y < 0 || mark.X > pageWidth || mark.Y > pageHeight)
                throw new ProtocolFailure("invalid_request", "Annotation origin must fit on paper");
            if (kind == "line")
            {
                mark.X2 = Number("x2"); mark.Y2 = Number("y2");
                if (mark.X2 < 0 || mark.Y2 < 0 || mark.X2 > pageWidth || mark.Y2 > pageHeight
                    || Math.Abs(mark.X2 - mark.X) + Math.Abs(mark.Y2 - mark.Y) < 0.01)
                    throw new ProtocolFailure("invalid_request", "Line endpoints must be distinct and fit on paper");
            }
            else
            {
                mark.Height = Number("height");
                if (kind == "rect")
                {
                    mark.Width = Number("width");
                    if (mark.Width < 0.1 || mark.Height < 0.1 || mark.X + mark.Width > pageWidth || mark.Y + mark.Height > pageHeight)
                        throw new ProtocolFailure("invalid_request", "Rectangle must be positive and fit on paper");
                }
                else
                {
                    if (item["text"]!.Type != JTokenType.String) throw new ProtocolFailure("invalid_request", "Annotation text must be plain text");
                    mark.Text = (string)item["text"]!;
                    if (string.IsNullOrWhiteSpace(mark.Text) || mark.Text.Length > 256 || mark.Text.Any(char.IsControl)
                        || mark.Text.Contains("%<") || mark.Text.Contains(">%")
                        || mark.Height < 1 || mark.Height > 20 || mark.Y + mark.Height > pageHeight)
                        throw new ProtocolFailure("invalid_request", "Text must be bounded plain text with height 1..20 mm");
                }
            }
            result.Add(mark);
        }
        return result;
    }
}
internal sealed class LayoutArtwork : IDisposable
{
    private readonly List<(LayoutAnnotationSpec Spec, GeometryBase Geometry)> items = new List<(LayoutAnnotationSpec, GeometryBase)>();
    internal static LayoutArtwork Prepare(RhinoDoc doc, LayoutTemplateSpec page, double units)
    {
        var artwork = new LayoutArtwork();
        try
        {
            Point3d Point(double x, double y) => new Point3d(x * units, (page.Height - y) * units, 0);
            foreach (var mark in page.Annotations)
            {
                GeometryBase geometry;
                if (mark.Kind == "rect") geometry = new PolylineCurve(new[] { Point(mark.X, mark.Y), Point(mark.X + mark.Width, mark.Y),
                    Point(mark.X + mark.Width, mark.Y + mark.Height), Point(mark.X, mark.Y + mark.Height), Point(mark.X, mark.Y) });
                else if (mark.Kind == "line") geometry = new LineCurve(Point(mark.X, mark.Y), Point(mark.X2, mark.Y2));
                else
                {
                    var text = TextEntity.Create(mark.Text, new Plane(Point(mark.X, mark.Y), Vector3d.ZAxis), doc.DimStyles.Current, false, 0, 0)
                        ?? throw new ProtocolFailure("invalid_request", "Native text geometry could not be prepared");
                    geometry = text;
                    artwork.items.Add((mark, geometry));
                    text.PlainText = mark.Text; text.Justification = TextJustification.TopLeft;
                    text.DimensionScale = 1; text.TextHeight = mark.Height * units;
                    var bounds = text.GetBoundingBox(true);
                    if (!bounds.IsValid || bounds.Min.X < -0.001 || bounds.Min.Y < -0.001
                        || bounds.Max.X > page.Width * units + 0.001 || bounds.Max.Y > page.Height * units + 0.001)
                        throw new ProtocolFailure("invalid_request", "Text bounds must fit within the page");
                    continue;
                }
                artwork.items.Add((mark, geometry));
            }
            return artwork;
        }
        catch { artwork.Dispose(); throw; }
    }
    internal void AddTo(RhinoDoc doc, Guid page, JArray result)
    {
        foreach (var item in items)
        {
            using var attributes = new ObjectAttributes { Space = ActiveSpace.PageSpace, ViewportId = page,
                LayerIndex = doc.Layers.CurrentLayerIndex, ColorSource = ObjectColorSource.ColorFromObject, ObjectColor = Color.Black,
                Name = "Slate layout " + item.Spec.Kind };
            attributes.SetUserString("slate.layout.annotation", "1");
            Guid id = item.Geometry is TextEntity text ? doc.Objects.AddText(text, attributes) : doc.Objects.AddCurve((Curve)item.Geometry, attributes);
            if (id == Guid.Empty) throw new ProtocolFailure("invalid_result", "Rhino refused page artwork");
            result.Add(new JObject { ["kind"] = item.Spec.Kind, ["object_id"] = id.ToString() });
            var actual = doc.Objects.FindId(id);
            if (actual == null || actual.Attributes.Space != ActiveSpace.PageSpace || actual.Attributes.ViewportId != page)
                throw new ProtocolFailure("invalid_result", "Page artwork scope could not be verified");
        }
    }
    public void Dispose() { foreach (var item in items) item.Geometry.Dispose(); items.Clear(); }
}
