using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.Display;
using Rhino.FileIO;

namespace SlateRhinoInside;
internal sealed partial class RhinoAdapter : IDisposable
{
    private readonly Options options;
    private readonly RuntimeTrace trace;
    private RhinoDoc? document;
    private uint documentSerial;
    private string mode = "", draftPath = "", originalPath = "";
    private readonly IntPtr window, originalParent, originalOwner;
    private readonly NativeWindowRecovery windowRecovery;
    private readonly QueuedRecoveryGate recovery = new QueuedRecoveryGate();
    private bool attached;
    public string ApplicationVersion => RhinoApp.Version.ToString();
    public bool Dirty => document?.Modified ?? false;
    public bool Isolated => mode == "isolated_draft";
    public JObject? BoundSource { get; private set; }
    public RhinoAdapter(Options launch, RuntimeTrace diagnostics, string privateScheme)
    {
        options = launch;
        trace = diagnostics;
        window = RhinoApp.MainWindowHandle();
        if (window == IntPtr.Zero || !Native.IsWindow(window)) throw new InvalidOperationException("Rhino did not create a full UI window");
        Native.GetWindowThreadProcessId(window, out uint pid);
        if (pid != Process.GetCurrentProcess().Id) throw new InvalidOperationException("Rhino window is not owned by this helper");
        windowRecovery = new NativeWindowRecovery(window);
        originalParent = windowRecovery.Parent;
        originalOwner = windowRecovery.Owner;
        // Preserve the SDK's recovery placement before parking. Only a later
        // authenticated, placed host surface may reveal the initialized UI.
        Native.ShowWindow(window, 0);
        RhinoApp.EscapeKeyPressed += OnNativeEscape;
        InitializeUiScale(privateScheme);
    }
    public Task<T> OnUiAsync<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        RhinoApp.InvokeOnUiThread(new Action(() => { try { completion.SetResult(action()); } catch (Exception error) { completion.SetException(error); } }));
        return completion.Task;
    }
    public JObject Bind(JObject source, string sourceMode)
    {
        if (document != null) throw new ProtocolFailure("invalid_request", "One model is allowed per Rhino helper; launch another helper for another model");
        if (sourceMode != "linked" && sourceMode != "isolated_draft") throw new ProtocolFailure("invalid_request", "Unknown source mode");
        string path = ResolveSource((JObject)source["locator"]!, sourceMode == "isolated_draft");
        if (RhinoDoc.ActiveDoc?.Modified == true) throw new ProtocolFailure("dirty_document", "Rhino has an unsaved document; preserve it before binding");
        mode = sourceMode;
        originalPath = path;
        string openPath = path;
        if (Isolated && File.Exists(path))
        {
            string folder = Path.Combine(options.SourceRoot, ".slate-rhino-drafts");
            Directory.CreateDirectory(folder);
            RejectReparse(folder);
            draftPath = RhinoPaths.ForNativeApi(Path.Combine(folder, Path.GetFileNameWithoutExtension(path) + "-" + Guid.NewGuid().ToString("N") + ".3dm"));
            File.Copy(path, draftPath, false);
            openPath = draftPath;
        }
        else if (Isolated) draftPath = path;
        document = File.Exists(openPath) ? RhinoDoc.Open(openPath, out _) : RhinoDoc.Create(null);
        if (document == null) throw new ProtocolFailure("invalid_result", "Rhino could not open the selected model");
        documentSerial = document.RuntimeSerialNumber;
        geometryResources.Bind(documentSerial);
        if (!File.Exists(openPath)) { document.ModelUnitSystem = UnitSystem.Meters; document.ModelAbsoluteTolerance = 0.001; }
        initialView = InitializeBoundView(document, openPath);
        BoundSource = (JObject)source.DeepClone();
        return new JObject { ["source"] = source.DeepClone(), ["document_id"] = "rhino-" + documentSerial, ["mode"] = mode, ["dirty"] = document.Modified };
    }
    private string ResolveSource(JObject locator, bool allowNew, string extension = ".3dm")
    {
        string root = RhinoPaths.ForFileSystem(options.SourceRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string? relative = (string?)locator["relative"], absolute = (string?)locator["absolute"];
        string path = !string.IsNullOrEmpty(relative) ? Path.GetFullPath(Path.Combine(root, relative!.Replace('/', '\\'))) : RhinoPaths.ForFileSystem(absolute ?? throw new ProtocolFailure("invalid_request", "Missing source locator"));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase)) throw new ProtocolFailure("authority_denied", "File must be a " + extension + " inside the approved source root");
        if (!allowNew && !File.Exists(path)) throw new ProtocolFailure("source_not_bound", "Selected Rhino file is missing");
        string parent = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(parent)) throw new ProtocolFailure("invalid_request", "Model parent directory must exist");
        RejectReparse(path);
        return RhinoPaths.ForNativeApi(path);
    }
    private static void RejectReparse(string path)
    {
        for (string? current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && ((uint)File.GetAttributes(current) & (0x400u | 0x1000u | 0x40000u | 0x400000u)) != 0)
                throw new ProtocolFailure("authority_denied", "Reparse/cloud paths must be made local before Rhino binding");
    }
    private RhinoDoc Document()
    {
        var doc = document;
        if (doc == null || !doc.IsAvailable || doc.RuntimeSerialNumber != documentSerial) throw new ProtocolFailure("source_not_bound", "The bound Rhino document is no longer available");
        if (RhinoDoc.ActiveDoc?.RuntimeSerialNumber != documentSerial) throw new ProtocolFailure("source_mismatch", "Rhino switched documents; rebind explicitly");
        return doc;
    }
    public JObject Scope() => new JObject { ["source_id"] = BoundSource!["source_id"]!.DeepClone(), ["revision"] = BoundSource["revision"]!.DeepClone() };
    public JObject SurfaceDescriptor(string surfaceMode = "native_full_ui") => new JObject { ["surface_id"] = "rhino-main", ["mode"] = surfaceMode, ["process_ownership"] = "launched_dedicated", ["native_handle"] = window.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture), ["can_detach"] = true };
    public JArray Surfaces()
    {
        Document(); var surfaces = new JArray(SurfaceDescriptor());
        var grasshopper = GrasshopperSurface(); if (grasshopper != null) surfaces.Add(grasshopper);
        return surfaces;
    }
    public void Surface(JObject action)
    {
        Document();
        string target = (string?)action["surface_id"] ?? (string?)action["surface"]?["surface_id"] ?? "rhino-main";
        if (target == "grasshopper") { GrasshopperAction(action); return; }
        if (target != "rhino-main") throw new ProtocolFailure("invalid_request", "Unknown native surface identity");
        switch ((string?)action["action"])
        {
            case "attach":
                var descriptor = action["surface"] as JObject;
                if (attached || descriptor == null || (string?)descriptor["surface_id"] != "rhino-main" || (string?)descriptor["native_handle"] != window.ToInt64().ToString()) throw new ProtocolFailure("invalid_request", "Invalid or duplicate Rhino surface attachment");
                attached = true; ResumeUiScale(); break;
            case "show": RequireHostedSurface(window, originalParent, originalOwner, false); Native.ShowWindowAsync(window, 4); RaiseHostedPresentation(); break;
            case "hide": HideMainSynchronously(); break;
            case "focus": RequireHostedSurface(window, originalParent, originalOwner, true); RhinoApp.SetFocusToMainWindow(Document()); RaiseHostedPresentation(); break;
            case "blur": Native.SetFocus(IntPtr.Zero); break;
            case "place": break; // Host NativeSurface is the one geometry owner.
            default: throw new ProtocolFailure("invalid_request", "Unknown surface action");
        }
    }
    private void HideMainSynchronously()
    {
        bool OwnUiWindow()
        {
            uint thread = Native.GetWindowThreadProcessId(window, out uint process);
            return NativeWindowRecovery.CanRecover(Native.IsWindow(window), process, Native.GetCurrentProcessId(), thread, Native.GetCurrentThreadId());
        }
        if (!OwnUiWindow()) throw new ProtocolFailure("invalid_request", "Native hide requires this helper's window and UI thread");
        // ShowWindow returns the prior visibility, not a success indication.
        Native.ShowWindow(window, 0);
        if (!OwnUiWindow() || Native.IsWindowVisible(window)
            || (Native.GetWindowLongPtr(window, -16).ToInt64() & 0x10000000L) != 0)
            throw new ProtocolFailure("invalid_result", "Rhino did not confirm its native window was hidden");
    }
    public void Detach(string policy)
    {
        if (policy != "preserve" && policy != "close_if_clean") throw new ProtocolFailure("invalid_request", "Unknown detach policy");
        if (policy == "close_if_clean" && Dirty) throw new ProtocolFailure("dirty_document", "Unsaved Rhino work remains open");
        RestoreWindow();
    }
    private void RestoreWindow()
    {
        SuspendUiScale();
        RestoreUiScale();
        RestoreGrasshopper();
        windowRecovery.Restore();
        attached = false;
    }
    public void ConnectionLost()
    {
        if (!recovery.TryRequest()) return;
        try { RhinoApp.InvokeOnUiThread(new Action(() => { if (recovery.TryExecute()) RestoreWindow(); })); }
        catch (Exception error) { Console.Error.WriteLine("Window restoration: " + error.Message); }
    }
    public JObject Invoke(string id, JObject parameters)
    {
        var doc = Document();
        if (id == "org.slate.rhino.inspect") return new JObject { ["initial_view"] = initialView.DeepClone(), ["objects"] = LiveObjectCount(doc), ["layers"] = doc.Layers.Count, ["units"] = doc.ModelUnitSystem.ToString(), ["dirty"] = doc.Modified, ["document_id"] = "rhino-" + documentSerial, ["draft_path"] = draftPath, ["original_path"] = originalPath, ["command_active"] = RhinoApp.InCommand > 0, ["menu_present"] = Native.GetMenu(window) != IntPtr.Zero, ["idle_escape_relay"] = true, ["escape_hook_seen"] = keyboardHookSeen, ["escape_observed"] = escapeObserved, ["escape_state_mask"] = lastEscapeMask, ["ui_scaling"] = UiScaleState(), ["native_presentation"] = NativePresentationState() };
        if (id == "org.slate.rhino.zoom_extents") { doc.Views.ActiveView.ActiveViewport.ZoomExtents(); doc.Views.Redraw(); return new JObject { ["framed"] = true }; }
        if (id == "org.slate.rhino.set_camera") { SetCamera(parameters); return new JObject { ["camera_changed"] = true }; }
        if (TryReadWorkflow(doc, id, parameters, out var workflow)) return workflow!;
        if (!Isolated || !id.StartsWith("org.slate.rhino.draft_", StringComparison.Ordinal)) throw new ProtocolFailure("authority_denied", "Only isolated draft modeling is supported");
        NativeCommandPolicy.RequireIdleDraft(RhinoApp.InCommand);
        if (TryWriteWorkflow(doc, id, parameters, out workflow)) return workflow!;
        if (id == "org.slate.rhino.draft_save")
        {
            if (!RhinoApp.CanSave) throw new ProtocolFailure("authority_denied", "Rhino license does not permit saving");
            RejectReparse(draftPath);
            string nativePath = RhinoPaths.ForNativeApi(draftPath);
            using var write = new FileWriteOptions { SuppressDialogBoxes = true, SuppressAllInput = true, UpdateDocumentPath = true, FileVersion = 8, IncludeRenderMeshes = true, IncludePreviewImage = true, IncludeHistory = true, WriteUserData = true, WriteSelectedObjectsOnly = false, WriteGeometryOnly = false };
            if (!doc.WriteFile(nativePath, write)) throw new ProtocolFailure("invalid_result", "Rhino could not save isolated draft: " + nativePath);
            return new JObject { ["saved"] = true, ["path"] = draftPath };
        }
        if (id == "org.slate.rhino.draft_clear") return ClearDraft(doc);
        if (LiveObjectCount(doc) >= 20000) throw new ProtocolFailure("invalid_request", "Draft geometry budget reached");
        uint undo = doc.BeginUndoRecord("Slate isolated draft: " + id);
        try
        {
            return AddGeometry(doc, id, parameters);
        }
        finally { if (undo != 0) doc.EndUndoRecord(undo); doc.Views.Redraw(); }
    }
}




