# 宿主 Action 接入

这些补丁让适配器直接复用 SDK 的 IPluginActionProvider，不需要伴随插件：

- shirobot-adapter-actions.patch：适配器分发、鉴权 API、控制台命令和生命周期回归测试。
- dashboard-adapter-actions.patch：适配器详情页 Action 按钮及 Web 页面入口，复用现有沙箱桥接、渲染和鉴权数据模型。

在各自仓库使用 `git apply --check <补丁路径>` 检查后应用。宿主和 Dashboard 必须同时更新才能从详情页使用；仅更新宿主时可先用控制台 `adapter action <实例 ID> qr-login`。

本次已在本地 ShiroBot 源码应用并通过 --adapter-actions 验证，Dashboard 构建和 69 项既有测试通过。补丁不包含用户目录中原有的更新、发布或文档改动。自定义 Dashboard 页面使用共享页面 owner、注册 scope 与生命周期管理；本补丁已支持沙箱自定义页面。


适配器注册页面：在 GetExtension<T>() 中返回 Action<IPluginDashboard> 注册回调，并实现 ILifetimeAwareAdapter。宿主为每次启动创建新页面作用域，调用回调后再 StartAsync；卸载时等待页面请求并注销登记。

验证新增 `--adapter-login-page <适配器 DLL>`，检查真实适配器嵌入页面、桥接状态、输入校验和停止后禁止调用。完整 ALC 回收仍可能要求重启宿主，不能把页面注销等同于程序集回收。
