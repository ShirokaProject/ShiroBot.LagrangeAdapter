Lagrange.Core V2 进程内 QQ 适配器，适用于 .NET 11 / ShiroBot SDK 1.0.2。

- 支持扫码、账号密码和保存会话快速登录，二维码可输出到控制台。
- 支持自定义 AppInfo、设备名称和标准 Lagrange V2 PC 签名服务。
- 多实例会话分别保存到 config.toml 对应实例的 session_store；登录后删除二维码 PNG。
- 已接入上游现有消息、群管理、文件、事件等能力，具体范围见 CAPABILITIES.md。

下载 ShiroBot.LagrangeAdapter.dll 安装到宿主。LICENSE、配置示例与 AppInfo 示例为附加文件。

自定义登录页面和适配器 Action 需要支持相应入口的宿主 / Dashboard；仓库 integration/ 提供接入补丁。控制台自动登录不依赖自定义页面。完整程序集回收仍可能要求重启宿主。

运行库使用 .NET 11 Runtime Async V2；许可证 GPL-3.0-or-later。构建与离线回归检查通过，在线能力受账号权限、登录状态和签名服务影响。
