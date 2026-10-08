# 轻阅独立入口部署

这是可部署配置，尚未上线新网址。需要自己的域名和可运行 Docker Compose 的 Linux 服务器。

## 原理

页面和资源在自己的服务器提供；浏览器仅向同域 /api/sync/* 与 /api/mail/* 发请求。服务器通过 HTTPS 将限定接口转到现有轻阅后台，沿用配对数据库与发件授权，不在浏览器加载 AppDeploy SDK。服务器自身必须能连接 api-v2.appdeploy.ai。

此方案是独立入口与接口网关，后台数据仍在原平台，不是完整后台迁移。没有开放任意 URL 代理，不转发用户 Cookie，不记录请求正文，不接受邮箱授权码。

可以考虑香港节点并实测国内运营商线路；香港部署不能保证所有网络始终可达。中国大陆服务器需要先完成服务商要求的备案。把域名直接 CNAME 到 AppDeploy 仍会使用原服务器。

## 构建和部署

在手机项目根目录执行 npm ci，再执行 npm run build:standalone。
把 dist-standalone 的内容复制到本目录的 dist/；交付包已包含构建好的 dist/。

上传此目录到服务器，例如 /opt/qingyue/。域名 A 记录指向公网 IPv4，只有实际支持 IPv6 时才设置 AAAA。放行 TCP 80 / 443。

执行：
```sh
cp .env.example .env
# 编辑 .env，把 DOMAIN 设置成自己的真实域名
docker compose up -d
```

Caddy 自动为真实域名申请 HTTPS。iPhone 分享和文件 API 需要安全连接，不要改用裸 HTTP 公网入口。

## 新域名的记录与配对

新域名和旧网站的本机存储隔离，旧书架、电子书、想读不能自动复制。先在旧网站另存重要 EPUB，在新网站导入。

电脑版生成的配对链接可能仍指向旧网址。复制完整链接，在新网页设置中粘贴“另一台设备的配对链接”，即可连接相同工作区，沿用已有发件授权。请勿公开配对链接。

## 上线检查与更新

在国内手机关闭代理后，用 Wi-Fi 和移动数据分别访问，检查首页、示例修复、书架、同步和邮箱连接。不要用重复寄信判断网络状态。
/healthz 只表示入口进程存活，不表示后台或国内线路已验证。
没有配置自动重试发信请求，避免重复邮件。

更新时保留旧 dist/ 备份，再上传新资源并 docker compose restart。不要删除证书数据卷。旧 AppDeploy 入口保留，可以回退。

Z-Library、koz.moe、Amazon 为外部网站，各自的可访问性不会因轻阅换入口而改变。新授权的安全提交页仍由 AppDeploy 提供，也受该平台网络可达性影响。

参考：
- https://caddyserver.com/docs/automatic-https
- https://caddyserver.com/docs/caddyfile/directives/reverse_proxy
- https://cloud.tencent.com/document/product/243/19630
