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

首次启动会保存默认配置。建议保持 `Uin = ""`、`Password = null` 使用二维码登录；二维码保存到 keystore 同目录的 `qrcode.png`。首次扫码后保存 keystore，后续尝试恢复登录。

```json
{
  "Uin": "",
  "Password": null,
  "Protocol": "Linux",
  "KeystorePath": null
}
```

`KeystorePath` 留空时使用适配器配置目录；相对路径也以该目录解析。密码登录需要同时设置 Uin 和 Password。滑块验证目前仅输出验证地址，不支持交互提交。keystore 和密码包含登录凭据，不应提交到仓库。

## 能力与验证范围

支持通用消息收发、查询及历史记录、近期消息撤回、群与成员查询、好友查询，以及协议端已提供的 QQ 群名/名片/头衔、戳一戳、反应、申请审批、文件上传下载等扩展。未实现的能力抛出 `NotSupportedException`。QQ 扩展通过 `GetExtension<IQ*Api>()` 获取，登录后可用。

消息缓存按会话、群/私聊及序号隔离，最多缓存 4096 条。通用历史记录的 `beforeMessageId` 为排他边界；QQ 历史接口的 cursor 为下一页起始序号。好友历史首页已接入 GetRoamMessage，无需提供边界。未缓存消息可查询后再回复/撤回。具体能力与限制见 [CAPABILITIES.md](CAPABILITIES.md)。

本地检查覆盖 SDK 接口签名匹配、启动前服务获取、重复停止、预取消调用、跨群序号冲突和模型身份映射。不包含真实 QQ 登录、协议请求或宿主热重载的在线测试。fork 已将 EventInvoker 的委托缓存改为实例作用域并在 Dispose 时清空；当前完整程序集回收检查仍可能要求重启宿主。适配器停止后会注销页面并清理自身会话。

## 自定义 AppInfo 和签名服务

在适配器配置中设置：

```json
{
  "AutoLogin": false,
  "Protocol": "Linux",
  "AppInfoPath": "appinfo.json",
  "SignServer": "http://127.0.0.1:8080",
  "SignToken": ""
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

签名服务统一使用 Lagrange V2 PC 标准：`sign_server` 填基础地址，自动追加 `/api/sign/sec-sign`。请求携带配置中的 UIN（JSON 数字）、command、seq、body（HEX）、设备 GUID（HEX），QUA 按平台 WIN/MAC/LNX 和 AppInfo.CurrentVersion 生成。响应要求 code=0 和 value 对象内的 sec_sign、sec_token、sec_extra 字符串，允许空 HEX。

支持 `sign_token`（Bearer）、`sign_proxy_url` 和 `sign_timeout_seconds`。`sign_server` 留空使用上游内置服务，此时不能单独设置 token 或代理。
完整且带注释的宿主 TOML 示例见 [examples/config.toml](examples/config.toml)，完整 Linux AppInfo 模板见 [examples/appinfo.example.json](examples/appinfo.example.json)。复制模板并将 `app_info_path` 指向你的 JSON 文件即可使用；AppInfo 与签名服务版本需要匹配。

已接入 Milky 对应的服务器与登录参数：`auto_reconnect`、`auto_re_login`、`use_ipv6_network`、`get_optimum_server`；另提供 `log_level`、`highway_chunk_size` 和 `highway_concurrent`。这些字段会直接传入 BotConfig。适配器声明强类型配置契约，Dashboard 可读取完整配置 schema，变更按重启连接方式生效。

Milky 的 HTTP/WebSocket/SSE/WebHook 配置属于 Milky 传输层，进程内适配器不需要。Milky 的在线验证码解析与终端 ASCII 二维码选项目前没有移植，不会展示成无效开关。

## 在适配器内触发扫码

登录、二维码获取和会话保存都由适配器完成，不需要额外插件。适配器直接复用 SDK 的 `IPluginActionProvider` 契约。

启用支持适配器 Action 的宿主后：

1. 将 `AutoLogin` 设为 `false`，启动适配器实例。
2. 在 Dashboard 的适配器实例配置窗口打开“QQ 登录”，点击“获取二维码”，或在宿主控制台执行：

```text
adapter actions <实例 ID>
adapter action <实例 ID> qr-login
```

二维码生成后返回 PNG 路径，并输出到日志。扫码后在手机确认；再次执行会关闭旧连接并重新获取二维码。此操作忽略密码和已有票据，不预先删除磁盘凭据，登录成功后保存新凭据。

宿主更新后，在运行中的适配器实例点击“配置”，再从配置窗口里的自定义页面入口打开“QQ 登录”：查看二维码、扫码状态，或临时输入 QQ 账号与密码。页面密码不写入配置，成功后仍保存 keystore。遇到滑块或短信验证时，页面显示验证地址并提供 ticket/randstr 或短信验证码提交入口。

配置模型的 19 个字段都有中文名称、说明、分组、下拉选项与必要的数值边界；密码和 Token 使用密码输入框。

页面直接复用 IPluginDashboard.AddWebPage、沙箱桥接和鉴权。适配器通过 GetExtension<Action<IPluginDashboard>>() 暴露注册回调，宿主在注入 Lifetime 后、StartAsync 前执行；页面登记随 Lifetime 注销，无需伴随插件。integration/ 中包含宿主和 Dashboard 的共用页面接入补丁。旧宿主没有这些入口，需要更新并重启。

## 编译与许可证

适配器和协议运行库使用 .NET SDK 11.0.100 / net11.0，并通过 `runtime-async=on` 启用 Runtime Async V2（async2），与宿主设置一致。Roslyn 源码生成器是编译工具，保留 netstandard2.0 以兼容编译器加载，不作为运行库打包。

协议子模块使用 [ShirokaProject/Lagrange.Core](https://github.com/ShirokaProject/Lagrange.Core) 的 `net11-async2` 分支，基于上游 fe789c4。更新依赖时以子模块 gitlink 固定提交。

许可证为 **GPL-3.0-or-later**，见 [LICENSE](LICENSE)，保留上游来源和作者信息。


QQ 账号配置使用字符串（`uin = "123456789"`），界面显示普通文本框；旧的整数 uin 会在加载前迁移，0 转为空值，其他字段保持不变。

兼容旧版平铺 AppInfo：MiscBitmap、MainSigMap、SubSigMap、WtLoginSdk 等字段会转入当前 V2 的 SdkInfo。NTLoginType 在当前 V2 无对应字段，会明确警告后忽略；建议使用完整 V2 模板。显式 SdkInfo 字段优先于旧版平铺字段。


登录失败会在登录页保留具体原因，不再把预期的登录失败抛成页面异常。签名服务 HTTP 错误会显示状态码与错误说明，并隐藏配置中的密码和 Token。客户端平台必须和 AppInfo.Os 一致，不能把 Mac AppInfo 覆盖进 Linux 默认配置后继续登录。
