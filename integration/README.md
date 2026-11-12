# 宿主 Action 接入

这些补丁让适配器直接复用 SDK 的 IPluginActionProvider，不需要伴随插件：

- shirobot-adapter-actions.patch：适配器分发、鉴权 API、控制台命令和生命周期回归测试。
- dashboard-adapter-actions.patch：适配器详情页 Action 按钮，复用现有 Action 返回数据模型。

在各自仓库使用 `git apply --check <补丁路径>` 检查后应用。宿主和 Dashboard 必须同时更新才能从详情页使用；仅更新宿主时可先用控制台 `adapter action <实例 ID> qr-login`。

本次已在本地 ShiroBot 源码应用并通过 --adapter-actions 验证，Dashboard 构建和 69 项既有测试通过。补丁不包含用户目录中原有的更新、发布或文档改动。自定义 Dashboard 页面尚未增加适配器注册入口；本补丁只提供 Action。
