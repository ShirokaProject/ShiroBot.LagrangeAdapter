Lagrange.Core V2 进程内 QQ 适配器，适用于 .NET 11 / ShiroBot SDK 1.0.3。

- 支持扫码、账号密码和保存会话快速登录，二维码可输出到控制台。
- 支持自定义 AppInfo、设备名称、PC 签名及 AndroidPhone / AndroidPad 的三接口签名服务；可从签名服务获取 AppInfo。
- 登录页支持滑块结果、短信二次验证和设备验证链接。独立免密码短信登录为实验入口，尚未完成真实登录验证；Android 扫码不支持。验证流程需要签名服务支持对应模块。
- 多实例会话分别保存到 config.toml 对应实例的 session_store；登录后删除二维码 PNG。
- 已接入上游现有消息、群管理、文件、事件等能力，具体范围见 docs/CAPABILITIES.md。

下载 ShiroBot.LagrangeAdapter.zip，解压后将 ShiroBot.LagrangeAdapter.dll 安装到宿主。配置与 AppInfo 示例见仓库 docs/examples/。

自定义登录页面和适配器 Action 需要支持相应入口的宿主 / Dashboard；仓库 docs/integration/ 提供接入补丁。控制台自动登录不依赖自定义页面。完整程序集回收仍可能要求重启宿主。

运行库使用 .NET 11 Runtime Async V2；许可证 GPL-3.0-or-later。构建与离线回归检查通过，在线能力受账号权限、登录状态和签名服务影响。

本版修复宿主沙箱中登录、短信和滑块按钮无法提交的问题，并支持输入框回车。Android 验证仅需 Ticket，也可直接粘贴结果 JSON；默认使用 https://ticket-helper.shiroka.org/，无需本机 helper 服务。Android 与 Mac 的设备配置 relay 使用现有签名地址的 SSE/HTTP 通道。
