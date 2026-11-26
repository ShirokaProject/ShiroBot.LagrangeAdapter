using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lagrange.Core;
using Lagrange.Core.Common.Interface;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Models;
using FlashEntry = ShiroBot.LagrangeAdapter.LagrangeFlashProtocol.Entry;

namespace ShiroBot.LagrangeAdapter.Services;

internal sealed class LagrangeFlashTransfers(
    BotContext context, string downloadRoot, Func<PlatformEvent, Task> publish, CancellationToken stopping)
{
    private static readonly HttpClient DownloadClient = new(new SocketsHttpHandler { AllowAutoRedirect = false });
    private readonly LagrangeFlashProtocol _protocol = new(new LagrangeProtocol(context));
    private readonly SemaphoreSlim _downloads = new(2);
    private readonly ConcurrentDictionary<string, UploadedFileSet> _uploadedFiles = new();

    private sealed record UploadedFileSet(DateTimeOffset Expiry, Dictionary<string, FlashEntry> Files);
    private const int BufferSize = 128 * 1024;

    internal async Task<QFlashFileSet> GetAsync(string source, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stopping, token);
        return (await _protocol.GetAsync(source, lifetime.Token)).Model;
    }

    internal async Task<QFlashShare> UploadAsync(IReadOnlyList<string> uris, string? title, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(uris);
        if (uris.Count == 0) throw new ArgumentException("至少提供一个闪传文件。", nameof(uris));
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stopping, token);
        token = lifetime.Token;
        var streams = new List<Stream>(uris.Count);
        var entries = new List<FlashEntry>(uris.Count);
        try
        {
            foreach (var uri in uris)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(uri);
                var stream = await LagrangeMapper.OpenResourceAsync(uri, bufferHttpResponse: false, token);
                streams.Add(stream);
                var size = checked((uint)stream.Length);
                var (md5, sha1) = await HashAsync(stream, token);
                entries.Add(new FlashEntry(NewId(), FileName(uri, entries.Count), size, sha1, md5, "", 11));
            }

            return await CreateShareAsync(entries, streams, title, token);
        }
        finally
        {
            foreach (var stream in streams) await stream.DisposeAsync();
        }
    }

    internal async Task<QFlashShare> ReshareAsync(string fileSetId, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stopping, token);
        token = lifetime.Token;
        var source = await _protocol.GetAsync(fileSetId, token);
        if (source.Entries.Count == 0) throw new InvalidDataException("源闪传文件集没有文件。");
        var local = await ReadUploadAsync(source.Model.FileSetId, token);
        var entries = new List<FlashEntry>(source.Entries.Count);
        foreach (var file in source.Entries)
        {
            var full = WithLocalMetadata(local, file);
            if (!HasHashes(full)) full = await _protocol.GetEntryAsync(source.Model.FileSetId, file.Id, token);
            if (!HasHashes(full))
                throw new InvalidDataException($"文件 {file.Name} 缺少重新分享所需的哈希。");
            entries.Add(full with { Id = NewId() });
        }

        return await CreateShareAsync(entries, null, source.Model.Title, token);
    }

    private async Task<QFlashShare> CreateShareAsync(IReadOnlyList<FlashEntry> entries, IReadOnlyList<Stream>? streams,
        string? title, CancellationToken token)
    {
        var nick = context.BotInfo?.Name;
        if (string.IsNullOrWhiteSpace(nick))
            nick = (await context.FetchStranger(context.BotUin).WaitAsync(token)).Nickname;
        if (string.IsNullOrWhiteSpace(nick) || string.IsNullOrWhiteSpace(context.Keystore.Uid))
            throw new InvalidOperationException("无法获取当前 QQ 的闪传上传资料。");
        title = string.IsNullOrWhiteSpace(title) ? entries[0].Name : title.Trim();

        var share = await _protocol.CreateAsync(title, entries, context.BotUin.ToString(), context.Keystore.Uid,
            nick, streams is null, token);
        var model = new QFlashFileSet
        {
            Title = title,
            FileSetId = share.FileSetId,
            ShareLink = share.ShareLink,
            TotalSize = entries.Sum(file => (long)file.Size),
            Files = [.. entries.Select(file => file.ToModel())]
        };
        foreach (var entry in entries)
            await _protocol.RegisterAsync(share.FileSetId, entry, token);
        await _protocol.PrepareAsync(share.FileSetId, token);

        long finished = 0;
        var committed = new Dictionary<string, FlashEntry>(StringComparer.Ordinal);
        var lastProgress = Stopwatch.GetTimestamp();
        await PublishAsync(model, QFlashTransferState.Uploading, 0, model.Files, token);
        for (var i = 0; i < entries.Count; i++)
        {
            var file = entries[i];
            var ticket = await _protocol.PreflightAsync(file, token);
            if (ticket.UKey.Length != 0)
            {
                if (streams is null)
                    throw new InvalidOperationException($"QQ 无法从原文件秒传 {file.Name}，请下载后重新上传。");
                var success = await context.UploadFlashFileContentAsync(ticket.UKey, streams[i], async offset =>
                {
                    if (Stopwatch.GetElapsedTime(lastProgress) < TimeSpan.FromSeconds(1)) return;
                    lastProgress = Stopwatch.GetTimestamp();
                    await PublishAsync(model, QFlashTransferState.Uploading, finished + offset, model.Files, token);
                }, token);
                if (!success) throw new IOException($"QQ 拒绝了闪传文件 {file.Name} 的上传。");
                if (ticket.Token.Length == 0)
                    ticket = await _protocol.PreflightAsync(file, token);
            }

            if (ticket.Token.Length == 0)
                throw new InvalidDataException($"QQ 未返回闪传文件 {file.Name} 的提交凭据。");
            await _protocol.CommitAsync(share.FileSetId, file, ticket, token);
            committed.Add(file.Id, file with { Token = ticket.Token });
            finished += file.Size;
            await PublishAsync(model, QFlashTransferState.Uploading, finished, model.Files, token);
        }

        await _protocol.FinalizeAsync(share.FileSetId, token);
        await RememberUploadAsync(share, committed, token);
        await PublishAsync(model, QFlashTransferState.Uploaded, null, model.Files, token);
        return share;
    }

    internal async Task SendCardAsync(string set, long peer, bool group, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stopping, token);
        token = lifetime.Token;
        var uid = group ? null : await LagrangeProtocol.Uid(context, peer, token);
        await _protocol.SendCardAsync(set, peer, uid, token);
    }

    internal async Task StartDownloadAsync(string source, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stopping, token);
        var set = await _protocol.GetAsync(source, lifetime.Token);
        if (set.Entries.Count == 0) throw new InvalidDataException("闪传文件集没有可下载的文件。");
        var local = await ReadUploadAsync(set.Model.FileSetId, lifetime.Token);
        var files = new List<(FlashEntry File, Uri Url)>(set.Entries.Count);
        foreach (var listed in set.Entries)
        {
            var file = WithLocalMetadata(local, listed);
            var url = await _protocol.GetDownloadUrlAsync(set.Model.FileSetId, file, lifetime.Token);
            files.Add((file, url));
        }

        lifetime.Token.ThrowIfCancellationRequested();
        _ = DownloadAsync(set.Model, files);
    }

    private async Task DownloadAsync(QFlashFileSet set, IReadOnlyList<(FlashEntry File, Uri Url)> files)
    {
        var directory = Path.Combine(downloadRoot, NewId());
        try
        {
            await _downloads.WaitAsync(stopping);
            try { await DownloadFilesAsync(set, files, directory, stopping); }
            finally { _downloads.Release(); }
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException || !stopping.IsCancellationRequested)
                context.LogWarning(nameof(LagrangeFlashTransfers), "Flash file set {0} download failed: {1}", null,
                    set.FileSetId, ex is HttpRequestException { StatusCode: { } status }
                        ? $"HTTP {(int)status}" : ex.GetType().Name);
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private async Task DownloadFilesAsync(QFlashFileSet model, IReadOnlyList<(FlashEntry File, Uri Url)> files,
        string directory, CancellationToken token)
    {
        var downloaded = new List<QFlashFile>(files.Count);
        var knownNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        var lastProgress = Stopwatch.GetTimestamp();
        Directory.CreateDirectory(directory);
        await PublishAsync(model, QFlashTransferState.Downloading, 0, model.Files, token);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            foreach (var (file, url) in files)
            {
                var name = SafeName(file.Name, downloaded.Count);
                while (!knownNames.Add(name))
                    name = $"{Path.GetFileNameWithoutExtension(name)}-{NewId()}{Path.GetExtension(name)}";
                var path = Path.Combine(directory, name);
                using var response = await DownloadResponseAsync(url, token);
                response.EnsureSuccessStatusCode();
                await using var body = await response.Content.ReadAsStreamAsync(token);
                await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

                var expectedHash = LagrangeFlashProtocol.DecodeHash(file.Sha1, 20);
                using var sha1 = expectedHash.Length == 0 ? null : IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                long written = 0;
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                while (true)
                {
                    readTimeout.CancelAfter(TimeSpan.FromSeconds(45));
                    int read = await body.ReadAsync(buffer.AsMemory(0, BufferSize), readTimeout.Token);
                    readTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
                    if (read == 0) break;
                    written += read;
                    if (written > file.Size) throw new IOException("闪传文件超过服务端声明的大小。");
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    sha1?.AppendData(buffer.AsSpan(0, read));
                    if (Stopwatch.GetElapsedTime(lastProgress) < TimeSpan.FromSeconds(1)) continue;
                    lastProgress = Stopwatch.GetTimestamp();
                    await PublishAsync(model, QFlashTransferState.Downloading, total + written, model.Files, token);
                }

                if (written != file.Size || (sha1 is not null && !sha1.GetCurrentHash().AsSpan().SequenceEqual(expectedHash)))
                    throw new IOException("闪传文件校验失败。");
                downloaded.Add(new QFlashFile(file.Name, written) { Path = path });
                total += written;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        await PublishAsync(model, QFlashTransferState.Downloaded, null, downloaded.ToArray(), token);
    }

    private async Task RememberUploadAsync(QFlashShare share, Dictionary<string, FlashEntry> committed,
        CancellationToken token)
    {
        var entry = new UploadedFileSet(share.ExpiresAt, committed);
        CacheMetadata(share.FileSetId, entry);

        var path = MetadataPath(share.FileSetId);
        var staging = path + "." + NewId() + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Options = FileOptions.Asynchronous };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var output = new FileStream(staging, options))
                await JsonSerializer.SerializeAsync(output, entry, cancellationToken: token);
            File.SetLastWriteTimeUtc(staging, share.ExpiresAt.UtcDateTime);
            File.Move(staging, path, overwrite: true);

            var now = DateTime.UtcNow;
            foreach (var expired in Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.json"))
                if (File.GetLastWriteTimeUtc(expired) <= now) File.Delete(expired);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            context.LogWarning(nameof(LagrangeFlashTransfers), "Flash metadata could not be saved: {0}", null, ex.GetType().Name);
        }
        finally
        {
            try { File.Delete(staging); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private async Task<UploadedFileSet?> ReadUploadAsync(string id, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        if (_uploadedFiles.TryGetValue(id, out var cached) && cached.Expiry > now) return cached;

        try
        {
            var path = MetadataPath(id);
            if (!File.Exists(path)) return null;
            await using var stream = File.OpenRead(path);
            var metadata = await JsonSerializer.DeserializeAsync<UploadedFileSet>(stream, cancellationToken: token);
            if (metadata is null || metadata.Expiry <= now || metadata.Files is null) return null;
            CacheMetadata(id, metadata);
            return metadata;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            context.LogWarning(nameof(LagrangeFlashTransfers), "Flash metadata could not be opened: {0}", null, ex.GetType().Name);
            return null;
        }
    }

    private void CacheMetadata(string id, UploadedFileSet metadata)
    {
        _uploadedFiles[id] = metadata;
        var now = DateTimeOffset.UtcNow;
        foreach (var expired in _uploadedFiles.Where(item => item.Value.Expiry <= now))
            _uploadedFiles.TryRemove(expired.Key, out _);
        foreach (var oldest in _uploadedFiles.OrderBy(item => item.Value.Expiry).Take(Math.Max(0, _uploadedFiles.Count - 64)))
            _uploadedFiles.TryRemove(oldest.Key, out _);
    }

    private string MetadataPath(string fileSetId)
    {
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(fileSetId)));
        return Path.Combine(downloadRoot, ".metadata", key + ".json");
    }

    private static FlashEntry WithLocalMetadata(UploadedFileSet? metadata, FlashEntry listed) =>
        metadata is not null && metadata.Files.TryGetValue(listed.Id, out var local) && local.Size == listed.Size
            ? listed with
            {
                Sha1 = listed.Sha1.Length == 0 ? local.Sha1 : listed.Sha1,
                Md5 = listed.Md5.Length == 0 ? local.Md5 : listed.Md5,
                Token = listed.Token.Length == 0 ? local.Token : listed.Token
            }
            : listed;

    private static bool HasHashes(FlashEntry file) =>
        LagrangeFlashProtocol.DecodeHash(file.Sha1, 20).Length == 20 &&
        LagrangeFlashProtocol.DecodeHash(file.Md5, 16).Length == 16;

    private static async Task<HttpResponseMessage> DownloadResponseAsync(Uri url, CancellationToken token)
    {
        for (var redirects = 0; ; redirects++)
        {
            var response = await DownloadClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Found
                or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
                return response;

            var location = response.Headers.Location;
            response.Dispose();
            if (redirects >= 3 || location is null || !Uri.TryCreate(url, location, out var next) ||
                next.Scheme != Uri.UriSchemeHttps || next.Port != 443 || next.UserInfo.Length != 0 ||
                !next.IdnHost.EndsWith(".qq.com", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("QQ 返回了无效的闪传下载跳转。");
            url = next;
        }
    }

    private async Task PublishAsync(QFlashFileSet set, QFlashTransferState state, long? transferred,
        IReadOnlyList<QFlashFile> files, CancellationToken token)
    {
        if (token.IsCancellationRequested) return;
        var self = context.BotUin.ToString();
        var progress = new PlatformEvent
        {
            Platform = LagrangeMapper.PlatformId,
            SelfId = self,
            Kind = QEventKinds.FlashFileTransfer,
            Raw = new QFlashFileTransfer
            {
                SelfId = self,
                Time = DateTimeOffset.UtcNow,
                State = state,
                FileSetId = set.FileSetId,
                Title = set.Title,
                ShareLink = set.ShareLink,
                Files = files,
                TransferredSize = transferred,
                TotalSize = set.TotalSize
            }
        };
        try { await publish(progress); }
        catch (Exception ex)
        {
            context.LogWarning(nameof(LagrangeFlashTransfers), "Flash transfer event delivery failed: {0}", null, ex.GetType().Name);
        }
    }

    private static async Task<(string Md5, string Sha1)> HashAsync(Stream stream, CancellationToken token)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, BufferSize), token)) != 0)
            {
                md5.AppendData(buffer.AsSpan(0, read));
                sha1.AppendData(buffer.AsSpan(0, read));
            }

            stream.Position = 0;
            return (Convert.ToHexStringLower(md5.GetCurrentHash()), Convert.ToHexStringLower(sha1.GetCurrentHash()));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string FileName(string value, int index)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return SafeName(value, index);
        if (uri.Scheme == "base64") return SafeName("", index);
        return SafeName(uri.IsFile ? uri.LocalPath : Uri.UnescapeDataString(uri.AbsolutePath), index);
    }

    private static string SafeName(string name, int index)
    {
        name = Path.GetFileName(name.Replace('\\', '/')).Trim();
        const string invalid = "<>:\"/\\|?*";
        name = string.Concat(name.Select(c => char.IsControl(c) || invalid.Contains(c) ? '_' : c)).TrimEnd('.');
        if (name.Length > 80) name = string.Concat(name.EnumerateRunes().Take(50).Select(rune => rune.ToString()));
        return string.IsNullOrWhiteSpace(name) ? $"file-{index + 1}" : name;
    }

    private static string NewId() => Guid.NewGuid().ToString("D");
}
