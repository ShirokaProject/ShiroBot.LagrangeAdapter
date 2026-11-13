using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Lagrange.Core;
using Lagrange.Core.Common.Interface;
using ShiroBot.Model.QQ;

namespace ShiroBot.LagrangeAdapter;

internal sealed class LagrangeGroupWeb(BotContext context)
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(25) };
    internal static string Csrf(string key)
    {
        uint hash = 5381;
        foreach (var c in key) hash = unchecked(hash * 33 + c);
        return (hash & 0x7fffffff).ToString(CultureInfo.InvariantCulture);
    }
    private async Task<(string Key, string Cookie)> Credentials(CancellationToken token)
    {
        var key = Encoding.UTF8.GetString(context.Keystore.WLoginSigs.SKey);
        if (key.Length == 0) throw new InvalidOperationException("当前会话缺少 SKey，无法访问群 Web API。");
        var keys = await context.FetchCookies("qun.qq.com").WaitAsync(token);
        if (!keys.TryGetValue("qun.qq.com", out var pskey) || pskey.Length == 0)
            throw new InvalidOperationException("未获取到群 Web API 的 p_skey。");
        return (key, $"uin=o{context.BotUin}; p_uin=o{context.BotUin}; skey={key}; p_skey={pskey}");
    }
    private async Task<JsonObject> Request(string url, Dictionary<string, string> values, bool post, CancellationToken token,
        Func<string, HttpContent>? content = null)
    {
        var auth = await Credentials(token); values["bkn"] = Csrf(auth.Key);
        using var query = new FormUrlEncodedContent(values);
        using var request = new HttpRequestMessage(post ? HttpMethod.Post : HttpMethod.Get,
            post ? url : url + "?" + await query.ReadAsStringAsync(token));
        request.Headers.Add("Cookie", auth.Cookie);
        request.Headers.Add("User-Agent", "Dalvik/2.1.0 (Linux; U; Android 7.1.2; PCRT00 Build/N2G48H)");
        if (post) request.Content = content is null ? new FormUrlEncodedContent(values) : content(values["bkn"]);
        using var response = await Client.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"群 Web API HTTP {(int)response.StatusCode}。");
        var text = await response.Content.ReadAsStringAsync(token);
        if (text.Length > 8 * 1024 * 1024) throw new InvalidDataException("群 Web API 响应过长。");
        var result = JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("群 Web API 响应无效。");
        foreach (var field in new[] { "ec", "retcode", "ret_code", "code" })
            if (result[field] is { } value && long.TryParse(value.ToString(), out var n) && n != 0)
                throw new InvalidOperationException($"群 Web API 错误 {n}: {result["em"] ?? result["retmsg"] ?? result["msg"]}");
        return result;
    }
    internal async Task<IReadOnlyList<QGroupAnnouncement>> Announcements(string group, CancellationToken token)
    {
        var data = await Request("https://web.qun.qq.com/cgi-bin/announce/get_t_list", new()
            { ["qid"] = group, ["ft"] = "23", ["ni"] = "1", ["n"] = "20", ["i"] = "1", ["log_read"] = "1", ["platform"] = "1", ["s"] = "-1" }, false, token);
        return ParseAnnouncements(data, group);
    }
    internal static IReadOnlyList<QGroupAnnouncement> ParseAnnouncements(JsonObject data, string group) =>
        new[] { data["feeds"], data["inst"] }.OfType<JsonArray>().SelectMany(a => a).OfType<JsonObject>().Select(item => new QGroupAnnouncement
        {
            GroupId = group, AnnouncementId = Text(item, "fid"), UserId = Text(item, "u"),
            Time = DateTimeOffset.FromUnixTimeSeconds(Number(item, "pubt")),
            Content = WebUtility.HtmlDecode(Text(item["msg"], "text")),
            ImageUrl = item["msg"]?["pics"] is JsonArray pics && pics.FirstOrDefault() is { } pic && Text(pic, "id") is { Length: > 0 } id
                ? $"https://gdynamic.qpic.cn/gdynamic/{Uri.EscapeDataString(id)}/0" : null
        }).ToArray();
    internal async Task Announce(string group, string text, string? imageUri, CancellationToken token)
    {
        var values = new Dictionary<string, string> { ["qid"] = group, ["text"] = text, ["pinned"] = "0", ["type"] = "1",
            ["settings"] = "{\"is_show_edit_card\":0,\"tip_window_type\":1,\"confirm_required\":1}" };
        if (!string.IsNullOrEmpty(imageUri))
        {
            await using var stream = await LagrangeMapper.OpenResourceAsync(imageUri);
            using var buffer = new MemoryStream(); await stream.CopyToAsync(buffer, token);
            if (buffer.Length > 20 * 1024 * 1024) throw new ArgumentException("公告图片不能超过 20 MiB。");
            var bytes = buffer.ToArray();
            var upload = await Request("https://web.qun.qq.com/cgi-bin/announce/upload_img", new(), true, token, bkn =>
            {
                var form = new MultipartFormDataContent { { new StringContent(bkn), "bkn" }, { new StringContent("troopNotice"), "source" }, { new StringContent("0"), "m" } };
                var file = new ByteArrayContent(bytes); file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                form.Add(file, "pic_up", "announcement.png"); return form;
            });
            var info = JsonNode.Parse(WebUtility.HtmlDecode(Text(upload, "id"))) ?? throw new InvalidDataException("公告图片上传结果无效。");
            values["pic"] = Text(info, "id"); values["imgWidth"] = Text(info, "w"); values["imgHeight"] = Text(info, "h");
        }
        var result = await Request("https://web.qun.qq.com/cgi-bin/announce/add_qun_notice", values, true, token);
        if (Text(result, "new_fid").Length == 0) throw new InvalidDataException("服务器未返回新公告 ID。");
    }
    internal Task<JsonObject> Delete(string group, string id, CancellationToken token) =>
        Request("https://web.qun.qq.com/cgi-bin/announce/del_feed", new() { ["fid"] = id, ["qid"] = group, ["ft"] = "23", ["op"] = "1" }, false, token);
    internal async Task<(IReadOnlyList<QEssenceMessage> Messages, bool IsEnd)> Essence(string group, int index, int size, CancellationToken token)
    {
        var data = await Request("https://qun.qq.com/cgi-bin/group_digest/digest_list", new() { ["random"] = "7800", ["X-CROSS-ORIGIN"] = "fetch", ["group_code"] = group,
            ["page_start"] = index.ToString(CultureInfo.InvariantCulture), ["page_limit"] = size.ToString(CultureInfo.InvariantCulture) }, false, token);
        return ParseEssence(data, group);
    }
    internal static (IReadOnlyList<QEssenceMessage> Messages, bool IsEnd) ParseEssence(JsonObject response, string group)
    {
        var data = response["data"] ?? throw new InvalidDataException("精华消息响应缺少 data。");
        var messages = (data["msg_list"] as JsonArray ?? []).OfType<JsonObject>().Select(item => new QEssenceMessage
        {
            GroupId = group, MessageId = Text(item, "msg_seq"), MessageTime = DateTimeOffset.FromUnixTimeSeconds(Number(item, "sender_time")),
            SenderId = Text(item, "sender_uin"), SenderName = WebUtility.HtmlDecode(Text(item, "sender_nick")),
            OperatorId = Text(item, "add_digest_uin"), OperatorName = WebUtility.HtmlDecode(Text(item, "add_digest_nick")),
            OperationTime = DateTimeOffset.FromUnixTimeSeconds(Number(item, "add_digest_time")),
            Segments = (item["msg_content"] as JsonArray ?? []).OfType<JsonObject>().Select(Segment).OfType<QIncomingSegment>().ToArray()
        }).ToArray();
        return (messages, Text(data, "is_end") is "true" or "1");
    }
    private static QIncomingSegment? Segment(JsonObject item) => Number(item, "msg_type") switch
    {
        1 => new QIncomingText(WebUtility.HtmlDecode(Text(item, "text"))),
        2 => new QIncomingFace(Text(item, "face_index"), false),
        3 => new QIncomingImage("", Text(item, "image_url")),
        4 => new QIncomingVideo("", Text(item, "file_thumbnail_url")),
        _ => null
    };
    private static string Text(JsonNode? item, string key) => item?[key]?.ToString() ?? "";
    private static long Number(JsonNode? item, string key) => long.TryParse(Text(item, key), out var n) ? n : 0;
}
