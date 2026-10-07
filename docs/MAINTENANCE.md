# 维护、构建与发布

## 唯一维护目录

本机正式仓库为 `G:\chatgpt\轻阅\qingyue`，后续在这里修改；历史 `轻阅-v*` 目录是版本归档。先检查 `git status` 和 `git remote -v`，存在未提交改动时先合并，不用新副本覆盖。

```powershell
git fetch origin
git pull --ff-only
```

## 构建

需要 Windows、PowerShell 7、.NET 10 SDK。SDK 通道由 `global.json` 管理，依赖版本在 csproj 中固定。

```powershell
dotnet restore .\src\Qingyue.Desktop\EpubKindleFix.csproj
dotnet build .\Qingyue.slnx -c Release --no-restore
pwsh -File .\scripts\publish.ps1
```

脚本输出自包含 Windows x64 包、源码 ZIP 与 SHA256SUMS。构建不等于真实邮箱收件、网页登录或 Kindle 同步验证。

## 保存更新

1. 修改源码，保持原界面和动效；更新 `CHANGELOG.md`。
2. 审阅 `git diff` 和待提交文件，确认不含私人配置、会话、书籍和授权数据。
3. 提交、推送到 `main`。多人修改用分支和 Pull Request 合并，禁止 force push。

```powershell
git add src docs scripts .github CHANGELOG.md README.md
git commit -m "feat: describe the change"
git push origin main
```

## 正式发布

1. 更新 `src/Qingyue.Desktop/EpubKindleFix.csproj` 的 Version，例如 `2.1.5`；补充 CHANGELOG 和 README 版本号。
2. 提交并推送源码。
3. 创建与项目版本一致的新标签；标签一经发布不覆盖。

```powershell
git tag -a v2.1.5 -m "Qingyue 2.1.5"
git push origin v2.1.5
```

GitHub Actions 会编译、打包并创建 Release，含 Windows 包、源码包与校验文件。普通 main 推送和 PR 只编译、保存临时构建产物，不发布新版本。若已有同名 Release，工作流会停止，避免覆盖资产。

下载新版本后退出旧程序再运行；当前没有软件内自动升级模块。这里的后续同步指源码与发行版在 GitHub 维护。

## 已有版本首次导入

本次首次导入为 v2.1.4，只建立这一版本的真实历史；不凭空生成以前的 Git 提交。原说明归档在 docs/history。旧手机后端没有纳入此仓库，公共同步服务不是独立部署保证。

参考：[setup-dotnet](https://github.com/actions/setup-dotnet)、[GitHub Release CLI](https://cli.github.com/manual/gh_release_create)。
