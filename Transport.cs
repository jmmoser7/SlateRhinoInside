using System;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SlateRhinoInside;
internal static class Framing
{
    public const int MaximumFrame = 4 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
    public static async Task<byte[]?> ReadAsync(Stream stream, CancellationToken cancellation)
    {
        var header = new byte[4];
        int first = await stream.ReadAsync(header, 0, 4, cancellation);
        if (first == 0) return null;
        await ReadRemaining(stream, header, first, cancellation);
        uint length = (uint)(header[0] | header[1] << 8 | header[2] << 16 | header[3] << 24);
        if (length == 0 || length > MaximumFrame) throw new InvalidDataException("Invalid frame length");
        var bytes = new byte[length];
        await ReadRemaining(stream, bytes, 0, cancellation);
        return bytes;
    }
    private static async Task ReadRemaining(Stream stream, byte[] bytes, int offset, CancellationToken cancellation)
    {
        while (offset < bytes.Length)
        {
            int count = await stream.ReadAsync(bytes, offset, bytes.Length - offset, cancellation);
            if (count == 0) throw new EndOfStreamException("Truncated frame");
            offset += count;
        }
    }
    public static JObject Parse(byte[] bytes)
    {
        using var reader = new JsonTextReader(new StringReader(Utf8.GetString(bytes))) { MaxDepth = 40, DateParseHandling = DateParseHandling.None };
        var value = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        if (reader.Read()) throw new InvalidDataException("Trailing JSON data");
        return value;
    }
    public static Task WriteJsonAsync(Stream stream, JObject value, CancellationToken cancellation) => WriteAsync(stream, Utf8.GetBytes(value.ToString(Formatting.None)), cancellation);
    public static async Task WriteAsync(Stream stream, byte[] bytes, CancellationToken cancellation)
    {
        if (bytes.Length == 0 || bytes.Length > MaximumFrame) throw new InvalidDataException("Invalid frame size");
        int length = bytes.Length;
        byte[] header = { (byte)length, (byte)(length >> 8), (byte)(length >> 16), (byte)(length >> 24) };
        await stream.WriteAsync(header, 0, 4, cancellation);
        await stream.WriteAsync(bytes, 0, bytes.Length, cancellation);
        await stream.FlushAsync(cancellation);
    }
    public static string Hash(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }
}

internal sealed class ArtifactServer : IDisposable
{
    private readonly NamedPipeServerStream pipe;
    private readonly ExpectedPeer peer;
    private readonly byte[] bytes;
    public JObject Descriptor { get; }
    private readonly CancellationTokenSource budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    public ArtifactServer(string token, ExpectedPeer expectedPeer, byte[] pixels)
    {
        if (pixels.Length == 0 || pixels.Length > 64 * 1024 * 1024) throw new InvalidDataException("Artifact exceeds transfer budget");
        bytes = pixels;
        peer = expectedPeer;
        string id = "rhino-" + Guid.NewGuid().ToString("N");
        Descriptor = new JObject { ["transfer_id"] = id, ["byte_length"] = bytes.Length, ["media_type"] = "image/png", ["sha256"] = Framing.Hash(bytes) };
        byte[] prefix = Encoding.ASCII.GetBytes(token + "\0" + id);
        string name = "slate-connector-" + Framing.Hash(prefix);
        string sid = WindowsIdentity.GetCurrent().User!.Value;
        if (!Native.ConvertStringSecurityDescriptorToSecurityDescriptor("D:P(A;;GA;;;" + sid + ")", 1, out var descriptor, out _)) throw new Win32Exception();
        try
        {
            var attrs = new Native.SecurityAttributes { Length = System.Runtime.InteropServices.Marshal.SizeOf<Native.SecurityAttributes>(), Descriptor = descriptor };
            var handle = Native.CreateNamedPipe(@"\\.\pipe\" + name, 3 | 0x40000000 | 0x00080000, 8, 1, 65536, 65536, 0, ref attrs);
            if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(); }
            pipe = new NamedPipeServerStream(PipeDirection.InOut, true, false, handle);
        }
        finally { Native.LocalFree(descriptor); }
    }
    public async Task ServeAsync()
    {
        using (budget.Token.Register(() => pipe.Dispose()))
        {
            await pipe.WaitForConnectionAsync(budget.Token);
            peer.Verify(pipe, true);
            await Framing.WriteJsonAsync(pipe, Descriptor, budget.Token);
            for (int offset = 0; offset < bytes.Length;)
            {
                int count = Math.Min(Framing.MaximumFrame, bytes.Length - offset);
                var chunk = new byte[count];
                Buffer.BlockCopy(bytes, offset, chunk, 0, count);
                await Framing.WriteAsync(pipe, chunk, budget.Token);
                offset += count;
            }
            await Framing.WriteJsonAsync(pipe, new JObject { ["transfer_id"] = Descriptor["transfer_id"]!.DeepClone(), ["complete"] = true }, budget.Token);
            var receipt = Framing.Parse(await Framing.ReadAsync(pipe, budget.Token) ?? throw new EndOfStreamException());
            if (receipt.Count != 3 || (string?)receipt["transfer_id"] != (string?)Descriptor["transfer_id"] || (string?)receipt["sha256"] != (string?)Descriptor["sha256"] || (bool?)receipt["verified"] != true)
                throw new InvalidDataException("Unverified transfer receipt");
            peer.VerifyAlive();
        }
    }
    public void Dispose() { budget.Cancel(); pipe.Dispose(); budget.Dispose(); }
}

