<div align="center">
  <img src="RimePPT/Assets/RimePPT-Logo.svg" width="96" alt="RimePPT Logo" />
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="RimePPT/Assets/MYXMJY-white.png" />
    <img src="RimePPT/Assets/MYXMJY-black.png" width="112" alt="开发者 MYXMJY Logo" />
  </picture>
  <h1>RimePPT · 雾淞 PPT 助手</h1>
  <p>面向课堂演示与触摸屏的 Windows 放映助手。</p>
  <p>
    <a href="https://RimePPT.MYXMJY.Top">官方网站</a> ·
    <a href="https://apps.microsoft.com/detail/9MV9Q91FTTFZ">Microsoft Store</a> ·
    <a href="https://github.com/WRMYX/RimePPT/issues">问题反馈</a>
  </p>
</div>

![RimePPT 封面](docs/images/RimePPT-Cover.png)

RimePPT 将翻页、批注、擦除和课堂辅助工具放在浮动工具栏中，帮助教师在演示过程中直接操作。界面采用 WinUI 与 Fluent 风格，支持鼠标、触笔及触摸操作。

## 主要功能

- **放映工具栏**：上一页、下一页、页面导航、批注、橡皮、撤销、重做及退出放映。
- **工具栏自定义**：分别设置左右侧栏及底部各位置的显示项目，拖动调整顺序；支持主题、按钮文字和屏幕边距设置。
- **两种批注模式**：PowerPoint 原生 COM 笔迹与 RimePPT 自研笔迹。线型、形状及自定义尺寸使用自研模式。
- **颜色、线型与形状**：预设及自定义颜色，虚线、点线、波浪线等线型；直接绘制直线、箭头、矩形、椭圆和三角形。
- **智能图形整理**：自研实线自由书写后保持按住约 0.6 秒，可整理为直线、圆、矩形或三角形；继续明显移动可恢复自由书写，可在设置中关闭。
- **笔迹切页动画**：渐隐渐显或按书写顺序重播。
- **独立画板**：覆盖放映屏幕，支持多页书写、页面缩略图导航、擦除、撤销、重做，以及 PNG / PDF 导出。
- **课堂辅助工具**：聚光与选区放大、保存选区图片、黑屏、计时器及板书导出。
- **应用与文件快速启动**：自定义名称、固定到指定工具栏，并与其他工具按钮一起排序。
- **Class Widgets 2 联动**：通过插件获取当前科目，设置页面提供连接状态检查。
- **使用引导**：简短入门与完整指南，均可跳过。
- **微软商店更新入口**：商店更新适用于通过 Microsoft Store 安装的版本。

> WPS 适配代码已接入，但仍需要真实 WPS 环境验证。智能整理的实际效果取决于书写方式和设备，不能保证每一笔都会识别。

## 安装与使用

优先从 [Microsoft Store](https://apps.microsoft.com/detail/9MV9Q91FTTFZ) 安装，或通过 [官方网站](https://RimePPT.MYXMJY.Top) 查看下载渠道。

便携版解压后运行 `RimePPT.exe`。打开 PowerPoint 并开始放映后，工具栏会按设置显示。需要线型、形状、智能整理或可调橡皮时，请选择 **RimePPT 自研** 模式。

独立画板中的退出按钮用于返回 PPT。导出板书前请确认当前内容；程序设置中的使用指南可帮助重新了解各个工具。

## 技术栈

- C# / .NET 8
- WinUI 3 / Windows App SDK
- Win2D：笔迹和画板渲染
- PowerPoint COM：放映控制与原生批注
- H.NotifyIcon.WinUI：系统托盘
- MSIX 与自包含便携版发布

## 从源码构建

使用 Windows 开发环境，安装 .NET 8 SDK、Windows SDK，以及相应的 WinUI 开发工具。打包脚本需要 **PowerShell 7.2 或更高版本（`pwsh`）**，不支持 Windows 自带的 PowerShell 5.1。仓库中的解决方案使用 `.slnx` 格式；若工具不支持该格式，可直接构建项目文件。

以下命令在仓库根目录运行，以 x64 为例：

```powershell
dotnet restore RimePPT/RimePPT.csproj
dotnet build RimePPT/RimePPT.csproj -c Release -p:Platform=x64
```

直接发布可运行程序：

```powershell
dotnet publish RimePPT/RimePPT.csproj -c Release -r win-x64 -p:Platform=x64 --self-contained true -p:WindowsAppSDKSelfContained=true -p:PublishTrimmed=false -p:PublishSingleFile=false -o artifacts/publish
```

打包便携版：

```powershell
pwsh -NoProfile -File tools/Build-Portable.ps1
```

生成 MSIX：

```powershell
pwsh -NoProfile -File tools/Build-MSIX.ps1
```

MSIX 打包需要 Windows SDK 中的 MakeAppx / SignTool。商店提交包与本地签名包的用途不同，请根据脚本参数和自己的发布身份配置选择；不要上传签名私钥或账号凭据。

## GitHub 自动检查与发布

上传代码或创建 PR 后，**Build and test / 编译与测试** 自动运行笔迹测试并编译 x64 程序。

无需命令发布新版本：

1. 打开仓库 **Actions → Package application / 打包程序 → Run workflow**，选择要发布的分支，通常为 `main`。
2. 在 `release_version` 输入未使用过的三段版本标签，例如 `v1.2.3`；留空则仅打包，不创建 Release。
3. 等待测试与打包通过。在 Actions 运行页面的 **Artifacts** 下载便携版、MSIX＋证书或 Store 提交包。
4. 填写版本号的运行会创建草稿 Release，仅附便携版 ZIP 和 MSIX＋证书 ZIP。下载并实际测试后，再点击 **Publish release** 公开发布。

也支持推送 `v1.2.3` 这样的标签触发打包。版本转换为 `1.2.3.0`，便携程序与 MSIX 使用同一版本。四段标签暂不支持；没有指定版本时读取 `RimePPT/Package.appxmanifest`，修改默认版本只需修改该清单。

- **便携版**：解压运行，无需安装。
- **MSIX＋证书**：本地安装版本，安装前需要信任附带的 `.cer`。当前每次打包生成新自签名证书，并非固定的发布证书。
- **Store 提交包**：只在 Actions 产物中提供，不附到 Release；上传微软合作伙伴中心，由微软审核并签名。

私有仓库的下载需要相应访问权限。草稿用于发布者验收，不供普通用户更新。第三方依赖与素材来源见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

## 软件内 GitHub 更新

设置的更新页面提供 GitHub 正式 Release 查询、更新说明、下载进度和取消下载。启动时自动检查默认关闭，可自行开启；该选项不会自动下载或安装更新。程序不内置 GitHub 令牌，仓库必须公开且有正式 Release，草稿和预发布不供软件更新。

下载文件必须匹配版本、安装类型与进程架构，且 GitHub API 必须提供有效 SHA-256 digest。便携版文件名为 `RimePPT-1.2.3.0-Portable-x64.zip`，非商店 MSIX 为 `RimePPT-1.2.3.0-MSIX-with-certificate-x64.zip`；现有发布流程自动生成这些名称。缺少文件或校验信息时不会自动安装，可通过页面中的 GitHub 链接手动查看。

便携版确认更新后会退出，通过独立进程备份并替换程序文件，再打开设置；失败时尝试恢复原文件。下载、备份与结果记录在 `%LOCALAPPDATA%\RimePPT\Updates`。不会修改用户设置和板书文件，不会自动删除旧备份。程序目录须有写入权限；如无权限，可手动解压新版本到可写目录。

非商店 MSIX 下载后打开安装文件夹，由用户信任证书并使用 Windows 安装界面验证升级。当前证书每次生成，仍需手动信任，不会自动安装证书。商店安装版继续使用 Microsoft Store 更新。

## 测试

```powershell
dotnet run --project tests/RimePPT.Ink.Tests -c Release
pwsh -NoProfile -File tests/RimePPT.Update.Tests/Test-PortableUpdate.ps1
```

自动化测试覆盖笔迹几何、擦除、图形识别、撤销、重播和设置兼容性等。自动化通过不代表真实触摸、PowerPoint / WPS 兼容性或界面交互已完成验收。

## 反馈

请通过 [GitHub Issues](https://github.com/WRMYX/RimePPT/issues) 提交问题，尽量附上软件版本、Windows 版本、放映软件、操作步骤以及截图。提交课件、日志或配置前，请移除个人信息。

## 许可证与署名

Copyright © 2026 **MYXMJY**。

本项目采用 **GNU General Public License v3.0（GPL-3.0-only）**，完整条款见 [LICENSE](LICENSE)，官方文本见 [GNU GPL v3](https://www.gnu.org/licenses/gpl-3.0.html)。

- 允许使用、学习、修改和分发，也允许商业使用，无需另行申请商业授权。
- 分发本作品或基于本作品的衍生作品时，须遵守 GPL v3，保留适用的版权、许可证及相关声明，并标明修改。
- 分发可执行程序时，须按 GPL v3 规定提供相应源码或符合条款的源码获取方式；对受 GPL 覆盖的衍生作品采用 GPL v3 授权。
- 仅在本地使用或修改、不向他人分发，不因 GPL v3 而必须公开修改后的源码。
- 第三方依赖与素材遵循各自许可证；本软件不提供任何担保，具体要求以许可证全文为准。

请保留原作者 **MYXMJY** 的版权声明。GPL v3 不代表原作者为衍生版本提供认可或背书。

更新页面会根据安装类型提供 Microsoft Store 或 GitHub 更新。GitHub 下载默认官方直连，可选择 GH-Proxy 或管理自定义 HTTPS 加速前缀；版本信息仍来自官方 API，所有线路均校验官方附件大小和 SHA-256。

便携版安装由独立更新窗口显示备份、文件替换、校验及回滚状态，安装期间请等待完成。更新记录位于 `%LOCALAPPDATA%/RimePPT/Updates/History`。非商店 MSIX 的安装进度由 Windows 应用安装程序提供；商店版显示 Store API 返回的进度。

独立安装窗口使用原生 WinUI 3，携带完整运行依赖，启动前复制到暂存目录。
