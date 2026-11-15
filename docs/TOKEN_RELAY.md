# 可选设备 Token relay

默认开启，不改变现有签名接口。当前仅支持与 Mac 7.0.3-54395 backend 匹配的 `MacOs` 实例；其他平台忽略 relay 配置。

## 适配器配置

```toml
protocol = "MacOs"
token_relay_enabled = true
token_relay_port = 0
token_relay_key_file = ""
```

默认自动分配本机端口，并在实例 keystore 路径旁生成 `.relay.key` 独立随机密钥及 `.relay.json` 接入信息文件（均为 `0600`）；后者只含 endpoint 和密钥文件路径，不含密钥。卸载时删除接入信息，保留密钥供下次启动复用。多实例端口、默认密钥分别独立。

固定隧道可指定非零端口。自定义密钥路径相对于实例配置文件目录；缺失时自动创建，已有密钥必须为 32–256 字符 URL-safe 串，Unix 权限不能向组或其他用户开放。设 `token_relay_enabled = false` 关闭，不创建 relay 文件或监听器。

只监听 `127.0.0.1`。`POST /debug/shiroSignToken` 使用 `Authorization: Bearer <relay密钥>`，仅允许以下命令：

- `trpc.o3.ecdh_access.EcdhAccess.SsoEstablishShareKey`
- `trpc.o3.ecdh_access.EcdhAccess.SsoSecureAccess`

请求为 `{"command":"...","body":"HEX","uin":"QQ号","guid":"32位HEX"}`；响应为 `{"retcode":0,"body":"HEX","uin":"QQ号","guid":"32位HEX"}`。校验当前在线账号及实际 keystore GUID；返回前再次校验，避免账号切换后返回旧响应。拒绝浏览器 Origin、错误鉴权、其他命令及超限正文。每实例只允许一个在途请求，HTTP 等待上限 10 秒；卸载停止监听并取消等待。底层 SDK 的网络请求仍由 SDK 的超时和 Context.Dispose 收尾。

## 生产签名端

Mac backend 的 `native_token_file` 指向私有 JSON 文件。初始内容：

```json
{"devices":[]}
```

这表示能力已配置但没有接入任何实例，不会请求 relay。没有匹配设备、客户端不支持、离线、隧道失败或响应无效时，继续使用现有空 `sec_token` 回退；有尚未过期的旧 Token 时保留到实际过期，失败后按既有退避处理。

后续接入某个实例需管理员添加一条记录，并重启签名服务加载（不是客户端自动注册）：

```json
{"devices":[{
  "owner_uin":"签名网站登录账号",
  "uin":"该账号绑定的签名QQ号",
  "guid":"该实例实际keystore的32位HEX GUID",
  "installation":"真实设备资料中的安装标识",
  "features_file":"/opt/shiro-sign/device-config/实际资料.json",
  "relay_url":"http://127.0.0.1:18145/debug/shiroSignToken",
  "relay_token_file":"/opt/shiro-sign/device-config/relay.key",
  "dynamic_fields":"off"
}]}
```

配置、资料和密钥文件均需 `0600`。`features_file` 为 `[{"id":1,"value":"安装标识"}, ...]`，ID 1 必须与 `installation` 相同；只提供核实过的资料，不能把示意值投入实际申请。`owner_uin` 是网站 Token 所属账号，可能不同于签名 QQ。GUID 更换后须同步登记；不会自动复用旧设备 Token。

远端实例使用管理员管理的 SSH 隧道，使 UK 的 loopback 端口转发到实例所在机器的 loopback；签名器只接受固定 loopback URL，不开放公网回调或任意 SSO 转发。断开隧道即可停止转发。

当前验证为离线/loopback 测试，未据此确认真实 Token 签发或登录。首次刷新仍是同步 HTTP 链路，多个设备同时冷启动可能触发超时退避；不应把本次接入当作刷新调度已完成重构。
