# ShiroBot.LagrangeAdapter

基于 LagrangeV2 的进程内 QQ 适配器，使用 ShiroBot.SDK **1.0.2**（.NET 11）。QQ 通用契约为 `ShiroBot.Model.QQ`，ID 在 SDK 边界使用字符串。

## 构建

```sh
git clone --recurse-submodules https://github.com/ShirokaProject/ShiroBot.LagrangeAdapter.git
cd ShiroBot.LagrangeAdapter
git submodule update --init --recursive
dotnet build -c Release
dotnet run --project tests/ContractChecks
```

LagrangeV2 使用 ShirokaProject 的 Lagrange.Core fork 子模块，基于官方提交 `fe789c4a23796fbc82fa09e70a9c44c8a5cf2948`，增加 .NET 11 / async2 编译配置。更新上游后需要重新构建、验证，并提交新的 gitlink：

```sh
git submodule update --remote external/LagrangeV2
```

Release 使用 SDK 自带打包器合并协议依赖。将 `bin/Release/net11.0/ShiroBot.LagrangeAdapter.dll` 安装到 ShiroBot 的适配器目录；SDK 和内置 Model 由宿主提供。宿主至少使用 1.0.2。参考[适配器文档](https://docs.shiroka.org/adapter/)。

## 配置

首次启动会保存默认配置。建议保持 `Uin = 0`、`Password = null` 使用二维码登录；二维码保存到 keystore 同目录的 `qrcode.png`。首次扫码后保存 keystore，后续尝试恢复登录。

```json
{
  "Uin": 0,
  "Password": null,
  "Protocol": "Linux",
  "KeystorePath": null
}
```

`KeystorePath` 留空时使用适配器配置目录；相对路径也以该目录解析。密码登录需要同时设置 Uin 和 Password。滑块验证目前仅输出验证地址，不支持交互提交。keystore 和密码包含登录凭据，不应提交到仓库。

## 能力与验证范围

支持通用消息收发、查询及历史记录、近期消息撤回、群与成员查询、好友查询，以及协议端已提供的 QQ 群名/名片/头衔、戳一戳、反应、申请审批、文件上传下载等扩展。未实现的能力抛出 `NotSupportedException`。QQ 扩展通过 `GetExtension<IQ*Api>()` 获取，登录后可用。

消息缓存按会话、群/私聊及序号隔离，最多缓存 4096 条。通用历史记录的 `beforeMessageId` 为排他边界；QQ 历史接口的 cursor 为下一页起始序号。好友历史首页已接入 GetRoamMessage，无需提供边界。未缓存消息可查询后再回复/撤回。具体能力与限制见 [CAPABILITIES.md](CAPABILITIES.md)。

本地检查覆盖 SDK 接口签名匹配、启动前服务获取、重复停止、预取消调用、跨群序号冲突和模型身份映射。不包含真实 QQ 登录、协议请求或宿主热重载的在线测试。当前上游 EventInvoker 使用静态委托缓存，因此不能据此保证程序集能够完全卸载；适配器停止时会释放连接并清理自身会话。

## 自定义 AppInfo 和签名服务

在适配器配置中设置：

```json
{
  "AutoLogin": false,
  "Protocol": "Linux",
  "AppInfoPath": "appinfo.json",
  "SignServer": "http://127.0.0.1:8080/sign"
}
```

这是配置字段示意；宿主配置文件为 TOML 时使用对应键值。`AppInfoPath` 相对适配器配置目录解析，文件允许只覆盖部分字段，例如：

```json
{
  "CurrentVersion": "3.2.26-46494",
  "SubAppId": 537345891,
  "AppClientVersion": 46494
}
```

其余字段继承所选协议的内置 AppInfo，`SdkInfo` 也支持局部覆盖。也可将 JSON 文本放在字符串字段 `AppInfo` 中；它与 `AppInfoPath` 互斥。`ApkSignatureMd5` 按 System.Text.Json 约定使用 Base64 字符串。未知字段、空字段和错误类型会导致启动失败。

`SignServer` 是完整 HTTP(S) POST 签名端点，不会自动追加路径。请求为 `{cmd, seq, src}`，src 为十六进制正文；响应支持 `{value:{sign,token,extra}}` 或 `{code:0,data:{sign,token,extra}}`，三个签名字段为十六进制。超时为 30 秒。签名命令列表与当前固定的上游 PC provider 一致；此配置不替代 Android 特有的 energy 等签名接口。

## 在适配器内触发扫码

登录、二维码获取和会话保存都由适配器完成，不需要额外插件。适配器直接复用 SDK 的 `IPluginActionProvider` 契约。

启用支持适配器 Action 的宿主后：

1. 将 `AutoLogin` 设为 `false`，启动适配器实例。
2. 在 Dashboard 的适配器实例详情页点击“扫码登录”，或在宿主控制台执行：

```text
adapter actions <实例 ID>
adapter action <实例 ID> qr-login
```

二维码生成后返回 PNG 路径，并输出到日志。扫码后在手机确认；再次执行会关闭旧连接并重新获取二维码。此操作忽略密码和已有票据，不预先删除磁盘凭据，登录成功后保存新凭据。

旧版宿主没有适配器 Action 分发入口，需更新宿主；自动登录仍可使用。自定义 Dashboard 页面的现有契约属于插件注册上下文，目前尚未向适配器注入，不能把“Action 已支持”理解为“自定义页面也已接通”。

## 编译与许可证

适配器和协议运行库使用 .NET SDK 11.0.100 / net11.0，并通过 `runtime-async=on` 启用 Runtime Async V2（async2），与宿主设置一致。Roslyn 源码生成器是编译工具，保留 netstandard2.0 以兼容编译器加载，不作为运行库打包。

协议子模块使用 [ShirokaProject/Lagrange.Core](https://github.com/ShirokaProject/Lagrange.Core) 的 `net11-async2` 分支，基于上游 fe789c4。更新依赖时以子模块 gitlink 固定提交。

许可证为 **GPL-3.0-or-later**，见 [LICENSE](LICENSE)，保留上游来源和作者信息。
