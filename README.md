# ShiroBot LagrangeAdapter

基于 LagrangeV2 的进程内 QQ 适配器。

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
本项目直接引用上游 [Lagrange.Core](https://github.com/LagrangeDev/Lagrange.Core) 源码进行开发对接。

版权均归 LagrangeDev 所有，如存在侵权或不当之处，请联系删除。

许可证为 **GPL-3.0-or-later**，见 [LICENSE](LICENSE)，保留上游来源和作者信息。
