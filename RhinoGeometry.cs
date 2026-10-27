using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace SlateRhinoInside;
internal sealed partial class RhinoAdapter
{
    private readonly GeometryResourceCache geometryResources = new GeometryResourceCache();
    private static IEnumerable<RhinoObject> LiveObjects(RhinoDoc doc) =>
        doc.Objects.GetObjectList(new ObjectEnumeratorSettings {
            NormalObjects = true, HiddenObjects = true, LockedObjects = true,
            ActiveObjects = true, ReferenceObjects = true, IdefObjects = false,
            DeletedObjects = false, IncludeLights = true, IncludePhantoms = true,
            IncludeGrips = false, ObjectTypeFilter = ObjectType.AnyObject
        }).Where(item => !item.IsDeleted);
    private static int LiveObjectCount(RhinoDoc doc) => LiveObjects(doc).Count();
    private static JObject ClearDraft(RhinoDoc doc)
    {
        var objects = LiveObjects(doc).ToArray();
        // IsDeletable is structural protection, not hidden/locked mode. Check
        // the entire target set before opening undo or changing any object.
        if (objects.Any(item => !DraftClearPolicy.CanDelete(item.IsReference, item.IsDeletable)))
            throw new ProtocolFailure("invalid_request", "Draft contains reference or protected geometry; no objects were deleted");
        var before = objects.Select(item => item.Id).ToArray();
        uint undo = doc.BeginUndoRecord("Slate isolated draft: org.slate.rhino.draft_clear");
        int confirmed = 0;
        bool interrupted = false;
        try
        {
            foreach (var item in objects)
                if (doc.Objects.Delete(item, true, true)) confirmed++;
        }
        catch { interrupted = true; }
        finally
        {
            // A cleanup failure after deletion must not become a no-effect
            // protocol rejection. The normal mutation reply advances revision.
            try { if (undo != 0) doc.EndUndoRecord(undo); } catch { interrupted = true; }
            try { doc.Views.Redraw(); } catch { interrupted = true; }
        }
        try
        {
            return DraftClearPolicy.Result(before, LiveObjects(doc).Select(item => item.Id).ToArray(), interrupted);
        }
        catch
        {
            // SDK failure can make actual final counts unavailable. Preserve
            // that uncertainty and the confirmed effects instead of guessing.
            return new JObject { ["removed"] = null, ["remaining"] = null, ["cleared"] = false,
                ["counts_verified"] = false, ["removed_confirmed"] = confirmed,
                ["warning"] = "Native clear result could not be verified; inspect the live document before continuing" };
        }
    }
    private int LayerIndex(RhinoDoc doc, string name, Color color)
    {
        geometryResources.Bind(doc.RuntimeSerialNumber);
        if (geometryResources.Layers.TryGetValue(name, out var cached)
            && cached.Index >= 0 && cached.Index < doc.Layers.Count)
        {
            var layer = doc.Layers[cached.Index];
            if (!layer.IsDeleted && !layer.IsReference && layer.Id == cached.Id
                && string.Equals(layer.FullPath, name, StringComparison.OrdinalIgnoreCase)) return cached.Index;
        }
        // A bound file already has its layer table. Reuse only the full-path
        // match, never a same-named child layer or a deleted undo-table entry.
        int index = doc.Layers.FindByFullPath(name, RhinoMath.UnsetIntIndex);
        if (index >= 0 && index < doc.Layers.Count)
        {
            var existing = doc.Layers[index];
            if (existing.IsReference) throw new ProtocolFailure("invalid_request", "Draft geometry requires a local layer");
            if (existing.IsDeleted || !string.Equals(existing.FullPath, name, StringComparison.OrdinalIgnoreCase))
                throw new ProtocolFailure("invalid_result", "Rhino returned an invalid layer match");
        }
        else
        {
            int split = name.LastIndexOf("::", StringComparison.Ordinal);
            Guid parent = split < 0 ? Guid.Empty : doc.Layers[LayerIndex(doc, name.Substring(0, split), Color.Gray)].Id;
            using var value = new Layer { Name = split < 0 ? name : name.Substring(split + 2), ParentLayerId = parent, Color = color };
            index = doc.Layers.Add(value);
            if (index < 0) throw new ProtocolFailure("invalid_result", "Rhino refused the layer name");
        }
        var resolved = doc.Layers[index];
        geometryResources.Layers[name] = new GeometryResource(index, resolved.Id);
        return index;
    }
    private int MaterialIndex(RhinoDoc doc, Color color, double transparency)
    {
        geometryResources.Bind(doc.RuntimeSerialNumber);
        string key = color.ToArgb() + ":" + transparency.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        bool Matches(Material value) => !value.IsDeleted && !value.IsReference
            && value.DiffuseColor.ToArgb() == color.ToArgb() && value.Transparency == transparency;
        if (geometryResources.Materials.TryGetValue(key, out var cached)
            && cached.Index >= 0 && cached.Index < doc.Materials.Count)
        {
            var existing = doc.Materials[cached.Index];
            if (existing.Id == cached.Id && Matches(existing)) return cached.Index;
        }
        int index = -1;
        for (int candidate = 0; candidate < doc.Materials.Count; candidate++)
        {
            var existing = doc.Materials[candidate];
            if (existing.Name == "Slate " + key && Matches(existing)) { index = candidate; break; }
        }
        if (index < 0)
        {
            using var value = new Material { Name = "Slate " + key, DiffuseColor = color, Transparency = transparency };
            index = doc.Materials.Add(value);
            if (index < 0) throw new ProtocolFailure("invalid_result", "Rhino refused the draft material");
        }
        geometryResources.Materials[key] = new GeometryResource(index, doc.Materials[index].Id);
        return index;
    }
    private ObjectAttributes Attributes(RhinoDoc doc, JObject parameters, bool material)
    {
        string name = (string)parameters["layer"]!;
        var color = Color.FromArgb((int)parameters["red"]!, (int)parameters["green"]!, (int)parameters["blue"]!);
        int layer = LayerIndex(doc, name, color);
        var attrs = new ObjectAttributes { LayerIndex = layer, ObjectColor = color, ColorSource = ObjectColorSource.ColorFromObject };
        if (material)
        {
            double transparency = (double?)parameters["transparency"] ?? 0;
            attrs.MaterialIndex = MaterialIndex(doc, color, transparency);
            attrs.MaterialSource = ObjectMaterialSource.MaterialFromObject;
        }
        return attrs;
    }
    private static Point3d[] ParsePoints(string text)
    {
        var json = JArray.Parse(text);
        if (json.Count < 2 || json.Count > 256) throw new ProtocolFailure("invalid_request", "Geometry requires2..256 points");
        return json.Select(item =>
        {
            if (item is not JArray point || point.Count != 3 || point.Any(v => v.Type != JTokenType.Integer && v.Type != JTokenType.Float)) throw new ProtocolFailure("invalid_request", "Points must be numeric [x,y,z]");
            var value = new Point3d((double)point[0], (double)point[1], (double)point[2]);
            if (!value.IsValid || Math.Max(Math.Abs(value.X), Math.Max(Math.Abs(value.Y), Math.Abs(value.Z))) > 1000000) throw new ProtocolFailure("invalid_request", "Point outside model bounds");
            return value;
        }).ToArray();
    }
    private JObject AddGeometry(RhinoDoc doc, string command, JObject parameters)
    {
        Guid id;
        if (command == "org.slate.rhino.draft_add_box")
        {
            double x = (double)parameters["x"]!, y = (double)parameters["y"]!, z = (double)parameters["z"]!;
            var box = new BoundingBox(new Point3d(x,y,z), new Point3d(x + (double)parameters["width"]!,y + (double)parameters["depth"]!, z + (double)parameters["height"]!));
            using var brep = new Box(box).ToBrep();
            id = doc.Objects.AddBrep(brep, Attributes(doc, parameters, true));
        }
        else if (command == "org.slate.rhino.draft_add_curve")
        {
            var points = ParsePoints((string)parameters["points"]!);
            if ((bool)parameters["closed"]! && points[0].DistanceTo(points[points.Length - 1]) > 0.000001) points = points.Concat(new[] { points[0] }).ToArray();
            id = doc.Objects.AddPolyline(points, Attributes(doc, parameters, false));
        }
        else if (command == "org.slate.rhino.draft_add_mesh")
        {
            var points = ParsePoints((string)parameters["vertices"]!);
            var faces = JArray.Parse((string)parameters["faces"]!);
            if (points.Length < 3 || faces.Count == 0 || faces.Count > 512) throw new ProtocolFailure("invalid_request", "Invalid mesh budget");
            using var mesh = new Mesh();
            foreach (var point in points) mesh.Vertices.Add(point);
            foreach (JToken item in faces)
            {
                if (item is not JArray face || (face.Count != 3 && face.Count != 4) || face.Any(v => v.Type != JTokenType.Integer || (int)v < 0 || (int)v >= points.Length)) throw new ProtocolFailure("invalid_request", "Mesh face indices are invalid");
                if (face.Count == 3) mesh.Faces.AddFace((int)face[0],(int)face[1],(int)face[2]);
                else mesh.Faces.AddFace((int)face[0],(int)face[1],(int)face[2],(int)face[3]);
            }
            mesh.Normals.ComputeNormals(); mesh.Compact();
            if (!mesh.IsValid) throw new ProtocolFailure("invalid_request", "Rhino rejected malformed mesh");
            id = doc.Objects.AddMesh(mesh, Attributes(doc, parameters, true));
        }
        else throw new ProtocolFailure("unsupported_operation", "Unknown isolated draft command");
        if (id == Guid.Empty) throw new ProtocolFailure("invalid_result", "Rhino refused geometry insertion");
        return new JObject { ["object_id"] = id.ToString(), ["objects"] = LiveObjectCount(doc) };
    }
}

// Cache entries identify a resource, not just a table slot. Rebinding a real
// document invalidates both tables together; this policy needs no Rhino runtime.
internal readonly struct GeometryResource
{
    internal readonly int Index;
    internal readonly Guid Id;
    internal GeometryResource(int index, Guid id) { Index = index; Id = id; }
}
internal sealed class GeometryResourceCache
{
    private uint documentSerial;
    internal readonly Dictionary<string, GeometryResource> Layers = new Dictionary<string, GeometryResource>(StringComparer.OrdinalIgnoreCase);
    internal readonly Dictionary<string, GeometryResource> Materials = new Dictionary<string, GeometryResource>(StringComparer.Ordinal);
    internal void Bind(uint serial)
    {
        if (serial == 0) throw new ProtocolFailure("source_not_bound", "Rhino document has no runtime identity");
        if (documentSerial == serial) return;
        Layers.Clear(); Materials.Clear(); documentSerial = serial;
    }
}
internal static class DraftClearPolicy
{
    internal static bool CanDelete(bool reference, bool deletable) => !reference && deletable;
    internal static JObject Result(Guid[] before, Guid[] after, bool interrupted)
    {
        var remainingIds = new HashSet<Guid>(after);
        var result = new JObject { ["removed"] = before.Count(id => !remainingIds.Contains(id)),
            ["remaining"] = after.Length, ["cleared"] = after.Length == 0 && !interrupted, ["counts_verified"] = true };
        if (interrupted) result["warning"] = "Native clear was interrupted; counts describe the resulting live document";
        return result;
    }
}
