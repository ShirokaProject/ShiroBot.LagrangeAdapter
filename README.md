# ShiroBot LagrangeAdapter

基于 LagrangeV2 的进程内 QQ 适配器。

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


## 配置

首次启动会保存默认配置。建议保持 `Uin = ""`、`Password = null` 使用二维码登录；二维码保存到 keystore 同目录的 `qrcode.png`。首次扫码后把会话凭据保存到当前实例的 session_store，后续可快速登录。

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