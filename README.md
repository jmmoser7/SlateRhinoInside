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

The v10e candidate's helper SHA256 is `1BC58650F53583C9E8BD084F92D157F12AA96B9EBBF131EB73D8EA63643084E0`; its Release build and 271 pure checks pass. The runtime package includes its executable, configuration, Rhino.Inside loader, Newtonsoft.Json, registration script, installation guide, checksums and upstream notices.

## Workflow and validation scope

The fixed native command surface covers layer hierarchy/settings, CSV layers, named views, metadata-bearing capture/camera recall, native page layouts, and typed building controls. Real saved-file checks cover agent-driven layer/CSV edits, image-derived native layouts, and floor-count changes. The [masterplan example](examples/masterplan/README.md) demonstrates the reusable declarative `slate-control` bridge with hover sliders.

Fresh-session original-PNG board dragging has visibly restored the camera with matching source identity, target, display mode and units. Exact pose/lens and pixel equality across changed viewport dimensions remain unverified. Preserve the original PNG file and its metadata.

Native toolbar images and command-prompt size follow portal enlargement and return after settling. Menu, panel, dialog and Grasshopper fonts retain their own native sizing. Physical right-mouse navigation parity and Grasshopper companion attach/tuck/reopen remain separate runtime checks. Third-party Grasshopper components may display their own load errors.

Native protocol receipts have a finite session budget. Unresolved Grasshopper discovery is cached per worker rather than continuously polling. Indeterminate commands are retained; they are not automatically replayed. Parent-exit recovery queues document-preserving native UI restoration independently of a pending connector call; a blocked third-party UI thread can still delay restoration.

## Source and dependencies

This repository contains connector source and a portable example. Licensed Rhino binaries, models, workbooks, architectural media, private agent state and machine registrations remain outside it. Dependency licenses are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and `licenses/`. No additional license grant for this project's own source is declared here.
