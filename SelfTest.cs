using System;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace SlateRhinoInside;
internal static partial class SelfTest
{
    public static int Run()
    {
        int assertions = 0;
        void Check(bool condition) { if (!condition) throw new Exception("Self-test assertion " + (assertions + 1) + " failed"); assertions++; }
        Check(Contract.Commands().Count == 20);
        Check(!Contract.Commands().Any(command => (string?)command["id"] == "org.slate.rhino.ui_scale"));
        Check(ConnectorSession.IdleFrameTimeout == TimeSpan.FromHours(8));
        Check(!InitialViewPolicy.ShouldInitialize(true, true, true, true));
        Check(!InitialViewPolicy.ShouldInitialize(null, true, true, true));
        Check(InitialViewPolicy.ShouldInitialize(false, true, true, true));
        Check(InitialViewPolicy.ShouldInitialize(true, false, false, false));
        Check(InitialViewPolicy.ShouldInitialize(true, true, false, true));
        Check(InitialViewPolicy.ShouldInitialize(null, true, true, false));
        var clear = Contract.Commands().OfType<JObject>().Single(v => (string?)v["id"] == "org.slate.rhino.draft_clear");
        Check((string?)clear["effect"] == "draft_mutation" && (string?)clear["undo"] == "native_application");
        Check((int)Contract.Hello("test")["versions"]![0]!["minor"]! == 2);
        Check(ConnectorSession.ValidId("agent.design-1")); Check(!ConnectorSession.ValidId("../bad"));
        foreach (JObject descriptor in Contract.Commands()) Contract.ValidateParameters(descriptor, ValidParameters((string)descriptor["id"]!));
        bool rejected = false; try { Framing.Parse(System.Text.Encoding.UTF8.GetBytes("{\"x\":1,\"x\":2}")); } catch { rejected = true; } Check(rejected);
        rejected = false; try { Framing.Parse(new byte[] { 255 }); } catch { rejected = true; } Check(rejected);
        rejected = false; try { Contract.ValidateParameters((JObject)Contract.Commands()[0], new JObject { ["script"] = "arbitrary" }); } catch (ProtocolFailure) { rejected = true; } Check(rejected);
        using var memory = new MemoryStream();
        Framing.WriteJsonAsync(memory, new JObject { ["test"] = true }, CancellationToken.None).GetAwaiter().GetResult(); memory.Position = 0;
        var decoded = Framing.Parse(Framing.ReadAsync(memory, CancellationToken.None).GetAwaiter().GetResult()!); Check((bool)decoded["test"]!);
        Check(Framing.Hash(System.Text.Encoding.ASCII.GetBytes(new string('a',64) + "\0capture-1")) == "395d6749d2ead254b559ac20402526e35026ae1b5c0b10434069a1cae08651bf");
        using var invalid = new MemoryStream(new byte[] { 0,0,64,1 });
        rejected = false; try { Framing.ReadAsync(invalid,CancellationToken.None).GetAwaiter().GetResult(); } catch (InvalidDataException) { rejected = true; } Check(rejected);
        Check(RhinoPaths.ForNativeApi(@"C:\approved\model.3dm") == @"C:\approved\model.3dm");
        Check(RhinoPaths.ForFileSystem(@"\\?\C:\approved/models/source-A.3dm") == @"C:\approved\models\source-A.3dm");
        Check(RhinoPaths.ForFileSystem(@"\\?\UNC\server\share/models/source-A.3dm") == @"\\server\share\models\source-A.3dm");
        Check(RhinoPaths.IsBoundLinkedPath("linked", @"C:\approved\model.3dm", @"C:\approved\model.3dm"));
        Check(RhinoPaths.IsBoundLinkedPath("linked", @"\\?\C:\APPROVED/model.3dm", @"c:\approved\model.3dm"));
        Check(RhinoPaths.IsBoundLinkedPath("linked", @"\\?\UNC\SERVER\share/model.3dm", @"\\server\share\model.3dm"));
        Check(!RhinoPaths.IsBoundLinkedPath("isolated_draft", @"C:\approved\model.3dm", @"C:\approved\model.3dm"));
        Check(!RhinoPaths.IsBoundLinkedPath("", @"C:\approved\model.3dm", @"C:\approved\model.3dm"));
        Check(!RhinoPaths.IsBoundLinkedPath("linked", @"C:\private\saved-as.3dm", @"C:\approved\model.3dm"));
        Check(!RhinoPaths.IsBoundLinkedPath("linked", null, @"C:\approved\model.3dm"));
        Check(!RhinoPaths.IsBoundLinkedPath("linked", @"C:\approved\model.3dm", null));
        Check(!RhinoPaths.IsBoundLinkedPath("linked", "", @"C:\approved\model.3dm"));
        Check(!RhinoPaths.IsBoundLinkedPath("linked", @"C:\approved\model.3dm", ""));
        Check(!RhinoPaths.IsBoundLinkedPath("linked", @"relative\model.3dm", @"C:\approved\model.3dm"));
        Check(!RhinoPaths.IsBoundLinkedPath("linked", @"\\.\C:\approved\model.3dm", @"C:\approved\model.3dm"));
        Check(!RhinoPaths.IsBoundLinkedPath("linked", @"C:\approved\model.3dm", @"C:\approved \model.3dm"));
        Check(RhinoPaths.AuxiliaryFailure(new ProtocolFailure("source_not_bound", "missing input")).Code == "invalid_request");
        var actualSourceLoss = new ProtocolFailure("source_mismatch", "bound source changed");
        Check(ReferenceEquals(RhinoPaths.AuxiliaryFailure(actualSourceLoss), actualSourceLoss));
        Check(RhinoPaths.ForNativeApi(@"\\?\C:\approved\model.3dm") == @"C:\approved\model.3dm");
        Check(RhinoPaths.ForNativeApi(@"\\?\UNC\server\share\model.3dm") == @"\\server\share\model.3dm");
        Check(RhinoPaths.ForNativeApi(@"\\server\share\model.3dm") == @"\\server\share\model.3dm");
        foreach (string badPath in new[] { @"\\.\C:\model.3dm", @"\\?\Volume{test}\model.3dm", @"C:\ambiguous.\model.3dm", @"C:\ambiguous \model.3dm", @"relative\model.3dm", @"C:relative\model.3dm", @"\\server\model.3dm" })
        { rejected = false; try { RhinoPaths.ForNativeApi(badPath); } catch (ProtocolFailure) { rejected = true; } Check(rejected); }
        CheckEscapePolicy(Check);
        CheckNativeLoopPolicy(Check);
        CheckGeometryResourceCache(Check);
        CheckDraftClearPolicy(Check);
        CheckWorkflowData(Check);
        CheckLayoutData(Check);
        CheckHostedSurfacePolicy(Check);
        CheckGrasshopperPresentation(Check);
        CheckColdCapturePolicy(Check);
        CheckUiScalePolicy(Check);
        CheckQueuedRecovery(Check);
        NativeCommandPolicy.RequireIdleDraft(0); Check(true);
        foreach (int depth in new[] { 1, 2, 15 })
        {
            bool busyRefused = false;
            try { NativeCommandPolicy.RequireIdleDraft(depth); }
            catch (ProtocolFailure failure) { busyRefused = failure.Code == "invalid_request" && failure.Message.Contains("Finish or cancel"); }
            Check(busyRefused);
        }
        Console.WriteLine("PASS " + assertions + " independent framing/schema/native-path/Escape-policy/native-loop/draft-busy/document-cache/draft-clear checks"); return 0;
    }
    private static void CheckColdCapturePolicy(Action<bool> check)
    {
        var readiness = new NativeCaptureReadiness();
        check(!readiness.Presented);
        void RefusesColdHidden()
        {
            bool ordinaryRefusal = false;
            try { readiness.Require(false); }
            catch (ProtocolFailure error) { ordinaryRefusal = error.Code == "invalid_request"; }
            check(ordinaryRefusal);
        }
        RefusesColdHidden();
        readiness.Require(true); check(true); // Preserve visible capture eligibility.
        RefusesColdHidden(); // Visibility alone does not latch authenticated presentation.
        readiness.ObserveHostedPresentation(false); RefusesColdHidden();
        readiness.ObserveHostedPresentation(true); readiness.Require(false); check(readiness.Presented);
        readiness.ObserveHostedPresentation(false); readiness.Require(false); check(true); // Later parking retains readiness.
    }
    private static void CheckDraftClearPolicy(Action<bool> check)
    {
        check(DraftClearPolicy.CanDelete(false, true)); // Local editable objects may be hidden or locked.
        check(!DraftClearPolicy.CanDelete(true, true));
        check(!DraftClearPolicy.CanDelete(false, false));
        check(!DraftClearPolicy.CanDelete(true, false));
        var first = Guid.NewGuid(); var second = Guid.NewGuid(); var added = Guid.NewGuid();
        var before = new[] { first, second };
        var result = DraftClearPolicy.Result(before, Array.Empty<Guid>(), false);
        check((int)result["removed"]! == 2 && (int)result["remaining"]! == 0 && (bool)result["cleared"]!);
        result = DraftClearPolicy.Result(before, new[] { second, added }, false);
        check((int)result["removed"]! == 1 && (int)result["remaining"]! == 2 && !(bool)result["cleared"]!); // Surviving/new objects are real partial effects.
        result = DraftClearPolicy.Result(before, before, false);
        check((int)result["removed"]! == 0 && (int)result["remaining"]! == 2 && !(bool)result["cleared"]!);
        result = DraftClearPolicy.Result(before, Array.Empty<Guid>(), true);
        check((int)result["removed"]! == 2 && (bool)result["counts_verified"]! && !(bool)result["cleared"]!); // Cleanup failure cannot erase actual effects.
    }
    private static void CheckGeometryResourceCache(Action<bool> check)
    {
        var cache = new GeometryResourceCache();
        cache.Bind(101);
        var first = new GeometryResource(7, Guid.NewGuid());
        cache.Layers["00_SITE"] = first;
        cache.Materials["material"] = first;
        cache.Bind(101);
        check(cache.Layers["00_site"].Id == first.Id); // Rhino layer names ignore case.
        check(cache.Materials["material"].Index == 7); // Same document retains both tables.
        cache.Bind(102);
        check(cache.Layers.Count == 0 && cache.Materials.Count == 0); // A new native serial invalidates both.
        cache.Layers["00_SITE"] = new GeometryResource(7, Guid.NewGuid());
        cache.Materials["material"] = first;
        cache.Bind(101);
        check(cache.Layers.Count == 0 && cache.Materials.Count == 0); // Returning to an older document never resurrects slots.
        bool refused = false;
        try { cache.Bind(0); } catch (ProtocolFailure failure) { refused = failure.Code == "source_not_bound"; }
        check(refused);
    }
    private static void CheckNativeLoopPolicy(Action<bool> check)
    {
        var accepted = new NativeLoopState {
            InitiallyVerified = true, Process = 11, CurrentProcess = 11,
            UiThread = 31, CurrentThread = 31, MainWindowExists = false, OpenDocuments = 0
        };
        check(NativeLoopPolicy.ShouldQuit(accepted)); // Native close has completed; no documents survive.
        var candidate = accepted; candidate.MainWindowExists = true;
        check(!NativeLoopPolicy.ShouldQuit(candidate)); // Hidden, parked, or reused live HWND is not destruction.
        candidate.OpenDocuments = 1;
        check(!NativeLoopPolicy.ShouldQuit(candidate)); // A save prompt/Cancel keeps the native window and document.
        candidate = accepted; candidate.OpenDocuments = 1;
        check(!NativeLoopPolicy.ShouldQuit(candidate)); // Every live document blocks, including clean/headless/dirty.
        candidate = accepted; candidate.OpenDocuments = null;
        check(!NativeLoopPolicy.ShouldQuit(candidate)); // API failure is not zero documents.
        candidate = accepted; candidate.OpenDocuments = -1;
        check(!NativeLoopPolicy.ShouldQuit(candidate));
        candidate = accepted; candidate.InitiallyVerified = false;
        check(!NativeLoopPolicy.ShouldQuit(candidate));
        candidate = accepted; candidate.CurrentProcess = 12;
        check(!NativeLoopPolicy.ShouldQuit(candidate));
        candidate = accepted; candidate.CurrentThread = 32;
        check(!NativeLoopPolicy.ShouldQuit(candidate)); // No worker-thread PostQuitMessage.
        candidate = accepted; candidate.Process = 0; candidate.CurrentProcess = 0;
        check(!NativeLoopPolicy.ShouldQuit(candidate));
        candidate = accepted; candidate.UiThread = 0; candidate.CurrentThread = 0;
        check(!NativeLoopPolicy.ShouldQuit(candidate));
        candidate = accepted; candidate.AlreadyPosted = true;
        check(!NativeLoopPolicy.ShouldQuit(candidate)); // At most one quit request.
    }
    private static void CheckEscapePolicy(Action<bool> check)
    {
        check(System.Runtime.InteropServices.Marshal.SizeOf<Native.GuiThreadInfo>() == 72);
        var idle = new EscapeRelayState {
            Key = 0x1b, Attached = true, Window = new IntPtr(101), WindowProcess = 11, HelperProcess = 11,
            WindowVisible = true, WindowEnabled = true, Host = new IntPtr(201), HostValid = true,
            HostProcess = 22, ExpectedHostProcess = 22, ActiveRoot = new IntPtr(101), Foreground = new IntPtr(101),
            GuiAvailable = true, FocusValid = true, FocusInBody = true,
            Gui = new Native.GuiThreadInfo { Active = new IntPtr(101), Focus = new IntPtr(102) }
        };
        check(EscapeRelayPolicy.ShouldRelay(idle)); // Owned full UI with body focus.
        var candidate = idle; candidate.Gui.Focus = candidate.Window;
        check(EscapeRelayPolicy.ShouldRelay(candidate)); // Main HWND itself can have focus.
        candidate = idle; candidate.ChildWindow = true;
        candidate.ActiveRoot = candidate.Host; candidate.Foreground = candidate.Host; candidate.Gui.Active = candidate.Host;
        check(EscapeRelayPolicy.ShouldRelay(candidate)); // Child viewport under the active Slate root.
        candidate.ChildWindow = false;
        check(!EscapeRelayPolicy.ShouldRelay(candidate)); // Owned UI cannot use Slate's root as its own.
        candidate = idle; candidate.Gui.Flags = 1;
        check(EscapeRelayPolicy.ShouldRelay(candidate)); // A blinking caret is not a modal interaction.
        foreach (uint flag in new uint[] { 2, 4, 8, 16, 0x1e })
        { candidate = idle; candidate.Gui.Flags = flag; check(!EscapeRelayPolicy.ShouldRelay(candidate)); }
        candidate = idle; candidate.Key = 0x41; check(!EscapeRelayPolicy.ShouldRelay(candidate));
        candidate = idle; candidate.CommandDepth = 1; check(!EscapeRelayPolicy.ShouldRelay(candidate));
        candidate = idle; candidate.NativeSelection = true; check(!EscapeRelayPolicy.ShouldRelay(candidate));
        candidate = idle; candidate.Attached = false; check(!EscapeRelayPolicy.ShouldRelay(candidate));
        candidate = idle; candidate.Window = IntPtr.Zero; check(!EscapeRelayPolicy.ShouldRelay(candidate));
        candidate = idle; candidate.WindowProcess = 33; check(!EscapeRelayPolicy.ShouldRelay(candidate));
        candidate = idle; candidate.WindowVisible = false; check(!EscapeRelayPolicy.ShouldRelay(candidate));
        candidate = idle; candidate.WindowEnabled = false; check(!EscapeRelayPolicy.ShouldRelay(candidate));
        candidate = idle; candidate.HostValid = false; check(!EscapeRelayPolicy.ShouldRelay(candidate));
        candidate = idle; candidate.Host = IntPtr.Zero; check(!EscapeRelayPolicy.ShouldRelay(candidate));
        candidate = idle; candidate.OriginalHost = candidate.Host; check(!EscapeRelayPolicy.ShouldRelay(candidate));
        candidate = idle; candidate.HostProcess = 33; check(!EscapeRelayPolicy.ShouldRelay(candidate));
        candidate = idle; candidate.ExpectedHostProcess = 0; check(!EscapeRelayPolicy.ShouldRelay(candidate));
        candidate = idle; candidate.GuiAvailable = false; check(!EscapeRelayPolicy.ShouldRelay(candidate));
        candidate = idle; candidate.Foreground = new IntPtr(301); check(!EscapeRelayPolicy.ShouldRelay(candidate));
        candidate = idle; candidate.Gui.Active = new IntPtr(301); check(!EscapeRelayPolicy.ShouldRelay(candidate));
        candidate = idle; candidate.FocusInBody = false; check(!EscapeRelayPolicy.ShouldRelay(candidate));
        candidate = idle; candidate.FocusValid = false; check(!EscapeRelayPolicy.ShouldRelay(candidate));
        candidate = idle; candidate.Gui.Focus = IntPtr.Zero; check(!EscapeRelayPolicy.ShouldRelay(candidate));
        candidate = idle; candidate.Gui.MenuOwner = candidate.Window; check(!EscapeRelayPolicy.ShouldRelay(candidate));
        candidate = idle; candidate.Gui.MoveSize = candidate.Window; check(!EscapeRelayPolicy.ShouldRelay(candidate));
        candidate = idle; candidate.Gui.Capture = candidate.Window; check(!EscapeRelayPolicy.ShouldRelay(candidate));
    }
    private static JObject ValidParameters(string id)
    {
        if (id.EndsWith("draft_layers_from_model")) return new JObject { ["source_relative"] = "fixtures/layers-source.3dm" };
        if (id.EndsWith("draft_layers_from_csv")) return new JObject { ["csv"] = "path,red,green,blue,visible,locked\nMODEL,120,130,140,true,false" };
        if (id.EndsWith("draft_named_view_add")) return new JObject { ["name"] = "Test perspective" };
        if (id.EndsWith("capture_named_views")) return new JObject { ["people_layers"] = "[\"PEOPLE\"]" };
        if (id.EndsWith("draft_building_set")) return BuildingFixture();
        if (id.EndsWith("draft_building_floors")) return new JObject { ["building_id"] = "B01", ["floors"] = 4 };
        if (id.EndsWith("draft_layout_template")) return LayoutFixture();
        if (id.EndsWith("ui_scale")) return new JObject { ["scale"] = 0.8 };
        if (id.EndsWith("draft_add_box")) return new JObject { ["x"]=0,["y"]=0,["z"]=0,["width"]=1,["depth"]=1,["height"]=1,["layer"]="test",["red"]=0,["green"]=0,["blue"]=0 };
        if (id.EndsWith("draft_add_curve")) return new JObject { ["points"]="[[0,0,0],[1,1,1]]",["closed"]=false,["layer"]="test",["red"]=0,["green"]=0,["blue"]=0 };
        if (id.EndsWith("draft_add_mesh")) return new JObject { ["vertices"]="[[0,0,0],[1,0,0],[0,1,0]]",["faces"]="[[0,1,2]]",["layer"]="test",["red"]=0,["green"]=0,["blue"]=0 };
        if (id.EndsWith("set_camera")) return new JObject { ["x"]=0,["y"]=0,["z"]=1,["target_x"]=0,["target_y"]=0,["target_z"]=0,["projection"]="perspective" };
        return new JObject();
    }
}

