#Requires -Version 5.1
param([switch]$Uninstall, [switch]$AllUsers)
$ErrorActionPreference = 'Stop'
if (Get-Process POWERPNT -ErrorAction SilentlyContinue) { throw '请先关闭 PowerPoint，再安装或卸载加载项。' }
$registrationHive = [Microsoft.Win32.RegistryHive]::CurrentUser
if ($AllUsers) {
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw '全机注册需要管理员权限。' }
    $registrationHive = [Microsoft.Win32.RegistryHive]::LocalMachine
}
$assemblyPath = Join-Path $PSScriptRoot 'RimePPT.OfficeAddin.dll'
$clsid = '{66E789D4-F1AF-4C5D-8F8B-7C4A560EE82C}'
$progId = 'RimePPT.PowerPointAddin'
if (-not $Uninstall -and -not (Test-Path -LiteralPath $assemblyPath)) { throw '请从已编译的 OfficeAddin 文件夹运行此脚本。' }
if (-not $Uninstall) {
    $assemblyIdentity = [Reflection.AssemblyName]::GetAssemblyName($assemblyPath)
    $assemblyName = $assemblyIdentity.FullName
    $installRoot = [Environment]::GetFolderPath('LocalApplicationData')
    if ($AllUsers) { $installRoot = [Environment]::GetFolderPath('ProgramFiles') }
    $installedFolder = Join-Path $installRoot 'RimePPT/OfficeAddin'
    New-Item -ItemType Directory -Path $installedFolder -Force | Out-Null
    $installedAssembly = Join-Path $installedFolder 'RimePPT.OfficeAddin.dll'
    if ([IO.Path]::GetFullPath($assemblyPath) -ne [IO.Path]::GetFullPath($installedAssembly)) {
        Copy-Item -LiteralPath $assemblyPath -Destination $installedAssembly -Force
    }
    $assemblyPath = $installedAssembly
}
$views = @([Microsoft.Win32.RegistryView]::Registry32)
if ([Environment]::Is64BitOperatingSystem) { $views += [Microsoft.Win32.RegistryView]::Registry64 }
foreach ($view in $views) {
    $userRoot = [Microsoft.Win32.RegistryKey]::OpenBaseKey($registrationHive, $view)
    try {
        $classPath = "Software\Classes\CLSID\$clsid"
        $progPath = "Software\Classes\$progId"
        $officePath = "Software\Microsoft\Office\PowerPoint\Addins\$progId"
        if ($Uninstall) {
            foreach ($path in @($officePath, $progPath, $classPath)) { $userRoot.DeleteSubKeyTree($path, $false) }
            continue
        }
        $class = $userRoot.CreateSubKey($classPath)
        try { $class.SetValue('', 'RimePPT PowerPoint Integration') } finally { $class.Dispose() }
        $server = $userRoot.CreateSubKey("$classPath\InprocServer32")
        try {
            $server.SetValue('', 'mscoree.dll')
            $server.SetValue('ThreadingModel', 'Both')
            $server.SetValue('Class', 'RimePPT.OfficeAddin.PowerPointAddin')
            $server.SetValue('Assembly', $assemblyName)
            $server.SetValue('RuntimeVersion', 'v4.0.30319')
            $server.SetValue('CodeBase', ([Uri][IO.Path]::GetFullPath($assemblyPath)).ToString())
        } finally { $server.Dispose() }
        $versionServer = $userRoot.CreateSubKey("$classPath\InprocServer32\$($assemblyIdentity.Version)")
        try {
            $versionServer.SetValue('Class', 'RimePPT.OfficeAddin.PowerPointAddin')
            $versionServer.SetValue('Assembly', $assemblyName)
            $versionServer.SetValue('RuntimeVersion', 'v4.0.30319')
            $versionServer.SetValue('CodeBase', ([Uri][IO.Path]::GetFullPath($assemblyPath)).ToString())
        } finally { $versionServer.Dispose() }
        $category = $userRoot.CreateSubKey("$classPath\Implemented Categories\{62C8FE65-4EBB-45E7-B440-6E39B2CDBF29}")
        $category.Dispose()
        $prog = $userRoot.CreateSubKey("$progPath\CLSID")
        try { $prog.SetValue('', $clsid) } finally { $prog.Dispose() }
        $classProg = $userRoot.CreateSubKey("$classPath\ProgId")
        try { $classProg.SetValue('', $progId) } finally { $classProg.Dispose() }
        $office = $userRoot.CreateSubKey($officePath)
        try {
            $office.SetValue('FriendlyName', 'RimePPT')
            $office.SetValue('Description', 'RimePPT 放映事件桥接（界面由独立 WinUI 主程序提供）')
            $office.SetValue('LoadBehavior', 3, [Microsoft.Win32.RegistryValueKind]::DWord)
        } finally { $office.Dispose() }
    } finally { $userRoot.Dispose() }
}
if ($AllUsers) { Write-Output '注册范围：所有用户。' }
if ($Uninstall) { Write-Output '已卸载指定注册范围的 RimePPT Office 加载项。' }
else { Write-Output '已安装指定注册范围的 RimePPT Office 加载项。加载项已复制到对应安装目录。请重新打开 PowerPoint。' }
