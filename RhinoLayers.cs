using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.FileIO;

namespace SlateRhinoInside;
internal sealed partial class RhinoAdapter
{
    private bool TryReadWorkflow(RhinoDoc doc, string id, JObject p, out JObject? value)
    {
        value = null;
        switch (id)
        {
            case "org.slate.rhino.layers": value = InspectLayers(doc, p); break;
            case "org.slate.rhino.named_views": value = InspectNamedViews(doc, p); break;
            case "org.slate.rhino.capture_named_views": NativeCommandPolicy.RequireIdleDraft(RhinoApp.InCommand); value = CaptureNamedViews(doc, p); break;
            case "org.slate.rhino.buildings": value = InspectBuildings(doc, p); break;
            case "org.slate.rhino.grasshopper_show": NativeCommandPolicy.RequireIdleDraft(RhinoApp.InCommand); value = ShowGrasshopper(); break;
            case "org.slate.rhino.grasshopper_hide": value = HideGrasshopper(); break;
            default: return false;
        }
        return true;
    }
    private bool TryWriteWorkflow(RhinoDoc doc, string id, JObject p, out JObject? value)
    {
        value = null;
        switch (id)
        {
            case "org.slate.rhino.draft_layers_from_model":
                var specs = ReadSourceLayers((string)p["source_relative"]!);
                string prefix = (string?)p["prefix"] ?? "";
                if (prefix.Length > 0) foreach (var spec in specs) spec.Path = LayerSpec.ValidatePath(prefix + "::" + spec.Path);
                value = ApplyLayers(doc, specs, (bool?)p["update_existing"] ?? false); break;
            case "org.slate.rhino.draft_layers_from_csv":
                bool inline = p["csv"] != null, file = p["source_relative"] != null;
                if (inline == file) throw new ProtocolFailure("invalid_request", "Supply exactly one of csv or source_relative");
                string text = inline ? (string)p["csv"]! : ReadBoundedData((string)p["source_relative"]!, ".csv", 262144);
                value = ApplyLayers(doc, LayerSpec.FromCsv(text), (bool?)p["update_existing"] ?? false); break;
            case "org.slate.rhino.draft_named_view_add":
                string name = (string)p["name"]!;
                if (string.IsNullOrWhiteSpace(name) || doc.NamedViews.FindByName(name) >= 0)
                    throw new ProtocolFailure("invalid_request", "Use a new nonempty named-view name");
                value = WorkflowMutation(doc, id, () => {
                    int index = doc.NamedViews.Add(name, doc.Views.ActiveView.ActiveViewport.Id);
                    if (index < 0) throw new ProtocolFailure("invalid_result", "Rhino refused the named view");
                    return new JObject { ["complete"] = true, ["name"] = name, ["index"] = index };
                }); break;
            case "org.slate.rhino.draft_building_set": value = SetBuilding(doc, BuildingSpec.Parse(p)); break;
            case "org.slate.rhino.draft_building_floors": value = UpdateBuildingFloors(doc, p); break;
            case "org.slate.rhino.draft_layout_template": value = CreateLayoutTemplate(doc, LayoutTemplateSpec.Parse(p)); break;
            default: return false;
        }
        return true;
    }
    private string ReadBoundedData(string relative, string extension, long maximum)
    {
        if (Path.IsPathRooted(relative)) throw new ProtocolFailure("authority_denied", "Use a source-root-relative input file");
        string path = ResolveAuxiliarySource(relative, extension);
        if (new FileInfo(path).Length > maximum) throw new ProtocolFailure("invalid_request", "Input file exceeds its size budget");
        return File.ReadAllText(path, new System.Text.UTF8Encoding(false, true));
    }
    private List<LayerSpec> ReadSourceLayers(string relative)
    {
        if (Path.IsPathRooted(relative)) throw new ProtocolFailure("authority_denied", "Use a source-root-relative model");
        string path = ResolveAuxiliarySource(relative, ".3dm");
        if (new FileInfo(path).Length > 268435456) throw new ProtocolFailure("invalid_request", "Layer source exceeds 256 MiB");
        using var file = File3dm.Read(path)
            ?? throw new ProtocolFailure("invalid_result", "Rhino could not read source layers");
        return LayerSpecs(file.AllLayers.Where(layer => !layer.IsDeleted));
    }
    private string ResolveAuxiliarySource(string relative, string extension)
    {
        try { return ResolveSource(new JObject { ["relative"] = relative }, false, extension); }
        catch (ProtocolFailure error) { throw RhinoPaths.AuxiliaryFailure(error); }
    }
    private static List<LayerSpec> LayerSpecs(IEnumerable<Layer> source)
    {
        var layers = source.ToArray();
        if (layers.Length > 512) throw new ProtocolFailure("invalid_request", "Layer table exceeds 512-layer budget");
        var lookup = layers.ToDictionary(layer => layer.Id);
        string FullPath(Layer layer, HashSet<Guid> seen)
        {
            if (!seen.Add(layer.Id) || seen.Count > 16) throw new ProtocolFailure("invalid_request", "Cyclic or deep layer hierarchy");
            if (layer.ParentLayerId == Guid.Empty) return layer.Name;
            if (!lookup.TryGetValue(layer.ParentLayerId, out var parent)) throw new ProtocolFailure("invalid_request", "Layer parent is missing");
            return FullPath(parent, seen) + "::" + layer.Name;
        }
        return layers.Select(layer => new LayerSpec { Path = LayerSpec.ValidatePath(FullPath(layer, new HashSet<Guid>())),
            Red = layer.Color.R, Green = layer.Color.G, Blue = layer.Color.B,
            Visible = layer.IsVisible, Locked = layer.IsLocked }).ToList();
    }
    private JObject InspectLayers(RhinoDoc doc, JObject p)
    {
        var specs = p["source_relative"] is JToken path ? ReadSourceLayers((string)path!) : LayerSpecs(doc.Layers.Where(l => !l.IsDeleted));
        int offset = (int?)p["offset"] ?? 0, limit = (int?)p["limit"] ?? 64;
        return new JObject { ["count"] = specs.Count, ["offset"] = offset,
            ["next_offset"] = offset + limit < specs.Count ? (JToken)(offset + limit) : JValue.CreateNull(),
            ["layers"] = new JArray(specs.Skip(offset).Take(limit).Select(s => new JObject {
                ["path"] = s.Path, ["parent"] = s.Parent, ["red"] = s.Red, ["green"] = s.Green, ["blue"] = s.Blue,
                ["visible"] = s.Visible, ["locked"] = s.Locked })) };
    }
    private JObject ApplyLayers(RhinoDoc doc, List<LayerSpec> supplied, bool update)
    {
        LayerSpec.ValidateUnique(supplied);
        var expanded = new Dictionary<string, LayerSpec>(StringComparer.OrdinalIgnoreCase);
        foreach (var spec in supplied)
        {
            string parent = spec.Parent;
            while (parent.Length > 0)
            {
                if (!expanded.ContainsKey(parent)) expanded[parent] = new LayerSpec { Path = parent };
                parent = expanded[parent].Parent;
            }
            expanded[spec.Path] = spec;
        }
        if (expanded.Count > 512) throw new ProtocolFailure("invalid_request", "Expanded hierarchy exceeds 512 layers");
        foreach (var spec in expanded.Values) ValidateLayerTarget(doc, spec.Path);
        var disabled = update ? supplied.Where(s => !s.Visible || s.Locked).Select(s => s.Path).ToArray() : Array.Empty<string>();
        int current = doc.Layers.CurrentLayerIndex;
        int safeCurrent = SafeCurrentLayer(doc, layer => disabled.Any(path => layer.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase)
            || layer.FullPath.StartsWith(path + "::", StringComparison.OrdinalIgnoreCase)));
        return WorkflowMutation(doc, "replicate layers", () => {
            if (current != safeCurrent && !doc.Layers.SetCurrentLayerIndex(safeCurrent, true))
                throw new ProtocolFailure("invalid_result", "Rhino could not move the current layer before changing its mode");
            int created = 0, changed = 0, reused = 0;
            foreach (var spec in expanded.Values.OrderBy(s => s.Path.Split(new[] { "::" }, StringSplitOptions.None).Length))
            {
                int index = doc.Layers.FindByFullPath(spec.Path, RhinoMath.UnsetIntIndex);
                if (index < 0)
                {
                    Guid parent = spec.Parent.Length == 0 ? Guid.Empty : doc.Layers[doc.Layers.FindByFullPath(spec.Parent, RhinoMath.UnsetIntIndex)].Id;
                    using var layer = new Layer { Name = spec.Name, ParentLayerId = parent,
                        Color = Color.FromArgb(spec.Red, spec.Green, spec.Blue), IsVisible = spec.Visible, IsLocked = spec.Locked };
                    index = doc.Layers.Add(layer);
                    if (index < 0) throw new ProtocolFailure("invalid_result", "Rhino refused layer " + spec.Path);
                    created++;
                }
                else if (update && supplied.Any(s => string.Equals(s.Path, spec.Path, StringComparison.OrdinalIgnoreCase)))
                {
                    using var layer = doc.Layers[index]; layer.EnsurePrivateCopy();
                    layer.Color = Color.FromArgb(spec.Red, spec.Green, spec.Blue);
                    layer.IsVisible = spec.Visible; layer.IsLocked = spec.Locked;
                    layer.SetPersistentVisibility(spec.Visible); layer.SetPersistentLocking(spec.Locked);
                    if (!doc.Layers.Modify(layer, index, true)) throw new ProtocolFailure("invalid_result", "Rhino refused layer settings");
                    var actual = doc.Layers[index];
                    if (actual.Color.ToArgb() != layer.Color.ToArgb() || actual.IsVisible != spec.Visible || actual.IsLocked != spec.Locked)
                        throw new ProtocolFailure("invalid_result", "Native layer settings did not match the request");
                    changed++;
                }
                else reused++;
            }
            return new JObject { ["complete"] = true, ["created"] = created, ["updated"] = changed, ["reused"] = reused,
                ["layers"] = expanded.Count, ["current_layer_changed"] = current != safeCurrent };
        });
    }
    private static int SafeCurrentLayer(RhinoDoc doc, Func<Layer, bool> excluded)
    {
        bool Safe(Layer layer) => !layer.IsDeleted && !layer.IsReference && !layer.IsLocked && !excluded(layer) && LayerEffectiveVisible(doc, layer);
        var current = doc.Layers[doc.Layers.CurrentLayerIndex];
        if (Safe(current)) return current.Index;
        foreach (var layer in doc.Layers) if (Safe(layer)) return layer.Index;
        throw new ProtocolFailure("invalid_request", "No unaffected visible unlocked current layer is available");
    }
    private static bool LayerEffectiveVisible(RhinoDoc doc, Layer layer)
    {
        var seen = new HashSet<Guid>();
        for (;;)
        {
            if (!seen.Add(layer.Id) || !layer.IsVisible || layer.IsDeleted) return false;
            if (layer.ParentLayerId == Guid.Empty) return true;
            int parent = doc.Layers.Find(layer.ParentLayerId, true, RhinoMath.UnsetIntIndex);
            if (parent < 0) return false;
            layer = doc.Layers[parent];
        }
    }
    private static void SetLayerVisibility(RhinoDoc doc, int index, bool visible)
    {
        using var layer = doc.Layers[index]; layer.EnsurePrivateCopy();
        layer.IsVisible = visible; layer.SetPersistentVisibility(visible);
        if (!doc.Layers.Modify(layer, index, true) || doc.Layers[index].IsVisible != visible
            || doc.Layers[index].GetPersistentVisibility() != visible)
            throw new ProtocolFailure("invalid_result", "Rhino refused the requested layer visibility");
    }
    private static void ValidateLayerTarget(RhinoDoc doc, string path)
    {
        LayerSpec.ValidatePath(path);
        foreach (string name in path.Split(new[] { "::" }, StringSplitOptions.None))
            if (!ModelComponent.IsValidComponentName(name)) throw new ProtocolFailure("invalid_request", "Invalid Rhino layer name");
        int index = doc.Layers.FindByFullPath(path, RhinoMath.UnsetIntIndex);
        if (index >= 0 && doc.Layers[index].IsReference) throw new ProtocolFailure("authority_denied", "Cannot modify reference layers");
    }
    private static JObject WorkflowMutation(RhinoDoc doc, string label, Func<JObject> action)
    {
        uint undo = doc.BeginUndoRecord("Slate isolated draft: " + label);
        JObject result;
        try { result = action(); }
        catch (Exception error) { result = new JObject { ["complete"] = false, ["effects_may_have_occurred"] = true,
            ["error"] = error.Message.Length > 256 ? error.Message.Substring(0, 256) : error.Message }; }
        try { if (undo != 0) doc.EndUndoRecord(undo); doc.Views.Redraw(); }
        catch { result["complete"] = false; result["cleanup_verified"] = false; }
        return result;
    }
}
