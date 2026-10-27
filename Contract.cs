using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace SlateRhinoInside;
internal static partial class Contract
{
    public const string Pack = "org.slate.rhino-inside", Application = "com.mcneel.rhino", ViewNamespace = "org.slate.rhino.view";
    public static JObject Schema() => new JObject { ["namespace"] = ViewNamespace, ["version"] = 1 };
    public static JObject Identity(string version) => new JObject { ["pack_id"] = Pack, ["application_id"] = Application, ["application_version"] = version, ["connector_version"] = "0.2.0" };
    private static JObject Number(double minimum = -1000000, double maximum = 1000000) => new JObject { ["type"] = "number", ["minimum"] = minimum, ["maximum"] = maximum };
    private static JObject Text(int maximum = 256) => new JObject { ["type"] = "string", ["maxLength"] = maximum, ["minLength"] = 1 };
    private static JObject Properties(bool material)
    {
        var props = new JObject { ["layer"] = Text(128), ["red"] = Integer(0,255), ["green"] = Integer(0,255), ["blue"] = Integer(0,255) };
        if (material) props["transparency"] = Number(0,1);
        return props;
    }
    private static JObject Integer(int minimum, int maximum) => new JObject { ["type"] = "integer", ["minimum"] = minimum, ["maximum"] = maximum };
    private static JObject Command(string name, string title, string effect, JObject props, params string[] required)
        => new JObject { ["id"] = "org.slate.rhino." + name, ["title"] = title, ["effect"] = effect, ["undo"] = effect == "draft_mutation" ? "native_application" : "none", ["cancellable"] = false, ["agent_allowed"] = true,
            ["parameters"] = new JObject { ["type"] = "object", ["properties"] = props, ["required"] = new JArray(required), ["additionalProperties"] = false } };
    public static JArray Commands()
    {
        var box = Properties(true);
        foreach (string axis in new[] { "x", "y", "z" }) box[axis] = Number();
        foreach (string size in new[] { "width", "depth", "height" }) box[size] = Number(0.001,100000);
        var curve = Properties(false); curve["points"] = Text(4096); curve["closed"] = new JObject { ["type"] = "boolean" };
        var mesh = Properties(true); mesh["vertices"] = Text(4096); mesh["faces"] = Text(4096);
        var camera = new JObject();
        foreach (string axis in new[] { "x","y","z","target_x","target_y","target_z" }) camera[axis] = Number();
        camera["lens"] = Number(1,300); camera["parallel_scale"] = Number(0.01,100000);
        camera["projection"] = new JObject { ["type"] = "string", ["enum"] = new JArray("perspective", "parallel"), ["maxLength"] = 16 };
        var commands = new JArray(
            Command("inspect", "Inspect Rhino document", "inspect", new JObject()),
            Command("zoom_extents", "Frame Rhino model", "ephemeral_view", new JObject()),
            Command("set_camera", "Set Rhino camera", "ephemeral_view", camera, "x","y","z","target_x","target_y","target_z","projection"),
            Command("draft_add_box", "Add box to isolated Rhino draft", "draft_mutation", box, "x","y","z","width","depth","height","layer","red","green","blue"),
            Command("draft_add_curve", "Add polyline to isolated Rhino draft", "draft_mutation", curve, "points","closed","layer","red","green","blue"),
            Command("draft_add_mesh", "Add mesh to isolated Rhino draft", "draft_mutation", mesh, "vertices","faces","layer","red","green","blue"),
            Command("draft_clear", "Clear isolated Rhino draft geometry", "draft_mutation", new JObject()),
            Command("draft_save", "Save isolated Rhino draft", "draft_mutation", new JObject()));
        foreach (var command in WorkflowCommands()) commands.Add(command);
        return commands;
    }
    public static JObject Hello(string version) => new JObject { ["protocol"] = "slate.application", ["versions"] = new JArray(new JObject { ["major"] = 0, ["minor"] = 2 }, new JObject { ["major"] = 0, ["minor"] = 1 }),
        ["identity"] = Identity(version), ["capabilities"] = new JObject { ["offered"] = new JArray("lifecycle","sources","surfaces","commands","views","capture","publication"), ["required"] = new JArray("lifecycle") },
        ["surface_modes"] = new JArray("native_full_ui","companion_window"), ["commands"] = Commands(), ["view_schemas"] = new JArray(Schema()) };
    public static void ValidateParameters(JObject descriptor, JObject parameters)
    {
        var schema = (JObject)descriptor["parameters"]!;
        var props = (JObject)schema["properties"]!;
        foreach (string required in ((JArray)schema["required"]!).Values<string>().Where(v => v != null).Select(v => v!)) if (parameters[required] == null) throw new ProtocolFailure("invalid_request", "Missing parameter: " + required);
        foreach (var param in parameters.Properties())
        {
            var shape = props[param.Name] as JObject ?? throw new ProtocolFailure("invalid_request", "Unknown parameter");
            string type = (string)shape["type"]!;
            bool valid = type == "string" ? param.Value.Type == JTokenType.String : type == "integer" ? param.Value.Type == JTokenType.Integer : type == "boolean" ? param.Value.Type == JTokenType.Boolean : param.Value.Type == JTokenType.Float || param.Value.Type == JTokenType.Integer;
            if (!valid) throw new ProtocolFailure("invalid_request", "Wrong parameter type");
            if (type == "string")
            {
                int length = ((string)param.Value)!.Length;
                if (length < ((int?)shape["minLength"] ?? 0) || length > ((int?)shape["maxLength"] ?? 4096)) throw new ProtocolFailure("invalid_request", "String parameter outside bounds");
            }
            if (type == "number" || type == "integer")
            {
                double value = (double)param.Value;
                if (double.IsNaN(value) || double.IsInfinity(value) || value < ((double?)shape["minimum"] ?? double.MinValue) || value > ((double?)shape["maximum"] ?? double.MaxValue)) throw new ProtocolFailure("invalid_request", "Numeric parameter outside bounds");
            }
            if (shape["enum"] is JArray choices && !choices.Any(choice => JToken.DeepEquals(choice, param.Value))) throw new ProtocolFailure("invalid_request", "Unknown enum value");
        }
    }
}
internal sealed class ProtocolFailure : Exception
{
    public string Code { get; }
    public ProtocolFailure(string code, string message) : base(message) { Code = code; }
}

