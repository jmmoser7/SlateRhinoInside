using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SlateRhinoInside;
internal static partial class SelfTest
{
    private static JObject LayoutFixture() => new JObject { ["name"] = "A3 reference", ["page_width_mm"] = 420,
        ["page_height_mm"] = 297, ["details"] = "[{\"title\":\"PLAN\",\"x\":20,\"y\":24,\"width\":182,\"height\":104,\"projection\":\"top\"}]" };
    private static void CheckLayoutData(Action<bool> check)
    {
        var spec = LayoutTemplateSpec.Parse(LayoutFixture());
        check(spec.Width == 420 && spec.Height == 297 && spec.Details.Count == 1);
        check(spec.Details[0].Y == 24 && spec.Details[0].Projection == "top");
        foreach (var change in new[] { ("name", (JToken)""), ("page_width_mm", (JToken)49), ("page_height_mm", (JToken)2001),
            ("details", (JToken)"[]"), ("details", (JToken)"[{\"title\":\"bad\"}]") })
        {
            var parameters = LayoutFixture(); parameters[change.Item1] = change.Item2;
            bool refused = false; try { LayoutTemplateSpec.Parse(parameters); } catch { refused = true; }
            check(refused);
        }
        foreach (var change in new[] { ("x", (JToken)(-1)), ("width", (JToken)500), ("height", (JToken)4),
            ("projection", (JToken)"script"), ("title", (JToken)"\n"), ("x", (JToken)"20") })
        {
            var parameters = LayoutFixture(); var details = JArray.Parse((string)parameters["details"]!);
            details[0]![change.Item1] = change.Item2; parameters["details"] = details.ToString();
            bool refused = false; try { LayoutTemplateSpec.Parse(parameters); } catch (ProtocolFailure) { refused = true; }
            check(refused);
        }
        var duplicate = LayoutFixture(); var rows = JArray.Parse((string)duplicate["details"]!);
        rows.Add(rows[0]!.DeepClone()); duplicate["details"] = rows.ToString();
        bool duplicateRefused = false; try { LayoutTemplateSpec.Parse(duplicate); } catch (ProtocolFailure) { duplicateRefused = true; }
        check(duplicateRefused);
        var artwork = LayoutFixture();
        artwork["annotations"] = "[{\"kind\":\"rect\",\"x\":15,\"y\":15,\"width\":390,\"height\":267},{\"kind\":\"text\",\"x\":20,\"y\":20,\"text\":\"PLAN\",\"height\":3},{\"kind\":\"line\",\"x\":20,\"y\":250,\"x2\":400,\"y2\":250}]";
        var parsed = LayoutTemplateSpec.Parse(artwork);
        check(parsed.Annotations.Count == 3 && parsed.Annotations[1].Text == "PLAN");
        foreach (string invalid in new[] {
            "[{\"kind\":\"script\",\"x\":0,\"y\":0}]",
            "[{\"kind\":\"rect\",\"x\":15,\"y\":15,\"width\":410,\"height\":100}]",
            "[{\"kind\":\"rect\",\"x\":15,\"y\":15,\"width\":0,\"height\":100}]",
            "[{\"kind\":\"line\",\"x\":0,\"y\":0,\"x2\":0,\"y2\":0}]",
            "[{\"kind\":\"line\",\"x\":0,\"y\":0,\"x2\":421,\"y2\":0}]",
            "[{\"kind\":\"text\",\"x\":0,\"y\":0,\"text\":\"A\",\"height\":21}]",
            "[{\"kind\":\"text\",\"x\":0,\"y\":296,\"text\":\"A\",\"height\":3}]",
            "[{\"kind\":\"text\",\"x\":-1,\"y\":0,\"text\":\"A\",\"height\":3}]",
            "[{\"kind\":\"text\",\"x\":0,\"y\":0,\"text\":\"A\",\"height\":3,\"script\":\"no\"}]" })
        {
            artwork["annotations"] = invalid;
            bool refused = false; try { LayoutTemplateSpec.Parse(artwork); } catch { refused = true; }
            check(refused);
        }
        artwork["annotations"] = new JArray(System.Linq.Enumerable.Range(0, 33).Select(_ => new JObject {
            ["kind"] = "text", ["x"] = 20, ["y"] = 20, ["text"] = "A", ["height"] = 3 })).ToString();
        bool overCount = false; try { LayoutTemplateSpec.Parse(artwork); } catch { overCount = true; } check(overCount);
        artwork["annotations"] = new string(' ', 4097);
        bool overLength = false; try { LayoutTemplateSpec.Parse(artwork); } catch { overLength = true; } check(overLength);
        foreach (var invalidText in new[] { new string('A', 257), "A\nB", "%<field", "field>%" })
        {
            artwork["annotations"] = new JArray(new JObject { ["kind"] = "text", ["x"] = 20, ["y"] = 20,
                ["text"] = invalidText, ["height"] = 3 }).ToString();
            bool refused = false; try { LayoutTemplateSpec.Parse(artwork); } catch { refused = true; } check(refused);
        }
    }
}
