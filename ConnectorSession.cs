using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SlateRhinoInside;
internal sealed class ConnectorSession
{
    internal static readonly TimeSpan IdleFrameTimeout = TimeSpan.FromHours(8);
    private readonly Options options;
    private readonly ExpectedPeer peer;
    private readonly RhinoAdapter adapter;
    private readonly Dictionary<string,(JObject Request, JObject Reply)> ledger = new Dictionary<string,(JObject,JObject)>(StringComparer.Ordinal);
    private long ledgerBytes;
    private JObject welcome = null!;
    private readonly JObject hello;
    private bool detached;
    private int activeTransfers;
    public ConnectorSession(Options launch, ExpectedPeer expectedPeer, RhinoAdapter rhino)
    { options = launch; peer = expectedPeer; adapter = rhino; hello = Contract.Hello(rhino.ApplicationVersion); }
    public async Task RunAsync()
    {
        using var pipe = new NamedPipeClientStream(".", "slate-connector-" + options.Token, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        using (var connectBudget = new CancellationTokenSource(TimeSpan.FromSeconds(30))) await pipe.ConnectAsync(30000, connectBudget.Token);
        peer.Verify(pipe, false);
        using (var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        using (budget.Token.Register(() => pipe.Dispose()))
        {
            await Framing.WriteJsonAsync(pipe, new JObject { ["kind"] = "hello", ["hello"] = hello }, budget.Token);
            var frame = Framing.Parse(await Framing.ReadAsync(pipe, budget.Token) ?? throw new EndOfStreamException());
            welcome = frame["welcome"] as JObject ?? throw new InvalidDataException("Expected welcome");
            ValidateWelcome();
        }
        Console.WriteLine("CONNECTOR_READY session=" + (string)welcome["session_id"]! + " protocol=0." + (int)welcome["version"]!["minor"]!);
        while (!detached)
        {
            peer.VerifyAlive();
            JObject? frame;
            using (var budget = new CancellationTokenSource(IdleFrameTimeout))
            using (budget.Token.Register(() => pipe.Dispose()))
            {
                byte[]? bytes = await Framing.ReadAsync(pipe, budget.Token);
                if (bytes == null) break;
                frame = Framing.Parse(bytes);
            }
            peer.Verify(pipe, false);
            var request = frame["request"] as JObject;
            if ((string?)frame["kind"] != "request" || request == null) throw new InvalidDataException("Expected request envelope");
            JObject reply = await HandleAsync(request);
            using (var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            using (budget.Token.Register(() => pipe.Dispose())) await Framing.WriteJsonAsync(pipe, new JObject { ["kind"] = "reply", ["reply"] = reply }, budget.Token);
        }
    }
    private void ValidateWelcome()
    {
        int? minor = (int?)welcome["version"]?["minor"];
        if ((string?)welcome["protocol"] != "slate.application" || (int?)welcome["version"]?["major"] != 0 || (minor != 1 && minor != 2) || !JToken.DeepEquals(welcome["identity"], hello["identity"])) throw new InvalidDataException("Welcome identity/version mismatch");
        if (!ValidId((string?)welcome["session_id"]) || (ulong?)welcome["generation"] == null) throw new InvalidDataException("Invalid welcome lifecycle identity");
        var caps = welcome["capabilities"] as JArray ?? throw new InvalidDataException("No capabilities");
        var offered = ((JArray)hello["capabilities"]!["offered"]!).Values<string>().ToArray();
        if (!caps.Values<string>().Contains("lifecycle") || caps.Values<string>().Distinct().Count() != caps.Count || caps.Values<string>().Any(c => !offered.Contains(c))) throw new InvalidDataException("Unoffered negotiated capability");
    }
    internal static bool ValidId(string? value) => value != null && value.Length > 0 && value.Length <= 256 && value.All(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_' || c == ':');
    private async Task<JObject> HandleAsync(JObject request)
    {
        string? id = (string?)request["request_id"];
        if (!ValidId(id)) return Reply(request, Failure("invalid_request", "Invalid request identity"));
        if (ledger.TryGetValue(id!, out var cached)) return JToken.DeepEquals(cached.Request, request) ? (JObject)cached.Reply.DeepClone() : Reply(request, Failure("request_id_conflict", "Request identity reused with different content"));
        if (ledger.Count >= 1024 || ledgerBytes + Encoding.UTF8.GetByteCount(request.ToString(Formatting.None)) + 300000 > 8 * 1024 * 1024) return Reply(request, Failure("request_cache_full", "Reconnect rather than evicting replay protection"));
        JObject outcome;
        try { ValidateRequest(request); outcome = await DispatchAsync(request); }
        catch (ProtocolFailure error) { outcome = Failure(error.Code, error.Message); }
        catch (Exception error) { outcome = Failure("invalid_result", "Rhino operation failed: " + error.GetType().Name + ": " + error.Message); }
        var reply = Reply(request, outcome);
        ledger.Add(id!, ((JObject)request.DeepClone(), reply));
        ledgerBytes += Encoding.UTF8.GetByteCount(request.ToString(Formatting.None)) + Encoding.UTF8.GetByteCount(reply.ToString(Formatting.None));
        return reply;
    }
    private void ValidateRequest(JObject request)
    {
        if ((string?)request["session_id"] != (string?)welcome["session_id"]) throw new ProtocolFailure("session_mismatch", "Wrong session");
        if (!JToken.DeepEquals(request["generation"], welcome["generation"])) throw new ProtocolFailure("stale_generation", "Wrong generation");
        if (detached) throw new ProtocolFailure("detached", "Session detached");
        var op = request["operation"] as JObject ?? throw new ProtocolFailure("invalid_request", "Missing operation");
        string method = (string?)op["method"] ?? "";
        string capability = method == "bind_source" || method == "unbind_source" ? "sources" : method == "describe_commands" || method == "invoke_command" ? "commands" : method == "surface" || method == "describe_surfaces" ? "surfaces" : method == "describe_view_schemas" || method == "snapshot_view" || method == "restore_view" ? "views" : method == "capture" ? "capture" : method == "publish" ? "publication" : "lifecycle";
        if (!((JArray)welcome["capabilities"]!).Values<string>().Contains(capability)) throw new ProtocolFailure("unsupported_operation", "Capability was not negotiated");
        var caller = request["caller"] as JObject ?? throw new ProtocolFailure("authority_denied", "Missing trusted caller");
        bool human = (string?)caller["kind"] == "human", agent = (string?)caller["kind"] == "agent";
        if ((!human && !agent) || (human && (string?)caller["action_id"] != (string?)request["request_id"]) || (agent && !ValidId((string?)caller["agent_id"]))) throw new ProtocolFailure("authority_denied", "Invalid caller identity");
        bool needsSource = new[] { "unbind_source", "invoke_command", "surface", "describe_surfaces", "snapshot_view", "restore_view", "capture", "publish" }.Contains(method);
        if (needsSource)
        {
            if (adapter.BoundSource == null) throw new ProtocolFailure("source_not_bound", "Bind a model first");
            if ((string?)request["source"]?["source_id"] != (string?)adapter.BoundSource["source_id"]) throw new ProtocolFailure("source_mismatch", "Wrong selected source");
            if ((string?)request["source"]?["revision"] != (string?)adapter.BoundSource["revision"]) throw new ProtocolFailure("stale_revision", "Source revision changed");
        }
        if (agent && new[] { "bind_source", "unbind_source", "surface", "detach", "restore_view" }.Contains(method)) throw new ProtocolFailure("authority_denied", "Lifecycle and surface action require human origin");
        if (method == "invoke_command")
        {
            string commandId = (string?)op["command_id"] ?? "";
            var descriptor = Contract.Commands().OfType<JObject>().SingleOrDefault(v => (string?)v["id"] == commandId) ?? throw new ProtocolFailure("unsupported_operation", "Command not advertised");
            Contract.ValidateParameters(descriptor, op["parameters"] as JObject ?? throw new ProtocolFailure("invalid_request", "Parameters must be a closed object"));
            string effect = (string)descriptor["effect"]!;
            if (effect == "draft_mutation")
            {
                if (!adapter.Isolated) throw new ProtocolFailure("authority_denied", "Native modeling requires an isolated draft");
                if (human && (string?)caller["target_source"] != (string?)adapter.BoundSource!["source_id"]) throw new ProtocolFailure("authority_denied", "Human action must name the draft target");
                if (agent && ((int)welcome["version"]!["minor"]! < 2 || (bool?)caller["workspace_autonomy"] != true)) throw new ProtocolFailure("authority_denied", "Agent draft modeling requires v0.2 and explicit workspace autonomy");
                // The authenticated Slate host additionally enforces its scoped agent/document/command grant.
            }
            if (agent && effect == "ephemeral_view" && (int)welcome["version"]!["minor"]! < 2) throw new ProtocolFailure("authority_denied", "v0.1 agents may inspect only");
        }
    }
    private async Task<JObject> DispatchAsync(JObject request)
    {
        var op = (JObject)request["operation"]!;
        string method = (string)op["method"]!;
        switch (method)
        {
            case "bind_source":
                if (!ValidId((string?)op["selection_id"])) throw new ProtocolFailure("invalid_request", "Explicit source selection receipt required");
                var source = op["source"] as JObject ?? throw new ProtocolFailure("invalid_request", "Missing source reference");
                if (!ValidId((string?)source["source_id"]) || !ValidId((string?)source["revision"]) || source["locator"] is not JObject) throw new ProtocolFailure("invalid_request", "Malformed source identity");
                var binding = await adapter.OnUiAsync(() => adapter.Bind(source, (string)op["mode"]!));
                return new JObject { ["status"] = "source_bound", ["binding"] = binding };
            case "describe_commands": return new JObject { ["status"] = "commands", ["commands"] = Contract.Commands() };
            case "describe_surfaces": return new JObject { ["status"] = "surfaces", ["surfaces"] = await adapter.OnUiAsync(adapter.Surfaces) };
            case "describe_view_schemas": return new JObject { ["status"] = "view_schemas", ["schemas"] = new JArray(Contract.Schema()) };
            case "invoke_command":
                var value = await adapter.OnUiAsync(() => adapter.Invoke((string)op["command_id"]!, (JObject)op["parameters"]!));
                bool mutation = ((string)op["command_id"]!).StartsWith("org.slate.rhino.draft_", StringComparison.Ordinal);
                string? revised = mutation ? "rhino-draft-" + Guid.NewGuid().ToString("N") : null;
                if (revised != null) adapter.BoundSource!["revision"] = revised;
                return new JObject { ["status"] = "command_result", ["value"] = value, ["revision"] = revised };
            case "snapshot_view": return new JObject { ["status"] = "view", ["view"] = await adapter.OnUiAsync(() => adapter.Snapshot((JObject)op["schema"]!)) };
            case "restore_view": await adapter.OnUiAsync(() => { adapter.RestoreView((JObject)op["view"]!); return true; }); return Ack();
            case "surface": await adapter.OnUiAsync(() => { adapter.Surface((JObject)op["action"]!); return true; }); return Ack();
            case "capture": case "publish":
                if (Interlocked.Increment(ref activeTransfers) > 4) { Interlocked.Decrement(ref activeTransfers); throw new ProtocolFailure("transfer_too_large", "Capture transfer budget reached"); }
                ArtifactServer transfer;
                (JObject Metadata, byte[] Bytes) capture;
                try { capture = await adapter.OnUiAsync(() => adapter.Capture(method == "capture" ? (JObject)op["schema"]! : Contract.Schema())); transfer = new ArtifactServer(options.Token, peer, capture.Bytes); }
                catch { Interlocked.Decrement(ref activeTransfers); throw; }
                _ = Task.Run(async () => { using (transfer) { try { await transfer.ServeAsync(); } catch (Exception error) { Console.Error.WriteLine("Artifact: " + error.Message); } finally { Interlocked.Decrement(ref activeTransfers); } } });
                return method == "capture" ? new JObject { ["status"] = "captured", ["capture"] = new JObject { ["metadata"] = capture.Metadata, ["artifact"] = transfer.Descriptor } } : new JObject { ["status"] = "published", ["publication"] = new JObject { ["source"] = adapter.Scope(), ["behavior"] = "poster_and_pointer", ["artifact"] = transfer.Descriptor } };
            case "detach":
                await adapter.OnUiAsync(() => { adapter.Detach((string)op["policy"]!); return true; });
                detached = true;
                return new JObject { ["status"] = "detached", ["preserved_unsaved"] = true, ["application_terminated"] = false };
            case "unbind_source": throw new ProtocolFailure("unsupported_operation", "Preserve-detach this dedicated model session; use a fresh helper for another model");
            case "operation_status":
                string target = (string?)op["request_id"] ?? "";
                if (!ledger.ContainsKey(target)) throw new ProtocolFailure("request_not_found", "Request not retained");
                return new JObject { ["status"] = "operation_state", ["target_request_id"] = target, ["state"] = "completed" };
            case "cancel": throw new ProtocolFailure("cancellation_unsupported", "Rhino commands are short atomic UI operations and do not advertise cancellation");
            default: throw new ProtocolFailure("unsupported_operation", "Unknown operation");
        }
    }
    private static JObject Ack() => new JObject { ["status"] = "ack" };
    private static JObject Failure(string code, string message) => new JObject { ["status"] = "failure", ["error"] = new JObject { ["code"] = code, ["message"] = message } };
    private static JObject Reply(JObject request, JObject outcome) => new JObject { ["session_id"] = request["session_id"]?.DeepClone(), ["generation"] = request["generation"]?.DeepClone(), ["request_id"] = request["request_id"]?.DeepClone(), ["source"] = request["source"]?.DeepClone(), ["outcome"] = outcome };
}

