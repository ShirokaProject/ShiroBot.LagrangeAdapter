# Ticket helper

独立静态页面，使用腾讯官方 `TencentCaptcha` 组件完成验证，显示真实 Ticket 和 JSON。无需 Node、数据库或签名服务；不会收集密码、签名 Token 或将结果上传到自己的服务。腾讯的验证码服务仍需要联网。

本机预览：

```sh
python3 -S serve.py --bind 127.0.0.1 --port 18130
```

生产环境只需将 `index.html` 放到自己的 HTTPS 静态站点，例如 `https://captcha.example.com/`。关闭该站点的 URL 查询参数访问日志，添加 `Referrer-Policy: no-referrer` 和 `Cache-Control: no-store`。若已有 CSP，需要允许腾讯的验证码脚本、iframe 和网络请求；不能放进禁止外部脚本的 Dashboard 沙箱 iframe。

适配器配置：

```toml
captcha_helper_url = "https://captcha.example.com/"
```

页面地址可直接附加腾讯挑战的查询参数；没有参数时可粘贴验证链接。适配器会传入当前挑战，并把 `uin=0` 换为登录账号。完成后复制 Ticket 或 JSON，返回登录页提交；出现 `terror_` 时显示错误，不把它当成有效 Ticket。短信验证码仍在适配器登录页填写，不在此页面索取。

原版 Yogurt 的 helper 域名写在程序中。未改动的版本可复制其完整链接，保持查询参数不变、将域名替换为自建 helper；不会因此自动回填客户端。
