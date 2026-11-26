using Lagrange.Core;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Lagrange.Core.Common;
using Lagrange.Core.Common.Interface;
using Lagrange.Core.Internal.Context;
using Lagrange.Core.Exceptions;

namespace ContractChecks;

internal static class HighwayUploadChecks
{
    internal static async Task Run()
    {
        var content = new byte[1024 * 1024 + 17];
        new Random(41).NextBytes(content);
        await CheckImageUploadResponsesAsync();
        await CheckFlashTransferAsync(content);

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var server = Serve(listener, timeout.Token);

        using var bot = BotFactory.Create(new BotConfig { HighwayChunkSize = 1024, HighwayConcurrent = 1 });
        var highway = bot.GetType().GetProperty("HighwayContext", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(bot)!;
        var (ticket, url) = await ParseHighwaySessionAsync(bot, ((IPEndPoint)listener.LocalEndpoint).Port);
        highway.GetType().GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(highway, (ticket, url, DateTimeOffset.UtcNow.AddMinutes(1)));
        var upload = highway.GetType().GetMethod("UploadFile")!;
        using var first = new MemoryStream(content, 0, 1500);
        var firstUpload = (Task<bool>)upload.Invoke(highway, [first, 1001, ReadOnlyMemory<byte>.Empty])!;
        if (await Task.WhenAny(firstUpload, server) == server) await server;
        if (!await firstUpload.WaitAsync(timeout.Token))
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

    private static async Task<(byte[] Ticket, string Url)> ParseHighwaySessionAsync(BotContext bot, int port)
    {
        // C501RspBody with a test ticket and one service-1 endpoint at 127.0.0.1.
        List<byte> address = [0x15, 127, 0, 0, 1, 0x18];
        var remaining = port;
        while (remaining >= 128)
        {
            address.Add((byte)((remaining & 127) | 128));
            remaining >>= 7;
        }
        address.Add((byte)remaining);
        byte[] packet = [
            0x8a, 0x50, (byte)(9 + address.Count),
            0x0a, 1, 1, 0x1a, (byte)(4 + address.Count), 0x08, 1, 0x12, (byte)address.Count,
            .. address,
        ];
        var serviceType = typeof(BotContext).Assembly.GetType(
            "Lagrange.Core.Internal.Services.System.HighwaySessionService", throwOnError: true)!;
        var service = Activator.CreateInstance(serviceType, nonPublic: true)!;
        var parse = serviceType.GetMethod("Parse", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var operation = parse.Invoke(service, [(ReadOnlyMemory<byte>)packet, bot])!;
        var task = (Task)operation.GetType().GetMethod("AsTask")!.Invoke(operation, null)!;
        await task;
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var urls = (Dictionary<uint, List<string>>)result.GetType().GetProperty("HighwayUrls")!.GetValue(result)!;
        var ticket = (byte[])result.GetType().GetProperty("SigSession")!.GetValue(result)!;
        return (ticket, urls[1].Single());
    }

    private static async Task CheckImageUploadResponsesAsync()
    {
        using var bot = BotFactory.Create(new BotConfig());
        var assembly = typeof(BotContext).Assembly;
        const string packets = "Lagrange.Core.Internal.Packets.Service.";
        Type Packet(string name) => assembly.GetType(packets + name, throwOnError: true)!;
        object NewPacket(string name) => Activator.CreateInstance(Packet(name), nonPublic: true)!;
        static void Set(object target, string name, object value) =>
            target.GetType().GetProperty(name)!.SetValue(target, value);

        var head = NewPacket("MultiMediaRespHead");
        Set(head, "Message", "fixture rejected image");
        foreach (var serviceName in new[] { "ImageGroupUploadService", "ImageUploadService" })
        {
            var serviceType = assembly.GetType("Lagrange.Core.Internal.Services.Message." + serviceName, true)!;
            var service = Activator.CreateInstance(serviceType, nonPublic: true)!;
            var process = serviceType.GetMethod("ProcessResponse", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var response = NewPacket("NTV2RichMediaResp");
            Set(response, "RespHead", head);
            Set(head, "RetCode", 941u);
            try
            {
                process.Invoke(service, [response, bot]);
                throw new Exception($"{serviceName} accepted a rejected upload.");
            }
            catch (TargetInvocationException ex) when (ex.InnerException is OperationException { Result: 941 })
            {
            }

            Set(head, "RetCode", 0u);
            var upload = NewPacket("UploadResp");
            var info = NewPacket("MsgInfo");
            var body = NewPacket("MsgInfoBody");
            Set(body, "Index", NewPacket("IndexNode"));
            var bodies = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(Packet("MsgInfoBody")))!;
            bodies.Add(body);
            Set(info, "MsgInfoBody", bodies);
            Set(upload, "MsgInfo", info);
            Set(upload, "UKey", string.Empty);
            Set(response, "Upload", upload);
            var task = (Task)process.Invoke(service, [response, bot])!;
            await task;
            var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
            if (result.GetType().GetProperty("Ext")!.GetValue(result) is not null)
                throw new Exception($"{serviceName} treated an empty upload key as a required upload.");
        }
        Console.WriteLine("Group and private image upload response checks passed.");
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
            var requestLine = headers.Split("\r\n", 2)[0];
            if (requestLine != "POST /cgi-bin/httpconn?htcmd=0x6FF0087&uin=0 HTTP/1.1")
                throw new Exception($"Unexpected Highway request target: {requestLine}");
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
