using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace SlateRhinoInside;
internal sealed partial class RhinoAdapter
{
    private const string BuildingIdKey = "slate.building.id", BuildingSchemaKey = "slate.building.schema", BuildingDataKey = "slate.building.definition";
    private static RhinoObject[] BuildingObjects(RhinoDoc doc, string id) => LiveObjects(doc)
        .Where(o => o.Attributes.GetUserString(BuildingIdKey) == id).ToArray();
    private JObject SetBuilding(RhinoDoc doc, BuildingSpec spec)
    {
        ValidateLayerTarget(doc, spec.Layer);
        var found = BuildingObjects(doc, spec.Id);
        if (found.Length > 1) throw new ProtocolFailure("invalid_request", "Building ID is ambiguous");
        var existing = found.SingleOrDefault();
        if (existing != null && (existing.IsReference || !existing.IsDeletable || existing.Attributes.GetUserString(BuildingSchemaKey) != "1"))
            throw new ProtocolFailure("authority_denied", "Only editable typed building masses can be replaced");
        if (existing == null && LiveObjectCount(doc) >= 20000) throw new ProtocolFailure("invalid_request", "Draft geometry budget reached");
        var points = spec.Footprint.Select(p => new Point3d(p[0], p[1], spec.Elevation)).ToList();
        if (points[0].DistanceTo(points[points.Count - 1]) > 0.000001) points.Add(points[0]);
        using var outline = new PolylineCurve(points);
        using var intersections = Intersection.CurveSelf(outline, doc.ModelAbsoluteTolerance);
        if (intersections != null && intersections.Count > 0) throw new ProtocolFailure("invalid_request", "Building footprint intersects itself");
        using var extrusion = Extrusion.Create(outline, spec.Height, true)
            ?? throw new ProtocolFailure("invalid_request", "Building footprint cannot form a solid extrusion");
        using var brep = extrusion.ToBrep();
        if (brep == null || !brep.IsValid || !brep.IsSolid) throw new ProtocolFailure("invalid_request", "Building must be a valid closed solid");
        return WorkflowMutation(doc, "building " + spec.Id, () => {
            using var attributes = Attributes(doc, spec.Parameters(), true);
            attributes.Name = spec.Id;
            attributes.SetUserString(BuildingIdKey, spec.Id);
            attributes.SetUserString(BuildingSchemaKey, "1");
            attributes.SetUserString(BuildingDataKey, spec.Parameters().ToString(Newtonsoft.Json.Formatting.None));
            Guid objectId;
            if (existing == null) objectId = doc.Objects.AddBrep(brep, attributes);
            else
            {
                objectId = existing.Id;
                if (!doc.Objects.Replace(objectId, brep) || !doc.Objects.ModifyAttributes(objectId, attributes, true))
                    throw new ProtocolFailure("invalid_result", "Rhino could not complete building replacement");
            }
            if (objectId == Guid.Empty) throw new ProtocolFailure("invalid_result", "Rhino refused the building mass");
            return new JObject { ["complete"] = true, ["updated"] = existing != null, ["building_id"] = spec.Id,
                ["object_id"] = objectId.ToString(), ["floors"] = spec.Floors, ["height"] = spec.Height,
                ["floor_height"] = spec.FloorHeight, ["objects"] = LiveObjectCount(doc) };
        });
    }
    private JObject UpdateBuildingFloors(RhinoDoc doc, JObject p)
    {
        string id = (string)p["building_id"]!;
        var found = BuildingObjects(doc, id);
        if (found.Length != 1 || found[0].Attributes.GetUserString(BuildingSchemaKey) != "1")
            throw new ProtocolFailure("invalid_request", "Exactly one typed building ID must exist");
        string text = found[0].Attributes.GetUserString(BuildingDataKey) ?? "";
        if (text.Length > 8192) throw new ProtocolFailure("invalid_request", "Stored building definition exceeds bounds");
        var parameters = JObject.Parse(text); parameters["floors"] = p["floors"]!.DeepClone();
        var spec = BuildingSpec.Parse(parameters);
        if (spec.Id != id) throw new ProtocolFailure("invalid_request", "Stored building identity differs");
        return SetBuilding(doc, spec);
    }
    private static JObject InspectBuildings(RhinoDoc doc, JObject p)
    {
        var objects = LiveObjects(doc).Where(o => o.Attributes.GetUserString(BuildingSchemaKey) == "1").ToArray();
        int offset = (int?)p["offset"] ?? 0, limit = (int?)p["limit"] ?? 64;
        var rows = new JArray();
        foreach (var item in objects.Skip(offset).Take(limit))
        {
            string raw = item.Attributes.GetUserString(BuildingDataKey) ?? "";
            if (raw.Length > 8192) continue;
            try
            {
                var spec = BuildingSpec.Parse(JObject.Parse(raw));
                rows.Add(new JObject { ["building_id"] = spec.Id, ["object_id"] = item.Id.ToString(),
                    ["floors"] = spec.Floors, ["floor_height"] = spec.FloorHeight, ["height"] = spec.Height,
                    ["layer"] = spec.Layer, ["z"] = spec.Elevation });
            }
            catch { rows.Add(new JObject { ["object_id"] = item.Id.ToString(), ["invalid_definition"] = true }); }
        }
        return new JObject { ["count"] = objects.Length, ["offset"] = offset,
            ["next_offset"] = offset + limit < objects.Length ? (JToken)(offset + limit) : JValue.CreateNull(), ["buildings"] = rows };
    }
}
