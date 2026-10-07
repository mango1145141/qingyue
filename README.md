<p align="center"><img src="src/Qingyue.Desktop/Assets/KindleIcon.png" width="96" alt="轻阅图标"></p>

# 轻阅 · Qingyue

让好书，轻松抵达。

轻阅是 Windows 桌面端的 EPUB 检查、修复与 Kindle 发送工具，采用蓝灰色极简界面、封面翻页与弹簧按钮动效。

**当前版本：2.1.5 · Windows x64 · .NET 10 / WPF**

[![Windows build](https://github.com/mango1145141/qingyue/actions/workflows/windows.yml/badge.svg)](https://github.com/mango1145141/qingyue/actions/workflows/windows.yml)

## 手机网页版

[在 iPhone 打开轻阅](https://kindle-zftrvo.v2.appdeploy.ai/) · 手机版 1.3

简洁首页，不含「为你推荐」；保留书籍／漫画搜索、EPUB 修复、邮箱／分享发送，书架、队列、想读各自独立。源码与边界详见 [手机项目说明](web/Qingyue.Mobile/README.md)。Safari 可添加到主屏幕。

## 下载和开始使用

在本仓库的 **[Releases](https://github.com/mango1145141/qingyue/releases)** 页面下载 `qingyue-v版本号-win-x64.zip`，解压并运行 `轻阅.exe`。发行包包含 .NET 运行时；网页功能需要 Microsoft Edge WebView2 Runtime。

1. 在设置中保存发件邮箱和 Kindle 接收邮箱，并首次连接发件账号。
2. 拖入 EPUB，或点击“选择书籍”，等待检查与修复。
3. 使用邮件发送，或连接 Amazon Send to Kindle 网页发送较大的文件。
4. 首页可搜索书籍、漫画，管理想读清单，并浏览个性化推荐。

“邮件已提交”或“网页已提交”表示发送服务接受请求；实际到达 Kindle 仍取决于亚马逊处理和设备同步。

## 功能

- EPUB 图片资源清单检查与重新打包；保留原书，另存修复文件。
- 邮箱连接与自动发送；较大文件通过 Amazon 网页流程提交，需登录时给出提示。
- Z-Library 书籍搜索、koz.moe 漫画搜索及本机登录会话保留。
- 书籍和漫画共同参与个性化推荐，依据搜索记录、题材、作者、想读与“不感兴趣”排序。
- 每批 12 本连续加载；封面悬停翻页显示作者与简介。
- 我的书架、发送队列和想读清单采用独立入口及窗口；书架与队列位于首页导入区上方，想读清单位于推荐区。
- EPUB 预览、记录管理、每日一句及界面偏好。
- 通过现有轻阅手机服务配对同步邮箱地址和偏好。

## 运行条件和当前边界

- Windows 64 位桌面环境；从源码构建需要 .NET 10 SDK。
- 此个人版本的书籍下载、修复和推荐缓存默认使用 `G:\chatgpt`。其他电脑需有可写的 G 盘目录；当前尚未提供全部路径的统一迁移设置。
- 邮件附件发送与网页大文件发送是不同流程；网页流程上限按当前实现为 200 MiB，实际接收限制以服务端为准。
- EPUB 为自动处理格式；缺失的原始图片无法通过补登记恢复，WebP 当前不会自动转换。
- 网站搜索、登录、验证码、下载额度与收件结果由对应网站决定。
- 手机页面与同步服务由现有 AppDeploy 项目运行；本仓库的 `web/Qingyue.Mobile` 包含手机前后端源码，运行依赖平台 SDK／数据库／环境授权。仅构建桌面项目不会生成手机服务。

## 源码构建

```powershell
dotnet restore .\src\Qingyue.Desktop\EpubKindleFix.csproj
dotnet build .\Qingyue.slnx -c Release --no-restore
pwsh -File .\scripts\publish.ps1
```

输出位于 `artifacts/`。也可以在 Visual Studio 中打开 `Qingyue.slnx`。

## 仓库结构

```text
src/Qingyue.Desktop/     桌面源码、XAML 和应用资源
web/Qingyue.Mobile/     手机网页前端、平台后端与验收说明
docs/                   用户说明、构建和更新指南
docs/history/           原始版本说明和书目来源
licenses/third-party/   依赖组件许可
scripts/                构建与打包脚本
.github/                Actions、Issue 模板和贡献配置
CHANGELOG.md            版本更新记录
```

## 更新与贡献

后续桌面更新在本仓库的 `main` 分支维护，更新版本号和 CHANGELOG 后推送 `v版本号` 标签，GitHub Actions 会构建并创建对应 Release。详见 [维护与发布](docs/MAINTENANCE.md) 和 [贡献说明](CONTRIBUTING.md)。GitHub 更新仓库和发行版不会让已经下载的 EXE 自动升级，用户需下载新版本。

## 数据与许可

邮箱授权与设备凭据使用当前 Windows 用户的 DPAPI 加密，网页登录由本机浏览器配置保留。不要提交个人配置、Cookie、日志、阅读记录或电子书。详见 [隐私说明](docs/PRIVACY.md)。

项目源码目前保留作者权利，未授予额外的开源许可；第三方组件适用各自许可证，封面及书目权利属于原权利人。详见 [LICENSE](LICENSE) 和 [第三方说明](THIRD_PARTY_NOTICES.md)。
