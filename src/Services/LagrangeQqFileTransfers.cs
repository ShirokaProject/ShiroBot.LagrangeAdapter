using Lagrange.Core.Common.Entity;
using Lagrange.Core.Common.Interface;
using ShiroBot.Model.QQ;

namespace ShiroBot.LagrangeAdapter.Services;

internal sealed partial class LagrangeQqFileApi
{
    public async Task<string> ForwardGroupFileToGroupAsync(string groupId, string fileId, string targetGroupId,
        string targetFolderId = "/", bool sendMessage = true, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFolderId);
        var target = LagrangeMapper.ParseId(targetGroupId, nameof(targetGroupId));
        var copied = await CopyGroupFileAsync(groupId, fileId, target, toFriend: false, cancellationToken);
        if (targetFolderId != "/")
            await GroupFiles.MoveAsync(session.Context, target, copied, targetFolderId, cancellationToken);
        if (sendMessage)
            await GroupFiles.SendAsync(session.Context, target, copied, cancellationToken);
        return copied.Id;
    }

    public async Task<string> ForwardGroupFileToFriendAsync(string groupId, string fileId, string userId,
        bool sendMessage = true, CancellationToken cancellationToken = default)
    {
        var target = LagrangeMapper.ParseId(userId, nameof(userId));
        if (!sendMessage)
            return (await CopyGroupFileAsync(groupId, fileId, target, toFriend: true, cancellationToken)).Id;

        return (await SendGroupFileToFriendAsync(groupId, fileId, target, cancellationToken)).FileId;
    }

    internal async Task<(ulong Sequence, string FileId)> SendGroupFileToFriendAsync(string groupId, string fileId,
        long target, CancellationToken token)
    {
        // The private message needs an offline-file ticket from the private upload service.
        var source = await FindGroupFileAsync(groupId, fileId, token);
        var url = await GroupFiles.DownloadUrlAsync(session.Context, LagrangeMapper.ParseId(groupId, nameof(groupId)), source, token);
        return await SendPrivateFileFromUrlAsync(target, url, source.FileName, token);
    }

    internal async Task<(ulong Sequence, string FileId)> SendPrivateFileFromUrlAsync(long target, string uri,
        string name, CancellationToken token)
    {
        var context = session.Context;
        await using var stream = await LagrangeMapper.OpenResourceAsync(uri, bufferHttpResponse: false, token);
        var sent = await context.RunMessageOperationAsync(() => context.SendFriendFileWithId(target, stream, name), token);
        if (string.IsNullOrEmpty(sent.FileId)) throw new InvalidDataException("私聊文件发送未返回文件 ID。");
        return (sent.Sequence, sent.FileId);
    }

    internal async Task<GroupFiles.CopiedFile> CopyGroupFileAsync(string groupId, string fileId, long target,
        bool toFriend, CancellationToken token)
    {
        var source = await FindGroupFileAsync(groupId, fileId, token);
        return await GroupFiles.CopyAsync(session.Context, LagrangeMapper.ParseId(groupId, nameof(groupId)),
            source, target, toFriend, token);
    }

    private async Task<BotFileEntry> FindGroupFileAsync(string groupId, string fileId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);
        var peer = LagrangeMapper.ParseId(groupId, nameof(groupId));
        var context = session.Context;
        var folders = new Queue<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { "/" };
        folders.Enqueue("/");
        while (folders.TryDequeue(out var folder))
        {
            token.ThrowIfCancellationRequested();
            var entries = await context.FetchGroupFSList(peer, folder).WaitAsync(token);
            foreach (var entry in entries)
            {
                if (entry is BotFileEntry file && file.FileId == fileId) return file;
                if (entry is BotFolderEntry child && visited.Add(child.FolderId)) folders.Enqueue(child.FolderId);
            }
        }

        throw new KeyNotFoundException($"群 {groupId} 找不到文件 {fileId}。");
    }

    public Task<QFlashShare> UploadFlashFilesAsync(IReadOnlyList<string> fileUris, string? title = null,
        CancellationToken cancellationToken = default) => flash.UploadAsync(fileUris, title, cancellationToken);

    public Task<QFlashFileSet> GetFlashFileSetAsync(string shareLinkOrFileSetId,
        CancellationToken cancellationToken = default) => flash.GetAsync(shareLinkOrFileSetId, cancellationToken);

    public Task DownloadFlashFilesAsync(string shareLinkOrFileSetId, CancellationToken cancellationToken = default) =>
        flash.StartDownloadAsync(shareLinkOrFileSetId, cancellationToken);

    public Task<QFlashShare> ReshareFlashFilesAsync(string fileSetId, CancellationToken cancellationToken = default) =>
        flash.ReshareAsync(fileSetId, cancellationToken);

    internal Task SendFlashCardAsync(string fileSetId, long target, bool group, CancellationToken cancellationToken) =>
        flash.SendCardAsync(fileSetId, target, group, cancellationToken);
}
