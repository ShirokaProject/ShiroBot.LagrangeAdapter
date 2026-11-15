# 设备 Token relay

`token_relay_enabled = true` 默认开启。Mac 协议复用当前签名服务地址、签名 Token 和代理：主动通过 SSE 接收两个固定 O3 任务，使用当前在线 QQ 会话发送，再通过 HTTPS POST 回传结果。无需公网 IP、入站端口、额外密钥或 SSH 隧道。设为 `false` 关闭。

服务需支持 `sse-http-v1`；旧服务返回 404/422 时退避重试，普通签名继续使用原流程。不支持此功能的 backend 不建立 relay。HTTP 仅允许本机测试，远端必须 HTTPS。原有 `token_relay_port`、`token_relay_key_file` 不再使用。

只允许 `SsoEstablishShareKey` / `SsoSecureAccess`，校验当前 UIN、GUID、任务 ID、命令和大小，执行前后再次确认登录状态；同一任务不会重复执行。切换账号、退出、卸载会取消接收；任务超时或断线失效。网站禁用账号、撤销 Token、解绑 UIN 同样阻止任务和回传。

服务端自动按网站账号、签名 UIN、GUID、backend 登记设备，不需要管理员写设备清单。客户端仅上报已知的本机资料；SDK 持久 GUID 作为安装标识，不编造未掌握的硬件或检测字段。Token 和会话密钥仍按设备隔离、内存缓存，服务重启后按需重建。真实 Token 签发结果取决于腾讯响应，离线协议联调不能代替真实登录验证。

私有服务需透传 `Authorization`，对 `/relay/events` 禁用响应缓冲，允许 `text/event-stream` 并保留至少 40 秒流等待；连接约 25 秒轮换，客户端自动重连。任务与结果分别走 SSE / HTTP POST，签名等待不会占用 SSE 工作线程。旧客户端没有 relay 时继续现有空 sec_token 行为。
