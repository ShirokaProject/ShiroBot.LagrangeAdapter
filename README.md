# ShiroBot LagrangeAdapter

基于 LagrangeV2 的进程内 QQ 适配器。

## 构建

```sh
git clone --recurse-submodules https://github.com/ShirokaProject/ShiroBot.LagrangeAdapter.git
cd ShiroBot.LagrangeAdapter
git submodule update --init --recursive
dotnet build -c Release
dotnet run --project tests/ContractChecks
node tests/DashboardChecks/login-page.cjs
```

LagrangeV2 使用 ShirokaProject 的 Lagrange.Core fork 子模块，基于官方提交 `fe789c4a23796fbc82fa09e70a9c44c8a5cf2948`，增加 .NET 11 / async2 编译配置。更新上游后需要重新构建、验证，并提交新的 gitlink：

```sh
git submodule update --remote external/LagrangeV2
```


## 配置

首次启动会保存默认配置。PC 协议可保持 `Uin = ""`、`Password = null` 使用二维码登录；AndroidPhone / AndroidPad 可使用账号密码登录，或在控制台尝试实验性的免密码短信入口。二维码保存到 keystore 同目录的 `qrcode.png`。成功后会话凭据保存到当前实例的 session_store，后续可快速登录。

```json
{
  "Uin": "",
  "Password": null,
  "Protocol": "Linux",
  "KeystorePath": null
}
```

## 能力与验证范围

具体能力与限制见 [能力与限制](docs/CAPABILITIES.md)。

## 在适配器内触发扫码

登录、二维码获取和会话保存都由适配器完成，不需要额外插件。适配器直接复用 SDK 的 `IPluginActionProvider` 契约。

启用支持适配器 Action 的宿主后：

1. 将 `AutoLogin` 设为 `false`，启动适配器实例。
2. 在 Dashboard 的适配器实例配置窗口打开“QQ 登录”，点击“获取二维码”，或在宿主控制台执行：

```text
adapter actions <实例 ID>
adapter action <实例 ID> qr-login
```

## 编译与许可证
本项目直接引用上游 [Lagrange.Core]([https://github.com/LagrangeDev/Lagrange.Core]) 源码进行开发对接。

许可证为 **GPL-3.0-or-later**，见 [LICENSE](LICENSE)，保留上游来源和作者信息。
## Android 与登录验证

在“QQ 登录”页面输入账号密码。选择 AndroidPhone 或 AndroidPad 后，自定义签名器自动使用 Yogurt Android 格式的 `/sign`、`/energy`、`/get_tlv553`；PC 继续使用 `/api/sign/sec-sign`。完整 QUA、QQ 版本与 SDK 版本来自当前 AppInfo，不再固定为内核内置的旧版本。

Android AppInfo 支持 `WtLoginPublicKey`、`WtLoginKeyVersion`、`WtLoginSsoProtocol` 和 `WtLoginProductType`。提供公钥时，登录实际使用 P-256 和指定密钥版本；`WtLoginSsoProtocol = 10` 只作用于 Android 的 `wtlogin.*`，其他请求保留原传输格式。APK 摘要兼容有符号/无符号 JSON 字节数组和 .NET Base64。Android 扫码仍不受支持。

例如使用版本签名地址（需该服务已启用对应 backend）：

```toml
protocol = "AndroidPad"
sign_server = "https://sign.shiroka.org/android/9.3.80-16750/<签名账户Token>"
sign_token = ""
fetch_app_info = true
```

也可用不含 Token 的版本地址配合 `sign_token`（Bearer）。两种鉴权方式不要同时设置。`fetch_app_info` 从同一基础地址追加 `/appinfo` 获取资料，与 `app_info` / `app_info_path` 互斥。关闭时继续使用内置资料或自定义 AppInfo；请保证其平台、QUA、版本与服务一致。

登录期间若需滑块、短信或新设备验证，页面会显示验证链接、复制按钮和对应输入框。滑块可粘贴返回的 JSON，或填写 ticket / randstr；Android 的 ticket 不要求另填 randstr。短信填写实际收到的 4–8 位验证码。验证结果只提交给当前实例、当前验证请求；过期页面不能向新的请求提交。密码不由登录页写入配置；验证成功后仍按原规则保存会话。

Android 滑块使用独立的 [Ticket helper](captcha-helper/README.md)，默认本机 `http://127.0.0.1:18130/`。可将静态页面部署到自己的 HTTPS 站点，再设置 `captcha_helper_url`；验证码组件仍来自腾讯官方。复制辅助链接到浏览器完成验证，将 Ticket/JSON 填回登录页；`terror_` 加载失败结果会被拒绝。等待验证的密码登录最长五分钟。

签名服务必须支持流程实际使用的模块：密码 `810_9`、滑块 `810_2`、短信发码的 TLV553 `810_8`、短信提交 `810_7`。服务只实现 `810_9` 时，不能据此宣称滑块或短信流程已可真实登录；适配器会保留可读错误，不用空签名冒充成功。

AndroidPhone / AndroidPad 另提供实验性的免密码短信入口：输入 QQ 号后依次检查账号、请求发码、提交验证码并换取登录票据。短期校验材料只在当前尝试内保存，结束后清理；账号不匹配、取消和超时会终止流程。此分支已通过离线状态机检查，尚未完成腾讯实际免密码短信登录验证，不能据此声称所有账号均可登录；Android 扫码和 AndroidWatch 仍未由该内核实现。

构建会在固定的 LagrangeV2 子模块上幂等应用 [登录挑战补丁](patches/lagrange-login-challenges.patch)：兼容直接返回的短信挑战、保留发码后省略的验证资料、提前建立提交等待任务并响应取消，并添加独立短信账号/发码/校验/票据交换入口。子模块 gitlink 不变，补丁源文件随本仓库管理；更新子模块若补丁冲突会停止构建。

验证包含 Android/PC HTTP 契约、两种 Token 方式、AppInfo、错误响应、验证提交与旧请求拒绝、现有宿主契约及卸载回归。离线测试不会联系腾讯，不代替真实账号登录验证。
