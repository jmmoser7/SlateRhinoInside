using Newtonsoft.Json.Linq;

namespace SlateRhinoInside;
internal static partial class Contract
{
    private static JObject Flag() => new JObject { ["type"] = "boolean" };
    private static JObject Page() => new JObject { ["offset"] = Integer(0, 4096), ["limit"] = Integer(1, 64) };
    private static JArray WorkflowCommands()
    {
        var layers = Page(); layers["source_relative"] = Text(1024);
        var import = new JObject { ["source_relative"] = Text(1024), ["prefix"] = Text(128), ["update_existing"] = Flag() };
        var csv = new JObject { ["csv"] = Text(4096), ["source_relative"] = Text(1024), ["update_existing"] = Flag() };
        var captures = new JObject { ["offset"] = Integer(0, 4096), ["limit"] = Integer(1, 2),
            ["view_names"] = Text(4096), ["people_layers"] = Text(4096) };
        var building = Properties(true);
        building.Remove("transparency");
        building["building_id"] = Text(128); building["footprint"] = Text(4096);
        building["floors"] = Integer(1, 80); building["floor_height"] = Number(1, 10); building["z"] = Number();
        return new JArray(
            Command("layers", "Inspect native layer hierarchy", "inspect", layers),
            Command("draft_layers_from_model", "Replicate layer structure into isolated draft", "draft_mutation", import, "source_relative"),
            Command("draft_layers_from_csv", "Create isolated draft layers from bounded CSV", "draft_mutation", csv),
            Command("named_views", "Inspect native named views", "inspect", Page()),
            Command("draft_named_view_add", "Save current view in isolated draft", "draft_mutation", new JObject { ["name"] = Text(128) }, "name"),
            Command("capture_named_views", "Capture named views with people shown and hidden", "ephemeral_view", captures, "people_layers"),
            Command("buildings", "Inspect typed building masses", "inspect", Page()),
            Command("draft_building_set", "Create or replace one typed building mass", "draft_mutation", building,
                "building_id", "footprint", "floors", "floor_height", "layer", "red", "green", "blue"),
            Command("draft_building_floors", "Update one typed building floor count", "draft_mutation",
                new JObject { ["building_id"] = Text(128), ["floors"] = Integer(1, 80) }, "building_id", "floors"),
            Command("draft_layout_template", "Create a native page with bounded detail viewports", "draft_mutation",
                new JObject { ["name"] = Text(128), ["page_width_mm"] = Number(50, 2000), ["page_height_mm"] = Number(50, 2000),
                    ["details"] = Text(4096), ["annotations"] = Text(4096) }, "name", "page_width_mm", "page_height_mm", "details"),
            Command("grasshopper_show", "Show owned native Grasshopper editor", "ephemeral_view", new JObject()),
            Command("grasshopper_hide", "Hide native Grasshopper editor", "ephemeral_view", new JObject()));
    }
}
