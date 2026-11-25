using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Lagrange.Core.Common;
using Lagrange.Core.Common.Interface;
using Lagrange.Core.Internal.Context;

namespace ContractChecks;

internal static class HighwayUploadChecks
{
    internal static async Task Run()
    {
        var content = new byte[1024 * 1024 + 17];
        new Random(41).NextBytes(content);
        await CheckFlashTransferAsync(content);

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var server = Serve(listener, timeout.Token);

        using var bot = BotFactory.Create(new BotConfig { HighwayChunkSize = 1024, HighwayConcurrent = 1 });
        var highway = bot.GetType().GetProperty("HighwayContext", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(bot)!;
        highway.GetType().GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(highway, (new byte[] { 1 }, listener.LocalEndpoint.ToString()!, DateTimeOffset.UtcNow.AddMinutes(1)));
        var upload = highway.GetType().GetMethod("UploadFile")!;
        using var first = new MemoryStream(content, 0, 1500);
        if (!await ((Task<bool>)upload.Invoke(highway, [first, 1001, ReadOnlyMemory<byte>.Empty])!).WaitAsync(timeout.Token))
            throw new Exception("Highway did not recover after a truncated HTTP response.");
        using var second = new MemoryStream(content, 0, 1500);
        if (await ((Task<bool>)upload.Invoke(highway, [second, 1001, ReadOnlyMemory<byte>.Empty])!).WaitAsync(timeout.Token))
            throw new Exception("Highway accepted a rejected segment.");

        var blocks = await server;
        if (!blocks[0].SequenceEqual(blocks[1]) ||
            !Payload(blocks[1]).SequenceEqual(content.AsSpan(0, 1024)) ||
            !Payload(blocks[2]).SequenceEqual(content.AsSpan(1024, 476)) ||
            !Payload(blocks[3]).SequenceEqual(content.AsSpan(0, 1024)))
            throw new Exception("Highway changed the retried block or its payload order.");
        Console.WriteLine("FlashTransfer video and Highway upload retry/rejection checks passed.");
    }

    private static async Task CheckFlashTransferAsync(byte[] content)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var bot = BotFactory.Create(new BotConfig());
        using var flash = (FlashTransferContext)Activator.CreateInstance(typeof(FlashTransferContext),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [bot, new Uri($"http://{listener.LocalEndpoint}/sliceupload")], null)!;
        using var video = new MemoryStream(content);
        var upload = flash.UploadFile("test-upload-key", 1415, video);
        byte[] finalHash = SHA1.HashData(content);
        int offset = 0;
        foreach (int length in new[] { 1024 * 1024, 17 })
        {
            using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = socket.GetStream();
            var (headers, payload) = await ReadRequestAsync(stream, timeout.Token);
            if (!headers.StartsWith("POST /sliceupload HTTP/1.1", StringComparison.Ordinal) ||
                !payload.AsSpan().EndsWith(content.AsSpan(offset, length)) ||
                payload.AsSpan().IndexOf(finalHash) < 0)
                throw new Exception("FlashTransfer did not send the expected slice and final SHA-1.");
            byte[] response = [0x2a, 7, (byte)'s', (byte)'u', (byte)'c', (byte)'c', (byte)'e', (byte)'s', (byte)'s'];
            await stream.WriteAsync(
                Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {response.Length}\r\nConnection: close\r\n\r\n"),
                timeout.Token);
            await stream.WriteAsync(response, timeout.Token);
            offset += length;
        }
        if (!await upload.WaitAsync(timeout.Token))
            throw new Exception("FlashTransfer rejected a successful upload.");
    }

    private static ReadOnlySpan<byte> Payload(byte[] frame)
    {
        var header = BinaryPrimitives.ReadInt32BigEndian(frame.AsSpan(1));
        var length = BinaryPrimitives.ReadInt32BigEndian(frame.AsSpan(5));
        return frame.AsSpan(9 + header, length);
    }

    private static async Task<List<byte[]>> Serve(TcpListener listener, CancellationToken token)
    {
        byte[] accepted = [0x28, 0, 0, 0, 2, 0, 0, 0, 0, 0x18, 0, 0x29];
        byte[] rejected = [0x28, 0, 0, 0, 4, 0, 0, 0, 0, 0x12, 2, 0x28, 7, 0x29];
        var blocks = new List<byte[]>();
        for (var i = 0; i < 4; i++)
        {
            using var client = await listener.AcceptTcpClientAsync(token);
            using var stream = client.GetStream();
            var (headers, block) = await ReadRequestAsync(stream, token);
            if (!headers.StartsWith("POST /cgi-bin/httpconn?htcmd=0x6FF0087&uin=", StringComparison.Ordinal))
                throw new Exception("Highway request is missing the httpconn endpoint.");
            blocks.Add(block);

            var response = i == 3 ? rejected : accepted;
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Length: {response.Length}\r\nConnection: close\r\n\r\n"), token);
            await stream.WriteAsync(response.AsMemory(0, i == 0 ? 1 : response.Length), token);
        }
        return blocks;
    }
    private static async Task<(string Headers, byte[] Body)> ReadRequestAsync(NetworkStream stream, CancellationToken token)
    {
        var headers = new StringBuilder();
        var value = new byte[1];
        while (true)
        {
            await stream.ReadExactlyAsync(value, token);
            headers.Append((char)value[0]);
            if (headers.Length >= 4 && headers[^4] == '\r' && headers[^3] == '\n' &&
                headers[^2] == '\r' && headers[^1] == '\n') break;
        }
        var text = headers.ToString();
        if (text.Contains("Expect: 100-continue", StringComparison.OrdinalIgnoreCase))
            await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), token);
        var contentLength = text.Split("\r\n")
            .Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
        var body = new byte[int.Parse(contentLength.AsSpan("Content-Length:".Length))];
        await stream.ReadExactlyAsync(body, token);
        return (text, body);
    }
}
