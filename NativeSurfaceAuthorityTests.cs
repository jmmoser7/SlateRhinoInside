using System;

namespace SlateRhinoInside;
internal static partial class SelfTest
{
    private static void CheckHostedSurfacePolicy(Action<bool> check)
    {
        IntPtr window = new IntPtr(100), host = new IntPtr(200);
        check(NativeSurfaceAuthority.MatchesOwner(window, 10, 10, host, IntPtr.Zero, true, 20, 20));
        check(!NativeSurfaceAuthority.MatchesOwner(window, 11, 10, host, IntPtr.Zero, true, 20, 20));
        check(!NativeSurfaceAuthority.MatchesOwner(window, 10, 10, IntPtr.Zero, IntPtr.Zero, false, 0, 20));
        check(!NativeSurfaceAuthority.MatchesOwner(window, 10, 10, host, host, true, 20, 20));
        check(!NativeSurfaceAuthority.MatchesOwner(window, 10, 10, host, IntPtr.Zero, true, 21, 20));
        check(!NativeSurfaceAuthority.MatchesOwner(window, 10, 10, host, IntPtr.Zero, true, 20, 0));
        check(NativeSurfaceAuthority.Allows(true, true, true, true, true, true, true));
        check(!NativeSurfaceAuthority.Allows(false, true, true, true, true, true, false));
        check(!NativeSurfaceAuthority.Allows(true, false, true, true, true, true, false));
        check(!NativeSurfaceAuthority.Allows(true, true, true, true, false, true, true));
        check(!NativeSurfaceAuthority.Allows(true, true, false, true, true, true, true));
        check(!NativeSurfaceAuthority.Allows(true, true, true, false, true, true, true));
        check(!NativeSurfaceAuthority.Allows(true, true, false, true, false, true, false));
        check(NativeSurfaceAuthority.Allows(true, true, false, true, true, true, false));
        check(!NativeSurfaceAuthority.Allows(true, true, true, true, true, false, true)); // Alt-Tab before the next host frame.
        check(!NativeSurfaceAuthority.Allows(true, true, false, true, true, false, false));
        var hosted = new NativeSurfaceHostState { OwnerMatches = true, OwnUiThread = true, Visible = true,
            Enabled = true, ClipHasArea = true, ForegroundEligible = true };
        check(NativeSurfaceAuthority.CanRaise(hosted));
        for (int boundary = 0; boundary < 6; boundary++)
        {
            var denied = hosted;
            switch (boundary)
            {
                case 0: denied.OwnerMatches = false; break;
                case 1: denied.OwnUiThread = false; break;
                case 2: denied.Visible = false; break;
                case 3: denied.Enabled = false; break;
                case 4: denied.ClipHasArea = false; break;
                case 5: denied.ForegroundEligible = false; break;
            }
            check(!NativeSurfaceAuthority.CanRaise(denied));
        }
        check(NativeSurfaceAuthority.RaiseFlags == 0x0013); // Geometry and activation stay with their existing owners.
        check((NativeSurfaceAuthority.RaiseFlags & (0x0040u | 0x0080u)) == 0); // Raising never shows or hides a parked surface.
        check((NativeSurfaceAuthority.RaiseFlags & (0x0004u | 0x0200u)) == 0); // No extra Z-order policy flags.
    }
}
