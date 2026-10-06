# 第三方依赖与参考资料

RimePPT 自身采用 GPL-3.0-only。该许可证不会替代第三方组件的授权条款。以下版本来自项目 PackageReference，许可证信息来自对应 NuGet 包元数据及随包文件。

## 直接依赖

- **H.NotifyIcon.WinUI 2.3.0**：MIT。项目与版权声明见 [H.NotifyIcon](https://github.com/HavenDV/H.NotifyIcon)，授权文本见 [MIT](https://licenses.nuget.org/MIT)。
- **Microsoft.WindowsAppSDK 2.4.0**：对应 NuGet 包的 `license.txt`，见 [Windows App SDK](https://github.com/microsoft/WindowsAppSDK)。包内含微软软件许可条款，应以实际使用版本的随包条款为准。
- **Microsoft.Graphics.Win2D 1.3.2**：NuGet 元数据引用 [微软 Win2D 软件许可条款](http://www.microsoft.com/web/webpi/eula/eula_win2d_10012014.htm)。源码项目见 [Win2D](https://github.com/microsoft/Win2D)，源码与预编译 NuGet 包的许可应分别核对。
- **Microsoft.Windows.SDK.BuildTools 10.0.28000.2705**：构建工具，元数据引用 [Windows SDK 许可条款](https://aka.ms/WinSDKLicenseURL)。

自包含发布还带有 .NET、Windows App SDK 及其传递依赖。分发时应保留它们随包提供的许可证和声明；以上直接依赖列表不是所有运行时文件的完整清单。

## 参考项目与设计资料

- **Luminalium**：[SECTL/Luminalium](https://github.com/SECTL/Luminalium)。本项目曾参考其演示工具功能及 WPS 适配方式；本地参考版本 1.4.1.0 的 LICENSE 为 GNU GPL v3。此处说明参考来源，不表示所有代码均直接复制。
- **Microsoft Fluent UI System Icons**：[microsoft/fluentui-system-icons](https://github.com/microsoft/fluentui-system-icons)。橡皮图标的设计参考资料；具体 SVG 为项目内绘制。若后续直接引入官方素材，应保留其原始许可证及版权声明。
- **WinUI / WinUI Gallery**：界面设计参考。使用框架提供的控件或系统图标不改变对应组件的授权。

## 项目品牌素材

README 封面、RimePPT Logo 与 MYXMJY Logo 为维护者提供的品牌素材。保留来源及版权声明；项目代码许可不表示维护者认可衍生产品，也不授予以维护者身份对外发布的权利。

更新依赖或引入第三方代码、图片时，请同步核对并更新本文件。
