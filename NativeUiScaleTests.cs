using System;
using System.Drawing;

namespace SlateRhinoInside;
internal static partial class SelfTest
{
    private static void CheckUiScalePolicy(Action<bool> check)
    {
        string scheme = NativeUiScalePolicy.SchemeName(Guid.Parse("9b302570-07d6-4da0-a35c-d4dc287ca324"));
        bool Allows(string expected, string actual, uint process = 7, uint helper = 7, uint thread = 11, uint current = 11, uint initial = 11) =>
            NativeUiScalePolicy.Allows(expected, actual, process, helper, thread, current, initial);
        check(Allows(scheme, scheme));
        check(!Allows(scheme, "Default"));
        check(!Allows("Default", "Default"));
        check(!Allows(scheme, scheme.ToUpperInvariant()));
        check(!Allows(NativeUiScalePolicy.SchemeName(Guid.Empty), NativeUiScalePolicy.SchemeName(Guid.Empty)));
        check(!Allows(scheme, NativeUiScalePolicy.SchemeName(Guid.NewGuid())));
        check(!Allows(scheme, scheme, process: 8));
        check(!Allows(scheme, scheme, process: 0, helper: 0));
        check(!Allows(scheme, scheme, current: 12));
        check(!Allows(scheme, scheme, initial: 12));
        check(!Allows(scheme, scheme, thread: 0, current: 0, initial: 0));
        check(NativeUiScalePolicy.SchemeMatches(scheme, scheme)
            && !NativeUiScalePolicy.ThreadMatches(7, 7, 11, 12, 11)); // Exact scheme, wrong dispatch thread.
        check(!NativeUiScalePolicy.SchemeMatches(scheme, "Default")
            && NativeUiScalePolicy.ThreadMatches(7, 7, 11, 11, 11)); // Correct thread, wrong scheme.
        check(!NativeUiScalePolicy.ThreadMatches(8, 7, 11, 11, 11)); // Same thread numbers do not prove own process.
        check(Allows(scheme, "Scheme: " + scheme)); // Installed SDK's exact Registry-scheme representation.
        check(!Allows(scheme, "Scheme: Scheme: " + scheme));
        check(!Allows(scheme, "scheme: " + scheme));
        check(!Allows(scheme, "Scheme: " + scheme + " "));
        check(!Allows(scheme, " Scheme: " + scheme));
        check(!Allows(scheme, "Scheme: " + NativeUiScalePolicy.SchemeName(Guid.NewGuid())));
        check(!Allows("Scheme: " + scheme, "Scheme: " + scheme));
        check(!Allows("Default", "Scheme: Default"));
        check(!Allows(NativeUiScalePolicy.SchemeName(Guid.Empty), "Scheme: " + NativeUiScalePolicy.SchemeName(Guid.Empty)));

        var policy = new NativeUiScalePolicy();
        check(!policy.Observe(Size.Empty, out _, out _));
        check(!policy.Observe(new Size(100, 80), out _, out _));
        check(policy.Observe(new Size(1000, 800), out double factor, out bool first) && first && factor == 1);
        check(policy.Baseline == new Size(1000, 800));
        check(!policy.Observe(new Size(1000, 800), out _, out _));
        check(!policy.Observe(new Size(1500, 1200), out _, out _));
        check(policy.Observe(new Size(1500, 1200), out factor, out first) && !first && factor == 1.5);
        check(!policy.Observe(new Size(2000, 1600), out _, out _));
        check(policy.Observe(new Size(2000, 1600), out factor, out _) && factor == 2);
        check(!policy.Observe(new Size(5000, 4000), out _, out _));
        check(!policy.Observe(new Size(5000, 4000), out _, out _)); // Bounded at 2.
        check(!policy.Observe(new Size(250, 200), out _, out _));
        check(policy.Observe(new Size(250, 200), out factor, out _) && factor == 0.5);
        check(!policy.Observe(new Size(1500, 800), out _, out _));
        check(policy.Observe(new Size(1500, 800), out factor, out _) && factor == 1); // Fit both dimensions.
        check(!policy.Observe(new Size(1000, 800), out _, out _));
        check(!policy.Observe(new Size(1000, 800), out _, out _)); // Original baseline, no drift.
        policy.ResetPending();
        check(!policy.Observe(new Size(1500, 1200), out _, out _));
        check(policy.Observe(new Size(1500, 1200), out factor, out _) && factor == 1.5);
        check(NativeUiScalePolicy.ScaleSize(new Size(24, 24), factor) == new Size(36, 36));
        check(NativeUiScalePolicy.Dimension(12, factor, 6, 48) == 18);
        check(NativeUiScalePolicy.Dimension(24, 0.5, 8, 256) == 12);
        check(NativeUiScalePolicy.Dimension(1, 0.5, 8, 256) == 8);
        check(NativeUiScalePolicy.Dimension(200, 2, 8, 256) == 256);
        check(NativeUiScalePolicy.PromptFontSize(90, 1) == 90);
        check(NativeUiScalePolicy.PromptFontSize(90, 1.5) == 135);
        check(NativeUiScalePolicy.PromptFontSize(90, 2) == 180);
        check(NativeUiScalePolicy.PromptFontSize(90, 0.5) == 45);
        check(NativeUiScalePolicy.PromptFontSize(int.MaxValue, 2) == int.MaxValue);
        check(NativeUiScalePolicy.PromptFontSize(0, 2) == 0); // Preserve native default sentinel.
        foreach (double invalid in new[] { double.NaN, double.NegativeInfinity, double.PositiveInfinity, 0.49, 2.01 })
        {
            bool refused = false;
            try { NativeUiScalePolicy.PromptFontSize(90, invalid); }
            catch (ArgumentOutOfRangeException) { refused = true; }
            check(refused);
        }
        policy.Restored();
        check(policy.Factor == 1 && policy.Baseline == new Size(1000, 800));
        check(!policy.Observe(new Size(1500, 1200), out _, out _));
        check(policy.Observe(new Size(1500, 1200), out factor, out _) && factor == 1.5);
    }
}
