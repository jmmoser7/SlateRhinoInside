using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SlateRhinoInside;
internal static partial class SelfTest
{
    private static JObject BuildingFixture() => new JObject { ["building_id"] = "B01", ["layer"] = "BUILDINGS::B01",
        ["footprint"] = "[[0,0],[20,0],[20,15],[0,15]]", ["floors"] = 8, ["floor_height"] = 3.6,
        ["red"] = 171, ["green"] = 190, ["blue"] = 204 };
    private static void CheckWorkflowData(Action<bool> check)
    {
        const string header = "path,red,green,blue,visible,locked\n";
        var layers = LayerSpec.FromCsv(header + "PROJECT,120,130,140,true,false\n\"PROJECT::People, slow\",10,20,30,0,1\r\n");
        check(layers.Count == 2 && layers[1].Path == "PROJECT::People, slow");
        check(layers[1].Parent == "PROJECT" && !layers[1].Visible && layers[1].Locked);
        foreach (string invalid in new[] {
            "wrong,header", header + "A,1,2,3,true,false\na,4,5,6,true,false", header + "A,-1,2,3,true,false",
            header + "A,1.5,2,3,true,false", header + "A,1,2,3,yes,false", header + "\"A,1,2,3,true,false",
            header + "A,1,2,3,true,false,extra", header + "A::,1,2,3,true,false",
            header + string.Join("::", Enumerable.Repeat("deep", 17)) + ",1,2,3,true,false" })
        {
            bool refused = false; try { LayerSpec.FromCsv(invalid); } catch (ProtocolFailure) { refused = true; }
            check(refused);
        }
        var building = BuildingSpec.Parse(BuildingFixture());
        check(Math.Abs(building.Height - 28.8) < 0.000001 && building.Footprint.Length == 4);
        var roundtrip = BuildingSpec.Parse(building.Parameters());
        check(roundtrip.Footprint.Length == 4 && roundtrip.Footprint[2][0] == 20 && roundtrip.Footprint[2][1] == 15);
        foreach (var change in new[] { ("floors", (JToken)81), ("floor_height", (JToken)0), ("red", (JToken)256),
            ("building_id", (JToken)"../other"), ("footprint", (JToken)"[[0,0],[1,1],[2,2]]"),
            ("footprint", (JToken)"[[0,0],[1,1]]"), ("footprint", (JToken)"[[0,0],[1,0],[1,\"x\"]]") })
        {
            var input = BuildingFixture(); input[change.Item1] = change.Item2;
            bool refused = false; try { BuildingSpec.Parse(input); } catch (ProtocolFailure) { refused = true; }
            check(refused);
        }
        check(NativeUiScalePolicy.Dimension(20, 0.8, 8, 256) == 16);
        check(NativeUiScalePolicy.Dimension(30, 2, 8, 256) == 60);
        check(NativeUiScalePolicy.Dimension(0, 0.8, 8, 256) == 0);
        check(NativeUiScalePolicy.Dimension(4, 0.5, 8, 256) == 8);
        var csvCommand = Contract.Commands().OfType<JObject>().Single(c => (string?)c["id"] == "org.slate.rhino.draft_layers_from_csv");
        bool tooLong = false;
        try { Contract.ValidateParameters(csvCommand, new JObject { ["csv"] = new string('x', 4097) }); }
        catch (ProtocolFailure) { tooLong = true; }
        check(tooLong);
        check(NativeWindowRecovery.CanRecover(true, 11, 11, 22, 22));
        check(!NativeWindowRecovery.CanRecover(false, 11, 11, 22, 22));
        check(!NativeWindowRecovery.CanRecover(true, 11, 12, 22, 22));
        check(!NativeWindowRecovery.CanRecover(true, 11, 11, 22, 23));
        check(!NativeWindowRecovery.CanRecover(true, 0, 0, 22, 22));
        check(!NativeWindowRecovery.CanRecover(true, 11, 11, 0, 0));
    }
}
