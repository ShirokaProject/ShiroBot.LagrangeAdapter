using Lagrange.Core.Common.Entity;
using Lagrange.Core.Common.Interface;
using Lagrange.Core.Message;
using Lagrange.Core.Message.Entities;
using ShiroBot.Model.QQ;

namespace ShiroBot.LagrangeAdapter.Services;

internal sealed partial class LagrangeQqMessageApi
{
    public async Task<string> ForwardSingleMessageAsync(QMessageScene scene, string peerId, string messageId,
        QMessageScene targetScene, string targetPeerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (scene is not (QMessageScene.Group or QMessageScene.Friend or QMessageScene.Temp) ||
            targetScene is not (QMessageScene.Group or QMessageScene.Friend))
            throw LagrangeMapper.Unsupported("单条消息转发的会话类型");

        var context = session.Context;
        var sourcePeer = LagrangeMapper.ParseId(peerId, nameof(peerId));
        var target = LagrangeMapper.ParseId(targetPeerId, nameof(targetPeerId));
        var group = targetScene == QMessageScene.Group;
        var source = await context.RunMessageOperationAsync(
            () => session.ResolveMessageAsync(messageId, sourcePeer, scene == QMessageScene.Group, cancellationToken),
            cancellationToken) ?? throw new KeyNotFoundException($"找不到源消息 {messageId}。");

        var expectedType = scene switch
        {
            QMessageScene.Group => MessageType.Group,
            QMessageScene.Friend => MessageType.Private,
            _ => MessageType.Temp
        };
        if (source.Type != expectedType)
            throw new KeyNotFoundException($"会话中找不到源消息 {messageId}。");

        var file = source.Entities.FirstOrDefault(entity => entity is GroupFileEntity or PrivateFileEntity);
        if (file is not null)
        {
            if (source.Entities.Any(entity => !ReferenceEquals(entity, file)))
                throw LagrangeMapper.Unsupported("带其他消息段的文件转发");

            if (file is GroupFileEntity groupFile)
            {
                if (group)
                {
                    var copied = await files.CopyGroupFileAsync(peerId, groupFile.FileId, target, toFriend: false, cancellationToken);
                    return await ObserveForwardAsync(target, group,
                        entity => entity is GroupFileEntity forwarded && forwarded.FileId == copied.Id,
                        () => GroupFiles.SendAsync(context, target, copied, cancellationToken), cancellationToken);
                }

                return (await files.SendGroupFileToFriendAsync(peerId, groupFile.FileId, target, cancellationToken)).Sequence.ToString();
            }

            var privateFile = (PrivateFileEntity)file;
            var url = await files.GetPrivateFileDownloadUrlAsync(peerId, privateFile.FileId, privateFile.FileHash,
                source.Contact.Uin == context.BotUin, cancellationToken);
            if (!group)
                return (await files.SendPrivateFileFromUrlAsync(target, url, privateFile.FileName, cancellationToken)).Sequence.ToString();

            var groupFileId = await files.UploadGroupFileAsync(targetPeerId, url, privateFile.FileName, cancellationToken: cancellationToken);
            return await FindGroupFileMessageAsync(target, groupFileId, cancellationToken);
        }

        if (source.Entities.Count == 1 && source.Entities[0] is MarkdownEntity markdown &&
            LagrangeMapper.TryReadFlashCard(markdown.Content) is { } card)
        {
            return await ObserveForwardAsync(target, group,
                entity => entity is MarkdownEntity incoming &&
                    LagrangeMapper.TryReadFlashCard(incoming.Content)?.FileSetId == card.FileSetId,
                () => files.SendFlashCardAsync(card.FileSetId, target, group, cancellationToken), cancellationToken);
        }

        var resources = new List<Stream>();
        try
        {
            var sent = await context.RunMessageOperationAsync(async () =>
            {
                var chain = new MessageChain();
                foreach (var entity in source.Entities)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    switch (entity)
                    {
                        case VideoEntity video:
                            if (string.IsNullOrEmpty(video.FileUrl)) await video.Postprocess(context, source);
                            chain.Add(await LagrangeMapper.CreateVideoAsync(video.FileUrl, null, cancellationToken, resources, video.IsBubble));
                            break;
                        case ImageEntity image:
                            if (string.IsNullOrEmpty(image.FileUrl)) await image.Postprocess(context, source);
                            chain.Add(new ImageEntity(await OpenMediaAsync(image.FileUrl), image.Summary) { SubType = image.SubType });
                            break;
                        case RecordEntity record:
                            if (string.IsNullOrEmpty(record.FileUrl)) await record.Postprocess(context, source);
                            chain.Add(new RecordEntity(await OpenMediaAsync(record.FileUrl)));
                            break;
                        case ReplyEntity reply when scene != targetScene || sourcePeer != target:
                            var quoted = string.IsNullOrWhiteSpace(reply.QuotedText) ? "[引用消息]" : reply.QuotedText;
                            chain.Add(new TextEntity($"> {quoted}\n"));
                            break;
                        case MentionEntity mention when scene != targetScene || sourcePeer != target:
                            chain.Add(new TextEntity(mention.Display ?? (mention.Uin == 0 ? "@全体成员" : $"@{mention.Uin}")));
                            break;
                        case TextEntity or MentionEntity or ReplyEntity or FaceEntity or MarketFaceEntity
                            or MultiMsgEntity or LightAppEntity or MarkdownEntity:
                            chain.Add(entity);
                            break;
                        default:
                            throw LagrangeMapper.Unsupported($"单条消息转发中的 {entity.GetType().Name}");
                    }
                }

                if (chain.Count == 0) throw new InvalidOperationException("源消息没有可转发的消息段。");
                return group ? await context.SendGroupMessage(target, chain) : await context.SendFriendMessage(target, chain);
            }, cancellationToken);
            if (sent.Sequence == 0) throw new InvalidDataException("QQ 未返回转发后的消息序号。");
            session.CacheMessage(sent);
            return sent.Sequence.ToString();
        }
        finally
        {
            foreach (var resource in resources) await resource.DisposeAsync();
        }

        async Task<Stream> OpenMediaAsync(string uri)
        {
            var stream = await LagrangeMapper.OpenResourceAsync(uri, bufferHttpResponse: false, cancellationToken);
            resources.Add(stream);
            return stream;
        }
    }

    private async Task<string> ObserveForwardAsync(long target, bool group, Func<IMessageEntity, bool> match,
        Func<Task> send, CancellationToken token)
    {
        var self = session.Context.BotUin;
        var started = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var observed = new TaskCompletionSource<ulong>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Observe(BotMessage message)
        {
            if (message.Contact.Uin != self || message.Sequence == 0 || message.Time < started) return;
            var isGroup = message.Contact is BotGroupMember;
            var peer = message.Contact is BotGroupMember member ? member.Group.GroupUin : message.Receiver.Uin;
            if (isGroup == group && target == peer && message.Entities.Any(match))
                observed.TrySetResult(message.Sequence);
        }

        session.MessagePushed += Observe;
        try
        {
            await send();
            try
            {
                return (await observed.Task.WaitAsync(TimeSpan.FromSeconds(8), token)).ToString();
            }
            catch (TimeoutException)
            {
                var recent = await GetRecentAsync(target, group, token);
                foreach (var message in recent)
                {
                    session.CacheMessage(message);
                    Observe(message);
                }

                if (observed.Task.IsCompletedSuccessfully) return observed.Task.Result.ToString();
                throw new TimeoutException("QQ 已接受转发，但未查到目标消息序号。");
            }
        }
        finally
        {
            session.MessagePushed -= Observe;
        }
    }

    private async Task<string> FindGroupFileMessageAsync(long group, string fileId, CancellationToken token)
    {
        var context = session.Context;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var messages = await GetRecentAsync(group, true, token);
            foreach (var message in messages)
            {
                session.CacheMessage(message);
                if (message.Contact.Uin == context.BotUin && message.Entities.OfType<GroupFileEntity>().Any(file => file.FileId == fileId))
                    return message.Sequence.ToString();
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500), token);
        }

        throw new TimeoutException("QQ 已接受群文件，但未查到目标消息序号。");
    }

    private async Task<List<BotMessage>> GetRecentAsync(long target, bool group, CancellationToken token)
    {
        var context = session.Context;
        if (!group)
            return await context.GetRoamMessage(target, checked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds()), 10).WaitAsync(token);

        var extra = await context.FetchGroupExtra(target).WaitAsync(token);
        var end = (ulong)extra.LatestMessageSequence;
        return end == 0 ? [] : await context.GetGroupMessage(target, end > 9 ? end - 9 : 1, end).WaitAsync(token);
    }
}
