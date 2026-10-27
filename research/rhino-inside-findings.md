# Rhino.Inside evidence and integration decisions

Inspected 25 October 2026. This project uses McNeel's actual Rhino.Inside assembly resolver and RhinoCore, never a simulated viewport. Rhino SDK binaries remain outside Slate and the helper.

## Supported API facts

- Rhino.Inside loads Rhino/Grasshopper into another64-bit process. Windows native UI is the reference target. Official project: https://github.com/mcneel/rhino.inside and product page https://www.rhino3d.com/features/rhino-inside/.
- RhinoCore has `(string[] args, WindowStyle windowStyle, IntPtr hostWnd)`; `hostWnd` means the host main window, not a documented promise of child-window embedding. `Normal` creates the full application UI. Official constructor: https://mcneel.github.io/rhinocommon-api-docs/api/RhinoCommon/html/M_Rhino_Runtime_InProcess_RhinoCore__ctor_3.htm.
- The sample RhinoWindows ViewportControl is a read-only model view, without native selection and editing. McNeel staff explicitly recommend showing Rhino's main window for editing: https://discourse.mcneel.com/t/rhino-inside-windows-form-or-wpf-window/113816/2.
- Full native main-window parenting therefore uses Slate's existing NativeSurface guard, with companion-window fallback. This is Win32 integration requiring native acceptance testing, not a separate Rhino API guarantee. Reparenting individual RhinoView windows can corrupt display teardown; a reported case remains evidence to avoid that approach: https://discourse.mcneel.com/t/embeddingrhinoview-in-wpf-problem/212636.
- RhinoCore is intended to have one instance per process. Multiple loaded cores in threads are not supported: https://discourse.mcneel.com/t/running-multiple-processes-with-rhino-inside/126655/7. Each helper owns one model/full UI process; Slate can keep several helpers resident and show one surface at a time.
- Windows RhinoDoc.Open closes/replaces the active document. OpenHeadless permits separate noninteractive docs but does not give multiple independent editable full native UIs. Official class: https://mcneel.github.io/rhinocommon-api-docs/api/RhinoCommon/html/T_Rhino_RhinoDoc.htm.

## Runtime and licensing

- Actual machine: Rhino8.36.26293.11001, SDK6.0.400, installed .NETFramework4.8 reference assemblies, .NET10 runtime. A net48x64 helper avoids installing another SDK. Its official Rhino.Inside8.0.7-beta package supports net48 and modern .NET: https://www.nuget.org/packages/Rhino.Inside/8.0.7-beta. The official modern example targets net8.0-windows8.0: https://github.com/mcneel/rhino-developer-samples/tree/8/rhino.inside/dotnet-netcore/HelloWorld.
- The official resolver is initialized before JIT reaches RhinoCommon-referencing code. An explicit installed Rhino8 path prevents selecting an unrelated WIP installation. Resolver source: https://github.com/mcneel/rhino.inside/blob/rhino-8.x/src/RhinoInside/Resolver.cs.
- Every user requires an installed licensed Rhino. Resolver source is MIT; that does not redistribute or license Rhino itself: https://discourse.mcneel.com/t/explanation-of-licensing/126175/2.
- UI startup alone does not prove licensing/saving. Initial probe printed IsLicenseValidated=false and CanSave=true before its message loop; actual native save validation remains the acceptance gate.
- A recent user report connects .NETCore resolver System.Management assembly selection to device-ID/license conflicts alongside another Rhino. We use net48, which that report says did not reproduce the issue; the report is not a universal guarantee: https://discourse.mcneel.com/t/rhino-inside-voids-the-license-of-my-open-rhino-cause-workaround/223361.

## Camera and captures

The namespaced `org.slate.rhino.view` v1 schema carries RhinoCommon ViewportInfo JSON (camera/frustum), target, display-mode ID and units. It restores only that explicitly declared scope. Layer state, clipping-plane geometry and renderer convergence are not promised by this schema.

- `new ViewportInfo(RhinoViewport)` copies native camera/frustum: https://mcneel.github.io/rhinocommon-api-docs/api/RhinoCommon/html/T_Rhino_DocObjects_ViewportInfo.htm.
- CommonObject.ToJSON/FromJSON preserve that native object: https://mcneel.github.io/rhinocommon-api-docs/api/RhinoCommon/html/Methods_T_Rhino_Runtime_CommonObject.htm.
- `RhinoViewport.SetViewProjection(ViewportInfo,false)` restores projection; target and display mode are restored separately: https://mcneel.github.io/rhinocommon-api-docs/api/RhinoCommon/html/M_Rhino_Display_RhinoViewport_SetViewProjection.htm.
- ViewCapture.CaptureToBitmap captures actual Rhino pixels, without screen UI overlays. Captures retain source/revision, view snapshot, UTC timestamp, dimensions and consistency. Both snapshot and pixels run in one UI dispatch. Pixel and immutable metadata transfer remains Slate-owned: https://mcneel.github.io/rhinocommon-api-docs/api/RhinoCommon/html/T_Rhino_Display_ViewCapture.htm.
- UI calls marshal through RhinoApp.InvokeOnUiThread; no pipe IO occurs on Rhino's UI thread: https://mcneel.github.io/rhinocommon-api-docs/api/RhinoCommon/html/M_Rhino_RhinoApp_InvokeOnUiThread.htm.

## Integration boundary

Slate owns atlas-connector protocol/version policy, authenticated local launch, program packs, portal chrome, fullscreen geometry, board capture publication, metadata drag restore and agent grants. This sibling helper owns Rhino SDK translation, UI/document lifecycle, bounded geometry operations, native undo, and capture pixels. Protocols0.2 and0.1 are offered; agent mutations require0.2, an isolated native draft, explicit workspace autonomy, and the host's scoped agent/document/command grant. No command accepts scripts or writes a linked original.

Known behavior: each successful draft command advances its opaque bound revision. Existing models bind in isolated mode by copying into `.slate-rhino-drafts` under the approved root; new paths produce new documents. The draft save command cannot target an arbitrary path. Disconnect/detach preserves unsaved native documents and restores foreign window parenting.

Save-path fix, native acceptance follow-up25October2026:
The first real154-object draft run reached Rhino but WriteFile failed for an approved extended-prefix target (`\\?\C:\...`). The live dirty process and failed-save evidence are preserved. Root/reparse validation remains unchanged; the final authorized native API path now converts extended DOS/UNC syntax to ordinary DOS/UNC. Device namespaces and trailing-dot/space names are refused so normalization cannot silently select another file. Native save options suppress dialogs/input, include render meshes/preview/history/user data, select Rhino8format and update the document path only on successful save. A versioned `bin/Release/save-path-fix` output preserves the already loaded helper binary and its live dirty document. Native retry evidence is required before claiming this resolves the failure.
