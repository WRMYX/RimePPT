# RimePPT Office 加载项

可选的 PowerPoint COM 事件桥接，支持 Windows 上的 32 / 64 位 PowerPoint，需要 .NET Framework 4.8。
加载项无窗口，不替代原生 WinUI 主程序。RimePPT 运行时，放映开始、页面变化和放映结束会唤醒主程序的 COM 检测线程；没有加载项时继续使用原有轮询。
它不会自动启动 RimePPT，也不会读写课件、修改安全设置或截获键盘。

构建：运行 `tools/Build-OfficeAddin.ps1`。关闭 PowerPoint，然后用 PowerShell 运行输出文件夹中的 `Register-OfficeAddin.ps1`。脚本将 DLL 复制到 `%LOCALAPPDATA%/RimePPT/OfficeAddin` 的独立位置，便携版更新不会覆盖正在加载的 DLL。
卸载：关闭 PowerPoint，运行同一个脚本并加 `-Uninstall`。默认仅注册当前用户，通常不需要管理员权限。关闭 UAC 或以管理员身份运行 PowerPoint 时，使用管理员 PowerShell 加 `-AllUsers` 注册；DLL 安装到 `%ProgramFiles%/RimePPT/OfficeAddin`。卸载全机注册时使用 `-AllUsers -Uninstall`。

可以在 PowerPoint → 文件 → 选项 → 加载项 → COM 加载项中查看或禁用 RimePPT。
FriendlyName 为 RimePPT；是否在启动画面显示由 Office 版本和加载过程决定，不能保证每次可见。
商店 / MSIX 的安装不自动注册此可选组件，注册后的本地应用数据文件夹必须保留。更新 DLL 前先关闭 PowerPoint。

此组件与主项目使用相同的 GPL-3.0 许可证。已在当前 64 位 PowerPoint 验证枚举和连接成功；32 位 Office 与放映事件仍需进一步测试。注册后可以使用 Windows PowerShell 运行 `tools/Test-OfficeAddin.ps1 -TestRegistration` 验证真实 COM 激活。
