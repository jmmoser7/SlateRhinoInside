# Slate Rhino.Inside

A standalone Windows x64 connector that opens licensed Rhino 8 inside Slate model portals. Rhino runs in its own process through the official Rhino.Inside resolver and RhinoCore.

## Requirements and installation

- Windows x64, licensed Rhino 8, and .NET Framework 4.8.
- Slate with the Rhino application-connector expansion pack. The suite 1.0.3 release candidate contains the integration.
- Build this source, or extract the separately supplied helper package with all four runtime files intact.

Register only the model folders you intend to use:

```powershell
.\Register-SlateRhino.ps1 -SourceRoot 'C:\path\to\models'
```

The default executable is `bin/Release/net48/SlateRhinoInside.exe`. Use `-Helper` to choose another output and `-RhinoPath` for a different Rhino installation. `-PrintOnly` validates without writing registration. Registration is per-user and backs up an existing entry.

In Slate, refresh **Advanced → Connections**, load a local `.3dm` through the normal 3D portal, and activate the Rhino blister. **Slate 3D** explicitly returns to the standard portal. Maximize and capture stay in Slate's surrounding menu. Use **Open isolated Rhino copy** for native edits while preserving the source file. Each model has its own native session.

The Robot menu's **Agent · chat train** creates a source-linked chat. Select its provider/project and enable the intended scoped board access. Connect reference images to the chat's input. Native agent operations use the advertised `board.model.connector` tools and the helper's fixed command descriptors.

## Build and pure checks

Requires a .NET SDK that can compile net48 and installed Rhino 8 reference assemblies. The default RhinoSystemDirectory is the installed Rhino 8 System folder. The project restores Rhino.Inside 8.0.7-beta and Newtonsoft.Json 13.0.3.

```powershell
dotnet build SlateRhinoInside.csproj -c Release
dotnet run --project SlateRhinoInside.csproj -c Release -- --self-test
```

Override `-p:RhinoSystemDirectory=...` when building for another installation. RhinoCommon and the Rhino/Grasshopper installation are not redistribution payloads.

The v10f candidate's helper SHA256 is `990A3154CE97F2C92C8D7C2E6CB5670BBA73D11A900233E6E235727A28BF89C9`; its Release build and 296 pure checks pass. The runtime package includes its executable, configuration, Rhino.Inside loader, Newtonsoft.Json, registration script, installation guide, checksums and upstream notices.

## Workflow and validation scope

The fixed native command surface covers layer hierarchy/settings, CSV layers, named views, metadata-bearing capture/camera recall, native page layouts, and typed building controls. Real saved-file checks cover agent-driven layer/CSV edits, image-derived native layouts, and floor-count changes. The [masterplan example](examples/masterplan/README.md) demonstrates the reusable declarative `slate-control` bridge with hover sliders.

Fresh-session original-PNG board dragging has visibly restored the camera with matching source identity, target, display mode and units. Exact pose/lens and pixel equality across changed viewport dimensions remain unverified. Preserve the original PNG file and its metadata.

Native toolbar images and command-prompt size follow portal enlargement and return after settling. Menu, panel, dialog and Grasshopper fonts retain their own native sizing. Physical right-mouse navigation parity remains a separate runtime check. The helper preserves Slate ownership when showing an existing Grasshopper editor and uses managed Hide rather than the plugin Close path.

The matching Slate CI source `6ee75` (executable SHA256 `AB183D307FE3DDBC872CF473A84503DCCA58EF84E75F38D09ACF987F5F405DBC`) and v10f helper passed a bounded NoDoc GUI lifecycle test on 2026-10-27: two blank-park/double-click-focus cycles, deletion of the child card and leader, footer reopening, then three more park/focus cycles. Each settled parked state was clean and each focus returned the real editor; no large black or floating native-window residue was observed in those cycles. The saved intermediate workbook contained only the parent, the helper stayed alive, and the final workbook contained the parent, reopened child and leader. This does not certify general stability or GH definition, solver, graph or same-HWND retention; no GH definition was intentionally opened or authored. Third-party components may display their own loading errors; the test dismissed one Cytoskeleton error dialog normally without changing plugin or security settings.

The live receipt `ci-6ee75-gh-live-acceptance.json` has SHA256 `0B75C384490C3083041B3A1BB6505DD30CFA19ABE2A106B88C29E1F80384CC69`. Both the original 5,738-byte model and its isolated copy retained SHA256 `CE2547DC013F7B4891093B5C65EA9D4CC6EF0C4751472FD7A2F985FBD54F15F5`; this lifecycle test issued no model mutations.

Native protocol receipts have a finite session budget. Unresolved Grasshopper discovery is cached per worker rather than continuously polling. Indeterminate commands are retained; they are not automatically replayed. Parent-exit recovery queues document-preserving native UI restoration independently of a pending connector call; a blocked third-party UI thread can still delay restoration.

A bounded local session on the installed NVIDIA GTX 1660 Ti completed without a native fault. Earlier access violations in the Intel OpenGL module remain under investigation. Choosing a per-application GPU is a local comparison, not a proven crash fix.

## Source and dependencies

This repository contains connector source and a portable example. Licensed Rhino binaries, models, workbooks, architectural media, private agent state and machine registrations remain outside it. Dependency licenses are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and `licenses/`. No additional license grant for this project's own source is declared here.
