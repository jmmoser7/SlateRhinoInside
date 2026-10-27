using System;

namespace SlateRhinoInside;
internal static partial class SelfTest
{
    private static void CheckGrasshopperPresentation(Action<bool> check)
    {
        var hosted = new NativeSurfaceHostState { OwnerMatches = true, OwnUiThread = true,
            Enabled = true, ClipHasArea = true, ForegroundEligible = true };
        int shows = 0, hides = 0, focuses = 0;
        var state = hosted;
        bool Run(Action reveal) => GrasshopperPresentation.TryShow(() => state,
            () => { shows++; reveal(); }, () => hides++, () => focuses++);
        check(Run(() => state.Visible = true));
        check(shows == 1 && hides == 0 && focuses == 1);
        for (int boundary = 0; boundary < 5; boundary++)
        {
            state = hosted; shows = hides = focuses = 0;
            void Revoke()
            {
                switch (boundary)
                {
                    case 0: state.OwnerMatches = false; break;
                    case 1: state.OwnUiThread = false; break;
                    case 2: state.Enabled = false; break;
                    case 3: state.ClipHasArea = false; break;
                    case 4: state.ForegroundEligible = false; break;
                }
            }
            Revoke();
            check(!Run(() => state.Visible = true));
            check(shows == 0 && hides == 1 && focuses == 0);
            state = hosted; shows = hides = focuses = 0;
            check(!Run(() => { state.Visible = true; Revoke(); }));
            check(shows == 1 && hides == 1 && focuses == 0); // Reentrant Show cannot retain a lost owner/clip/focus grant.
        }
        state = hosted; shows = hides = focuses = 0;
        check(!Run(() => { }));
        check(shows == 1 && hides == 1 && focuses == 0); // A hidden result never claims successful presentation.
        state = hosted; shows = hides = focuses = 0;
        bool failed = false;
        try { Run(() => throw new InvalidOperationException("native show failed")); }
        catch (InvalidOperationException) { failed = true; }
        check(failed && hides == 1 && focuses == 0);
    }
}
