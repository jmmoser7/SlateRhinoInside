using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;
using Rhino.Runtime;

namespace SlateRhinoInside;
internal sealed partial class RhinoAdapter
{
    private static string[] NameList(JToken? token, int maximum, bool required)
    {
        if (token == null) return required ? throw new ProtocolFailure("invalid_request", "Explicit layer names required") : Array.Empty<string>();
        var array = JArray.Parse((string)token!);
        if (array.Count > maximum || (required && array.Count == 0) || array.Any(v => v.Type != JTokenType.String))
            throw new ProtocolFailure("invalid_request", "Name list exceeds bounds");
        var names = array.Values<string>().Select(v => v!).ToArray();
        if (names.Any(v => string.IsNullOrWhiteSpace(v) || v.Length > 512) || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length)
            throw new ProtocolFailure("invalid_request", "Names must be nonempty and unique");
        return names;
    }
    private static JObject InspectNamedViews(RhinoDoc doc, JObject p)
    {
        int offset = (int?)p["offset"] ?? 0, limit = (int?)p["limit"] ?? 64;
        var views = new JArray();
        for (int i = offset; i < Math.Min(doc.NamedViews.Count, offset + limit); i++)
        {
            var view = doc.NamedViews[i];
            views.Add(new JObject { ["index"] = i, ["name"] = view.Name, ["id"] = view.NamedViewId.ToString() });
        }
        return new JObject { ["count"] = doc.NamedViews.Count, ["offset"] = offset,
            ["next_offset"] = offset + limit < doc.NamedViews.Count ? (JToken)(offset + limit) : JValue.CreateNull(), ["views"] = views };
    }
    private JObject CaptureNamedViews(RhinoDoc doc, JObject p)
    {
        var people = NameList(p["people_layers"], 64, true);
        var requested = NameList(p["view_names"], 64, false);
        var indices = requested.Length == 0 ? Enumerable.Range(0, doc.NamedViews.Count).ToArray()
            : requested.Select(name => doc.NamedViews.FindByName(name)).ToArray();
        if (indices.Any(index => index < 0)) throw new ProtocolFailure("invalid_request", "A named view was not found");
        int offset = (int?)p["offset"] ?? 0, limit = (int?)p["limit"] ?? 1;
        var batch = indices.Skip(offset).Take(limit).ToArray();
        if (batch.Length == 0) throw new ProtocolFailure("invalid_request", "No named views in the requested page");
        if (doc.Layers.Count > 1024) throw new ProtocolFailure("invalid_request", "Layer snapshot exceeds 1024 entries");
        var layerStates = doc.Layers.Where(layer => !layer.IsDeleted).Select(layer => new LayerSnapshot(layer)).ToArray();
        var peopleIds = new HashSet<Guid>();
        foreach (string path in people)
        {
            int index = doc.Layers.FindByFullPath(path, RhinoMath.UnsetIntIndex);
            if (index < 0) throw new ProtocolFailure("invalid_request", "A people layer was not found");
            foreach (var layer in doc.Layers.Where(l => !l.IsDeleted && (l.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase) || l.FullPath.StartsWith(path + "::", StringComparison.OrdinalIgnoreCase))))
            {
                if (layer.IsReference) throw new ProtocolFailure("authority_denied", "People-layer visibility must be locally editable");
                peopleIds.Add(layer.Id);
            }
            for (var layer = doc.Layers[index]; layer.ParentLayerId != Guid.Empty;)
            {
                int parent = doc.Layers.Find(layer.ParentLayerId, true, RhinoMath.UnsetIntIndex);
                if (parent < 0 || doc.Layers[parent].IsReference) throw new ProtocolFailure("authority_denied", "People layer has an unavailable/reference parent");
                layer = doc.Layers[parent];
            }
        }
        int originalCurrent = doc.Layers.CurrentLayerIndex;
        int safeCurrent = SafeCurrentLayer(doc, layer => peopleIds.Contains(layer.Id));
        var nativeView = doc.Views.ActiveView ?? throw new ProtocolFailure("invalid_result", "No active native viewport");
        var viewport = nativeView.ActiveViewport;
        using var original = new ViewportInfo(viewport);
        var originalTarget = viewport.CameraTarget; var originalPlane = viewport.GetConstructionPlane();
        var originalDisplay = viewport.DisplayMode; string originalName = viewport.Name; bool originalDirty = doc.Modified;
        bool Restore(bool final = false)
        {
            bool restored = doc.Layers.CurrentLayerIndex == safeCurrent || doc.Layers.SetCurrentLayerIndex(safeCurrent, true);
            foreach (var snapshot in layerStates)
            {
                if (snapshot.Index >= doc.Layers.Count || doc.Layers[snapshot.Index].Id != snapshot.Id) { restored = false; continue; }
                using var value = CommonObject.FromJSON(snapshot.Json);
                if (value is not Layer layer || !doc.Layers.Modify(layer, snapshot.Index, true)) restored = false;
                else
                {
                    var actual = doc.Layers[snapshot.Index];
                    if (actual.IsVisible != snapshot.Visible || actual.IsLocked != snapshot.Locked
                        || actual.GetPersistentVisibility() != snapshot.PersistentVisible || actual.GetPersistentLocking() != snapshot.PersistentLocked) restored = false;
                }
            }
            restored &= viewport.SetViewProjection(original, false);
            viewport.SetCameraTarget(originalTarget, false); viewport.SetConstructionPlane(originalPlane);
            viewport.DisplayMode = originalDisplay; viewport.Name = originalName;
            if (final && (doc.Layers.CurrentLayerIndex != originalCurrent && !doc.Layers.SetCurrentLayerIndex(originalCurrent, true)
                || doc.Layers.CurrentLayerIndex != originalCurrent)) restored = false;
            if (restored) doc.Modified = originalDirty;
            doc.Views.Redraw(); return restored;
        }
        string directory = Path.Combine(options.SourceRoot, ".slate-rhino-captures", Guid.NewGuid().ToString("N"));
        RejectReparse(directory); Directory.CreateDirectory(directory); RejectReparse(directory);
        var captures = new JArray(); string? failure = null; bool restoration = false;
        try
        {
            foreach (int index in batch)
            foreach (bool visible in new[] { true, false })
            {
                if (!Restore()) throw new InvalidOperationException("Baseline state restoration failed");
                if (!doc.NamedViews.Restore(index, viewport)) throw new InvalidOperationException("Named view restore failed");
                var targets = new HashSet<int>(doc.Layers.Where(l => !l.IsDeleted && peopleIds.Contains(l.Id)).Select(l => l.Index));
                if (visible)
                {
                    foreach (int target in targets.ToArray())
                    {
                        for (var ancestor = doc.Layers[target]; ancestor.ParentLayerId != Guid.Empty;)
                        {
                            int parent = doc.Layers.Find(ancestor.ParentLayerId, true, RhinoMath.UnsetIntIndex);
                            if (parent < 0) throw new InvalidOperationException("Layer parent disappeared");
                            targets.Add(parent); ancestor = doc.Layers[parent];
                        }
                    }
                }
                foreach (int target in targets.OrderBy(i => doc.Layers[i].FullPath.Split(new[] { "::" }, StringSplitOptions.None).Length))
                    SetLayerVisibility(doc, target, visible);
                if (doc.Layers.Where(l => !l.IsDeleted && peopleIds.Contains(l.Id)).Any(l => LayerEffectiveVisible(doc, l) != visible))
                    throw new InvalidOperationException("People-layer effective visibility could not be verified");
                doc.Views.Redraw();
                var capture = Capture(Contract.Schema());
                string stem = "view-" + index.ToString("D4") + (visible ? "-people" : "-no-people");
                string png = Path.Combine(directory, stem + ".png"), metadata = Path.Combine(directory, stem + ".native.json");
                WriteNew(png, capture.Bytes); WriteNew(metadata, Encoding.UTF8.GetBytes(capture.Metadata.ToString(Formatting.None)));
                captures.Add(new JObject { ["view_name"] = doc.NamedViews[index].Name, ["view_index"] = index,
                    ["people_visible"] = visible, ["png"] = png, ["metadata"] = metadata,
                    ["width_px"] = capture.Metadata["width_px"]!.DeepClone(), ["height_px"] = capture.Metadata["height_px"]!.DeepClone() });
            }
        }
        catch (Exception error) { failure = error.Message.Length > 256 ? error.Message.Substring(0, 256) : error.Message; }
        finally { try { restoration = Restore(true); } catch { restoration = false; } }
        var manifest = new JObject { ["complete"] = failure == null && restoration, ["original_state_restored"] = restoration,
            ["format"] = "raw_png_and_native_metadata_sidecar", ["source"] = Scope(), ["captures"] = captures,
            ["people_layers"] = new JArray(people), ["offset"] = offset,
            ["next_offset"] = offset + batch.Length < indices.Length ? (JToken)(offset + batch.Length) : JValue.CreateNull() };
        if (failure != null) manifest["error"] = failure;
        string manifestPath = Path.Combine(directory, "manifest.json");
        WriteNew(manifestPath, Encoding.UTF8.GetBytes(manifest.ToString(Formatting.Indented)));
        return new JObject { ["complete"] = manifest["complete"]!.DeepClone(), ["original_state_restored"] = restoration,
            ["manifest"] = manifestPath, ["captures"] = captures.DeepClone(), ["next_offset"] = manifest["next_offset"]!.DeepClone() };
    }
    private static void WriteNew(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
    }
    private sealed class LayerSnapshot
    {
        internal readonly int Index; internal readonly Guid Id; internal readonly string Json;
        internal readonly bool Visible, Locked, PersistentVisible, PersistentLocked;
        internal LayerSnapshot(Layer layer) { Index = layer.Index; Id = layer.Id; Json = layer.ToJSON(new SerializationOptions());
            Visible = layer.IsVisible; Locked = layer.IsLocked; PersistentVisible = layer.GetPersistentVisibility(); PersistentLocked = layer.GetPersistentLocking(); }
    }
}
