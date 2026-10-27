using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace SlateRhinoInside;

// Bounded declarative data only. This parser never evaluates expressions.
internal sealed class LayerSpec
{
    internal string Path = "";
    internal int Red = 128, Green = 128, Blue = 128;
    internal bool Visible = true, Locked;
    internal string Parent => Path.Contains("::") ? Path.Substring(0, Path.LastIndexOf("::", StringComparison.Ordinal)) : "";
    internal string Name => Path.Split(new[] { "::" }, StringSplitOptions.None).Last();
    internal static string ValidatePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value != value.Trim()
            || value.Any(char.IsControl)) throw new ProtocolFailure("invalid_request", "Layer path is invalid");
        var parts = value.Split(new[] { "::" }, StringSplitOptions.None);
        if (parts.Length > 16 || parts.Any(p => p.Length == 0 || p.Length > 128 || p != p.Trim()))
            throw new ProtocolFailure("invalid_request", "Layer path needs 1..16 nonempty names");
        return value;
    }
    internal static List<LayerSpec> FromCsv(string text)
    {
        var rows = CsvRows(text);
        string[] headers = { "path", "red", "green", "blue", "visible", "locked" };
        if (rows.Count == 0 || rows[0].Count != headers.Length
            || !rows[0].Select(v => v.Trim()).SequenceEqual(headers, StringComparer.OrdinalIgnoreCase))
            throw new ProtocolFailure("invalid_request", "CSV header must be path,red,green,blue,visible,locked");
        var result = new List<LayerSpec>();
        foreach (var row in rows.Skip(1))
        {
            if (row.Count != 6) throw new ProtocolFailure("invalid_request", "Every CSV row needs six columns");
            result.Add(new LayerSpec { Path = ValidatePath(row[0]), Red = Byte(row[1]), Green = Byte(row[2]),
                Blue = Byte(row[3]), Visible = Bool(row[4]), Locked = Bool(row[5]) });
        }
        ValidateUnique(result);
        return result;
    }
    internal static void ValidateUnique(IReadOnlyList<LayerSpec> specs)
    {
        if (specs.Count == 0 || specs.Count > 512) throw new ProtocolFailure("invalid_request", "Use 1..512 layers");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var spec in specs)
            if (!paths.Add(ValidatePath(spec.Path))) throw new ProtocolFailure("invalid_request", "Duplicate layer path");
    }
    private static int Byte(string text) => int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value)
        && value >= 0 && value <= 255 ? value : throw new ProtocolFailure("invalid_request", "Layer RGB must be integers 0..255");
    private static bool Bool(string text) => text.Trim().ToLowerInvariant() switch {
        "true" or "1" => true, "false" or "0" => false,
        _ => throw new ProtocolFailure("invalid_request", "Layer flags must be true/false or 1/0") };
    private static List<List<string>> CsvRows(string text)
    {
        if (text.Length > 262144) throw new ProtocolFailure("invalid_request", "CSV exceeds 256 KiB character budget");
        var rows = new List<List<string>>(); var row = new List<string>(); var cell = new StringBuilder();
        bool quoted = false, closed = false;
        void FinishCell() { row.Add(cell.ToString()); cell.Clear(); closed = false; }
        void FinishRow() { FinishCell(); if (row.Any(s => s.Length > 0)) rows.Add(row); row = new List<string>();
            if (rows.Count > 513) throw new ProtocolFailure("invalid_request", "CSV has more than 512 layer rows"); }
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c != '"') cell.Append(c);
                else if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else { quoted = false; closed = true; }
            }
            else if (c == ',') FinishCell();
            else if (c == '\r' || c == '\n') { if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++; FinishRow(); }
            else if (c == '"' && cell.Length == 0 && !closed) quoted = true;
            else if (closed || c == '"') throw new ProtocolFailure("invalid_request", "Malformed quoted CSV cell");
            else cell.Append(c);
            if (row.Count > 6 || cell.Length > 1024) throw new ProtocolFailure("invalid_request", "CSV row exceeds bounds");
        }
        if (quoted) throw new ProtocolFailure("invalid_request", "Unterminated CSV quote");
        if (row.Count != 0 || cell.Length != 0 || closed) FinishRow();
        return rows;
    }
}

internal sealed class BuildingSpec
{
    internal string Id = "", Layer = "";
    internal int Floors, Red, Green, Blue;
    internal double FloorHeight, Elevation;
    internal double[][] Footprint = Array.Empty<double[]>();
    internal double Height => Floors * FloorHeight;
    internal static BuildingSpec Parse(JObject parameters)
    {
        var spec = new BuildingSpec { Id = (string)parameters["building_id"]!, Layer = (string)parameters["layer"]!,
            Floors = (int)parameters["floors"]!, FloorHeight = (double)parameters["floor_height"]!,
            Elevation = (double?)parameters["z"] ?? 0, Red = (int)parameters["red"]!,
            Green = (int)parameters["green"]!, Blue = (int)parameters["blue"]! };
        if (!ConnectorSession.ValidId(spec.Id) || spec.Id.Length > 128) throw new ProtocolFailure("invalid_request", "Invalid building identifier");
        LayerSpec.ValidatePath(spec.Layer);
        if (new[] { spec.Red, spec.Green, spec.Blue }.Any(c => c < 0 || c > 255))
            throw new ProtocolFailure("invalid_request", "Building RGB values must be 0..255");
        if (spec.Floors < 1 || spec.Floors > 80 || !Finite(spec.FloorHeight) || spec.FloorHeight < 1 || spec.FloorHeight > 10
            || !Finite(spec.Elevation) || Math.Abs(spec.Elevation) > 1000000)
            throw new ProtocolFailure("invalid_request", "Building height data outside bounds");
        var points = JArray.Parse((string)parameters["footprint"]!);
        if (points.Count < 3 || points.Count > 64) throw new ProtocolFailure("invalid_request", "Building footprint needs 3..64 vertices");
        spec.Footprint = points.Select(item => {
            if (item is not JArray p || p.Count != 2 || p.Any(v => v.Type != JTokenType.Integer && v.Type != JTokenType.Float))
                throw new ProtocolFailure("invalid_request", "Footprint vertices must be numeric [x,y]");
            var xy = new[] { (double)p[0], (double)p[1] };
            if (xy.Any(v => !Finite(v) || Math.Abs(v) > 1000000)) throw new ProtocolFailure("invalid_request", "Footprint exceeds coordinate bounds");
            return xy;
        }).ToArray();
        double area = 0;
        for (int i = 0; i < spec.Footprint.Length; i++) { var a = spec.Footprint[i]; var b = spec.Footprint[(i + 1) % spec.Footprint.Length]; area += a[0] * b[1] - b[0] * a[1]; }
        if (Math.Abs(area) < 0.000001) throw new ProtocolFailure("invalid_request", "Footprint has zero projected area");
        return spec;
    }
    internal JObject Parameters() => new JObject { ["building_id"] = Id, ["layer"] = Layer, ["floors"] = Floors,
        ["floor_height"] = FloorHeight, ["z"] = Elevation, ["red"] = Red, ["green"] = Green, ["blue"] = Blue,
        ["footprint"] = new JArray(Footprint.Select(p => new JArray(p[0], p[1]))).ToString(Newtonsoft.Json.Formatting.None) };
    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
