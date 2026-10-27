# Third-party notices

The helper candidate redistributes only these two dependency assemblies:

| Assembly | Package | License |
| --- | --- | --- |
| `Rhino.Inside.dll` | Rhino.Inside 8.0.7-beta | MIT; copyright 2025 Robert McNeel & Associates |
| `Newtonsoft.Json.dll` | Newtonsoft.Json 13.0.3 | MIT; copyright 2007 James Newton-King |

The corresponding license texts are in `licenses/`. Rhino.Inside's license is
from its [pinned package source commit](https://github.com/mcneel/Rhino.Inside/blob/b26264c359eeb9f7862b51f666fb50cd49e5466a/LICENSE).
Json.NET's license is copied unchanged from the installed NuGet package.

Rhino.Inside is an assembly resolver. Its MIT license does not provide a Rhino
license or permission to redistribute the Rhino installation. RhinoCommon,
Rhino.exe, Grasshopper and Rhino installation assets are excluded. A separately
installed, licensed Rhino 8 for Windows is required.

This notice does not grant a new license to the helper's own source. Candidate
staging is separate from the owner's final release and licensing decision.
