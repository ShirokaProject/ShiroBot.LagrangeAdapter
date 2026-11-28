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
using Lagrange.Core.Message.Entities;
using ShiroBot.LagrangeAdapter;
using ShiroBot.Model.QQ;

namespace ContractChecks;

internal static class HighwayUploadChecks
{
    internal static async Task Run()
    {
        await CheckUploadHashesAsync();
        var content = new byte[1024 * 1024 + 17];
        new Random(41).NextBytes(content);
        await CheckImageUploadResponsesAsync();
        await CheckRemoteImageDownloadAsync();
        await CheckHttpMediaRetriesAsync();
        await CheckForwardImageDownloadsAsync();
        await CheckFlashTransferAsync(content, appId: 1415);
        await CheckFlashTransferAsync(content, appId: 14901);

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var server = Serve(listener, timeout.Token);

        using var bot = BotFactory.Create(new BotConfig { HighwayChunkSize = 1024, HighwayConcurrent = 1 });
        var highway = bot.GetType().GetProperty("HighwayContext", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(bot)!;
        var (ticket, url) = await ParseHighwaySessionAsync(bot, ((IPEndPoint)listener.LocalEndpoint).Port);
        InstallSession(highway, ticket, url);
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
        await CheckHighwayEndpointFailoverAsync(content);
        await CheckHighwayCallerCancellationAsync(content);
        await CheckHighwayShutdownAsync(content);
        await CheckFlashTransferShutdownAsync(content);
        Console.WriteLine("Remote image download, FlashTransfer and Highway upload, retry, cancellation and shutdown checks passed.");
    }

    private static async Task CheckUploadHashesAsync()
    {
        const int sampleSize = 10 * 1024 * 1024;
        const int prefixSize = 10_002_432;
        var assembly = typeof(BotContext).Assembly;
        var triSha = assembly.GetType("Lagrange.Core.Utility.Cryptography.TriSha1Provider")!
            .GetMethod("CalculateTriSha1", [typeof(Stream)])!;
        var prefixMd5 = assembly.GetType("Lagrange.Core.Internal.Logic.OperationLogic")!
            .GetMethod("HashFilePrefixAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        var data = new byte[3 * sampleSize + 137];
        new Random(42).NextBytes(data);
        var lengthSuffix = new byte[sizeof(long)];

        foreach (var length in new[] { 0, 65_539, 3 * sampleSize, data.Length })
        {
            using var stream = new MemoryStream(data, 0, length);
            if (length != 0) stream.Position = 1;
            var prefix = await (Task<byte[]>)prefixMd5.Invoke(null, [stream, CancellationToken.None])!;
            if (stream.Position != 0 ||
                !prefix.AsSpan().SequenceEqual(MD5.HashData(data.AsSpan(0, Math.Min(length, prefixSize)))))
                throw new Exception($"File prefix MD5 changed for {length} bytes.");

            using var expected = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            if (length <= 3 * sampleSize)
                expected.AppendData(data, 0, length);
            else
            {
                expected.AppendData(data, 0, sampleSize);
                expected.AppendData(data, (length - sampleSize) / 2, sampleSize);
                expected.AppendData(data, length - sampleSize, sampleSize);
            }
            BinaryPrimitives.WriteInt64LittleEndian(lengthSuffix, length);
            expected.AppendData(lengthSuffix);
            if (length != 0) stream.Position = 1;
            var actual = (byte[])triSha.Invoke(null, [stream])!;
            if (stream.Position != 0 || !actual.AsSpan().SequenceEqual(expected.GetHashAndReset()))
                throw new Exception($"File Tri-SHA1 changed for {length} bytes.");
        }

        var cancellableTriSha = triSha.DeclaringType!.GetMethod("CalculateTriSha1", [typeof(Stream), typeof(CancellationToken)])!;
        using var cancellation = new CancellationTokenSource();
        using var interrupted = new CancelAfterReadStream(data, cancellation);
        try
        {
            cancellableTriSha.Invoke(null, [interrupted, cancellation.Token]);
            throw new Exception("Tri-SHA1 ignored cancellation during the file read.");
        }
        catch (TargetInvocationException exception) when (exception.InnerException is OperationCanceledException)
        {
            if (interrupted.Position != 0)
                throw new Exception("Cancelled Tri-SHA1 did not rewind the file.");
        }
    }

    private sealed class CancelAfterReadStream(byte[] data, CancellationTokenSource cancellation) : MemoryStream(data)
    {
        public override int Read(Span<byte> buffer)
        {
            var count = base.Read(buffer);
            cancellation.Cancel();
            return count;
        }
    }

    private static void InstallSession(object highway, byte[] ticket, params string[] urls)
    {
        var field = highway.GetType().GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var session = Activator.CreateInstance(field.FieldType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            [ticket, urls, DateTimeOffset.UtcNow.AddMinutes(1)], null);
        field.SetValue(highway, session);
    }

    private static async Task CheckHighwayEndpointFailoverAsync(byte[] content)
    {
        using var primary = new TcpListener(IPAddress.Loopback, 0);
        using var backup = new TcpListener(IPAddress.Loopback, 0);
        primary.Start();
        backup.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        using var bot = BotFactory.Create(new BotConfig { HighwayChunkSize = 1024 });
        var highway = typeof(BotContext).GetProperty("HighwayContext", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(bot)!;
        var (ticket, firstUrl) = await ParseHighwaySessionAsync(bot, ((IPEndPoint)primary.LocalEndpoint).Port);
        var (_, secondUrl) = await ParseHighwaySessionAsync(bot, ((IPEndPoint)backup.LocalEndpoint).Port);
        InstallSession(highway, ticket, firstUrl, secondUrl);
        var method = highway.GetType().GetMethod("UploadFile")!;
        Task<bool> Upload(Stream data) => (Task<bool>)method.Invoke(highway, [data, 1004, ReadOnlyMemory<byte>.Empty])!;

        byte[] accepted = [0x28, 0, 0, 0, 2, 0, 0, 0, 0, 0x18, 0, 0x29];
        byte[] rejected = [0x28, 0, 0, 0, 4, 0, 0, 0, 0, 0x12, 2, 0x28, 7, 0x29];
        async Task RespondAsync(NetworkStream stream, byte[] response)
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Length: {response.Length}\r\nConnection: close\r\n\r\n"), timeout.Token);
            await stream.WriteAsync(response, timeout.Token);
        }

        async Task<byte[]> ExchangeAsync(TcpListener endpoint, byte[] response)
        {
            using var socket = await endpoint.AcceptTcpClientAsync(timeout.Token);
            using var stream = socket.GetStream();
            var (_, body) = await ReadRequestAsync(stream, timeout.Token);
            await RespondAsync(stream, response);
            return body;
        }

        using var data = new MemoryStream(content, 0, 2500);
        var sending = Upload(data);
        var first = await ExchangeAsync(primary, accepted);
        using var stalledSocket = await primary.AcceptTcpClientAsync(timeout.Token);
        using var stalledStream = stalledSocket.GetStream();
        var (_, stalled) = await ReadRequestAsync(stalledStream, timeout.Token);

        // Leave the primary connected without a response and wait for the fast request deadline.
        using var failoverDeadline = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        failoverDeadline.CancelAfter(TimeSpan.FromSeconds(17));
        using var retrySocket = await backup.AcceptTcpClientAsync(failoverDeadline.Token);
        using var retryStream = retrySocket.GetStream();
        var (_, retried) = await ReadRequestAsync(retryStream, timeout.Token);

        // Start a second upload before the backup has acknowledged any blocks.
        using var concurrentData = new MemoryStream(content, 0, 1024);
        var concurrent = Upload(concurrentData);
        await ExchangeAsync(backup, accepted);
        if (!await concurrent.WaitAsync(timeout.Token) || primary.Pending())
            throw new Exception("A concurrent upload did not skip the cooling endpoint.");

        // With the primary cooling down, the backup may take longer than the fast deadline.
        await Task.Delay(TimeSpan.FromSeconds(11), timeout.Token);
        await RespondAsync(retryStream, accepted);
        var final = await ExchangeAsync(backup, accepted);
        if (!await sending.WaitAsync(timeout.Token) || !stalled.SequenceEqual(retried) ||
            !Payload(first).SequenceEqual(content.AsSpan(0, 1024)) ||
            !Payload(retried).SequenceEqual(content.AsSpan(1024, 1024)) ||
            !Payload(final).SequenceEqual(content.AsSpan(2048, 452)))
            throw new Exception("Highway failover did not preserve the acknowledged block order and retried bytes.");

        using var again = new MemoryStream(content, 0, 1024);
        var response = Upload(again);
        await ExchangeAsync(backup, rejected);
        if (await response.WaitAsync(timeout.Token) || primary.Pending())
            throw new Exception("Highway retried an explicit QQ response rejection on another endpoint.");
        Console.WriteLine("Highway fast timeout, shared endpoint cooldown, and full fallback deadline checks passed.");
    }

    private static async Task CheckRemoteImageDownloadAsync()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cancellation = new CancellationTokenSource();
        var url = $"http://{listener.LocalEndpoint}/image";
        List<Stream> resources = [];
        var chain = LagrangeMapper.ToChainAsync(
            [new ShiroBot.SDK.Models.ImageSegment(url)],
            _ => Task.FromResult<Lagrange.Core.Message.BotMessage?>(null),
            cancellationToken: cancellation.Token, resources: resources);
        using (var client = await listener.AcceptTcpClientAsync(timeout.Token))
        using (var connection = client.GetStream())
        using (var reader = new StreamReader(connection, leaveOpen: true))
        {
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(timeout.Token))) { }
            await connection.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 4194304\r\nConnection: close\r\n\r\n"u8.ToArray(), timeout.Token);
            var chunk = new byte[65536];
            for (var i = 0; i < 64; i++)
                await connection.WriteAsync(chunk, timeout.Token);
            await chain.WaitAsync(timeout.Token);
        }
        await using (var resource = resources.Single())
        {
            if (resource is not FileStream || !resource.CanSeek || resource.Length != 4194304 || resource.Position != 0)
                throw new Exception("Remote image was not spooled to a rewindable file.");
        }

        resources.Clear();
        var interrupted = LagrangeMapper.ToChainAsync(
            [new ShiroBot.SDK.Models.ImageSegment(url)],
            _ => Task.FromResult<Lagrange.Core.Message.BotMessage?>(null),
            cancellationToken: cancellation.Token, resources: resources);
        using var stalled = await listener.AcceptTcpClientAsync(timeout.Token);
        using var stalledStream = stalled.GetStream();
        using var stalledReader = new StreamReader(stalledStream, leaveOpen: true);
        while (!string.IsNullOrEmpty(await stalledReader.ReadLineAsync(timeout.Token))) { }
        await stalledStream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 4194304\r\n\r\nx"u8.ToArray(), timeout.Token);
        await cancellation.CancelAsync();
        await ExpectCancellationAsync(interrupted, timeout.Token);
        if (resources.Count != 0) throw new Exception("An incomplete image was passed to the message chain.");
    }

    private static async Task CheckHttpMediaRetriesAsync()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var uri = $"http://{listener.LocalEndpoint}/image?token=fixture-secret";

        async Task RespondAsync(string response)
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, leaveOpen: true);
            while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 }) { }
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), timeout.Token);
        }

        foreach (var (buffer, failure) in new[]
        {
            (true, "HTTP/1.1 503 Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"),
            (false, "HTTP/1.1 200 OK\r\nContent-Length: 16\r\nConnection: close\r\n\r\nincomplete")
        })
        {
            var pending = LagrangeMapper.OpenResourceAsync(uri, buffer, timeout.Token);
            await RespondAsync(failure);
            await RespondAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
            await using var result = await pending;
            if (result.Position != 0 || result.Length != 2 || result.ReadByte() != 'o' || result.ReadByte() != 'k')
                throw new Exception("Media retry passed through bytes from the unsuccessful response.");
        }

        var denied = LagrangeMapper.OpenResourceAsync(uri, bufferHttpResponse: false, timeout.Token);
        await RespondAsync("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        try
        {
            await denied;
            throw new Exception("A forbidden media download was accepted.");
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            if (listener.Pending() || ex.Message.Contains("fixture-secret", StringComparison.Ordinal))
                throw new Exception("A forbidden media download retried or leaked its URL token.");
        }
    }

    private static async Task CheckForwardImageDownloadsAsync()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string Url(string path) => $"http://{listener.LocalEndpoint}/{path}?secret=hidden";
        static Task<Lagrange.Core.Message.BotMessage?> NoQuote(string _) => Task.FromResult<Lagrange.Core.Message.BotMessage?>(null);
        async Task<string> PathAsync(TcpClient client)
        {
            using var reader = new StreamReader(client.GetStream(), leaveOpen: true);
            var request = await reader.ReadLineAsync(timeout.Token);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(timeout.Token))) { }
            return request!.Split(' ')[1].Split('?')[0];
        }
        Task WriteAsync(TcpClient client, string status, string body) => client.GetStream().WriteAsync(
            Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"),
            timeout.Token).AsTask();

        List<Stream> resources = [];
        var forward = new QOutgoingForward([
            new QForwardedMessage("123", "first", [new QOutgoingImage(Url("first"))]),
            new QForwardedMessage("456", "second", [new QOutgoingImage(Url("second"))])
        ]);
        var prepared = LagrangeMapper.ToMultiMsgAsync(forward, NoQuote, cancellationToken: timeout.Token, resources: resources);
        try
        {
            using var a = await listener.AcceptTcpClientAsync(timeout.Token);
            var aPath = await PathAsync(a);
            // The second download must start while the first response is still pending.
            using var b = await listener.AcceptTcpClientAsync(timeout.Token);
            var bPath = await PathAsync(b);
            if (aPath == bPath || aPath is not ("/first" or "/second") || bPath is not ("/first" or "/second"))
                throw new Exception("Parallel forward downloads did not request both images.");
            await WriteAsync(aPath == "/second" ? a : b, "200 OK", "2");
            await WriteAsync(aPath == "/first" ? a : b, "503 Unavailable", "");
            using var retry = await listener.AcceptTcpClientAsync(timeout.Token);
            if (await PathAsync(retry) != "/first") throw new Exception("The wrong image was retried.");
            await WriteAsync(retry, "200 OK", "1");
            var entity = await prepared.WaitAsync(timeout.Token);
            if (entity.Messages.Count != 2 || entity.Messages.Any(message => message.Entities.Single() is not ImageEntity) ||
                resources.Select(stream => stream.ReadByte()).SequenceEqual(new[] { (int)'1', (int)'2' }) is false)
                throw new Exception("Forward download completion order changed the message order or image streams.");
        }
        finally
        {
            foreach (var stream in resources) await stream.DisposeAsync();
            resources.Clear();
        }

        var denied = new QOutgoingForward([new QForwardedMessage("123", "first", [new QOutgoingImage(Url("denied"))])]);
        var failed = LagrangeMapper.ToMultiMsgAsync(denied, NoQuote, cancellationToken: timeout.Token, resources: resources);
        using var rejected = await listener.AcceptTcpClientAsync(timeout.Token);
        if (await PathAsync(rejected) != "/denied") throw new Exception("Wrong image requested.");
        await WriteAsync(rejected, "403 Forbidden", "");
        try
        {
            await failed.WaitAsync(timeout.Token);
            throw new Exception("A forbidden image was silently accepted in a forwarded message.");
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden &&
            ex.Message.Contains("forwarded image", StringComparison.Ordinal) &&
            !ex.Message.Contains("secret", StringComparison.Ordinal))
        {
            if (resources.Count != 0) throw new Exception("A failed forward retained incomplete image downloads.");
        }
    }

    private static async Task CheckHighwayCallerCancellationAsync(byte[] content)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var cancelled = new CancellationTokenSource();
        await using var bot = BotFactory.Create(new BotConfig { HighwayConcurrent = 1 });
        var highway = typeof(BotContext).GetProperty("HighwayContext", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(bot)!;
        var (ticket, url) = await ParseHighwaySessionAsync(bot, ((IPEndPoint)listener.LocalEndpoint).Port);
        InstallSession(highway, ticket, url);
        var method = highway.GetType().GetMethod("UploadFile")!;
        Task<bool> Upload(Stream stream) => (Task<bool>)method.Invoke(highway, [stream, 1004, ReadOnlyMemory<byte>.Empty])!;

        using var first = new MemoryStream(content, 0, 1024);
        var inFlight = bot.RunMessageOperationAsync(() => Upload(first), cancelled.Token);
        using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
        using var connection = socket.GetStream();
        await ReadRequestAsync(connection, timeout.Token);
        await cancelled.CancelAsync();
        await ExpectCancellationAsync(inFlight, timeout.Token);

        using var second = new MemoryStream(content, 0, 1024);
        var nextUpload = Upload(second);
        using var next = await listener.AcceptTcpClientAsync(timeout.Token);
        using var nextStream = next.GetStream();
        await ReadRequestAsync(nextStream, timeout.Token);
        byte[] accepted = [0x28, 0, 0, 0, 2, 0, 0, 0, 0, 0x18, 0, 0x29];
        await nextStream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 12\r\nConnection: close\r\n\r\n"u8.ToArray(), timeout.Token);
        await nextStream.WriteAsync(accepted, timeout.Token);
        if (!await nextUpload.WaitAsync(timeout.Token))
            throw new Exception("Cancelling a Highway action did not release the only upload slot.");
    }

    private static async Task CheckHighwayShutdownAsync(byte[] content)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var bot = BotFactory.Create(new BotConfig { HighwayConcurrent = 1 });
        var highway = typeof(BotContext).GetProperty("HighwayContext", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(bot)!;
        var (ticket, url) = await ParseHighwaySessionAsync(bot, ((IPEndPoint)listener.LocalEndpoint).Port);
        InstallSession(highway, ticket, url);
        var method = highway.GetType().GetMethod("UploadFile")!;
        Task Upload(Stream stream) => (Task)method.Invoke(highway, [stream, 1001, ReadOnlyMemory<byte>.Empty])!;

        using var first = new MemoryStream(content, 0, 1024);
        var inFlight = Upload(first);
        using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
        using var connection = socket.GetStream();
        await ReadRequestAsync(connection, timeout.Token);
        using var second = new MemoryStream(content, 0, 1024);
        var queued = Upload(second);
        await bot.DisposeAsync().AsTask().WaitAsync(timeout.Token);

        await ExpectCancellationAsync(inFlight, timeout.Token);
        await ExpectCancellationAsync(queued, timeout.Token);
        await ExpectCancellationAsync(Upload(second), timeout.Token);
    }

    private static async Task CheckFlashTransferShutdownAsync(byte[] content)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var bot = BotFactory.Create(new BotConfig { HighwayConcurrent = 1 });
        using var flash = (FlashTransferContext)Activator.CreateInstance(typeof(FlashTransferContext),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [bot, new Uri($"http://{listener.LocalEndpoint}/sliceupload")], null)!;
        using var first = new MemoryStream(content, 0, 1024);
        var inFlight = flash.UploadFile("test-upload-key", 1415, first);
        using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
        using var connection = socket.GetStream();
        await ReadRequestAsync(connection, timeout.Token);
        using var second = new MemoryStream(content, 0, 1024);
        var queued = flash.UploadFile("test-upload-key", 1415, second);
        await Task.Run(flash.Dispose, timeout.Token).WaitAsync(timeout.Token);

        await ExpectCancellationAsync(inFlight, timeout.Token);
        await ExpectCancellationAsync(queued, timeout.Token);
        await ExpectCancellationAsync(flash.UploadFile("test-upload-key", 1415, second), timeout.Token);
    }

    private static async Task ExpectCancellationAsync(Task upload, CancellationToken cancellationToken)
    {
        try
        {
            await upload.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (upload.IsCanceled)
        {
            return;
        }
        throw new Exception("Media upload was not cancelled during shutdown.");
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

    private static async Task CheckFlashTransferAsync(byte[] content, uint appId)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var bot = BotFactory.Create(new BotConfig());
        using var flash = (FlashTransferContext)Activator.CreateInstance(typeof(FlashTransferContext),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [bot, new Uri($"http://{listener.LocalEndpoint}/sliceupload")], null)!;
        using var media = new MemoryStream(content);
        var progress = new List<long>();
        var upload = flash.UploadFile("test-upload-key", appId, media, offset =>
        {
            progress.Add(offset);
            return Task.CompletedTask;
        });
        byte[] prefixHash = SHA1.HashData(content.AsSpan(0, 1024 * 1024));
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
            var wire = ProtoWire.Parse(payload);
            var states = ProtoWire.Child(ProtoWire.Child(wire, 107), 6);
            if (appId == 14901 && !states.Any(state => state.Bytes.AsSpan().SequenceEqual(prefixHash)))
                throw new Exception("General FlashTransfer slice lacked its finalized prefix hash.");
            byte[] response = [0x2a, 7, (byte)'s', (byte)'u', (byte)'c', (byte)'c', (byte)'e', (byte)'s', (byte)'s'];
            await stream.WriteAsync(
                Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {response.Length}\r\nConnection: close\r\n\r\n"),
                timeout.Token);
            await stream.WriteAsync(response, timeout.Token);
            offset += length;
        }
        if (!await upload.WaitAsync(timeout.Token) || !progress.SequenceEqual(new long[] { 1024L * 1024, content.Length }))
            throw new Exception("FlashTransfer success did not report acknowledged progress.");
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
