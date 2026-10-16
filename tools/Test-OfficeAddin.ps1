#Requires -Version 5.1
param([string]$AssemblyPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/OfficeAddin/RimePPT.OfficeAddin.dll'), [switch]$TestRegistration)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') { throw '请使用 Windows PowerShell（.NET Framework）运行此加载项接口测试。' }
if ($TestRegistration) {
    # Run before LoadFrom: preloading would hide a broken CodeBase registration.
    $registered = New-Object -ComObject RimePPT.PowerPointAddin
    if ($registered.GetType().GUID.ToString() -ne '66e789d4-f1af-4c5d-8f8b-7c4a560ee82c') { throw 'Unexpected registered CLSID.' }
    if ([Runtime.InteropServices.Marshal]::IsComObject($registered)) {
        [Runtime.InteropServices.Marshal]::ReleaseComObject($registered) | Out-Null
    }
    $registered = $null
    Write-Output 'PASS registered COM activation without assembly preloading.'
}
$assembly = [Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($AssemblyPath))
$type = $assembly.GetType('RimePPT.OfficeAddin.PowerPointAddin', $true)
$interface = $assembly.GetType('RimePPT.OfficeAddin.IDTExtensibility2', $true)
if ($type.GUID.ToString() -ne '66e789d4-f1af-4c5d-8f8b-7c4a560ee82c') { throw 'Unexpected CLSID.' }
if ($interface.GUID.ToString() -ne 'b65ad801-abaf-11d0-bb8b-00a0c90f2744') { throw 'Unexpected Office interface.' }
if (-not $interface.IsAssignableFrom($type)) { throw 'Missing Office extensibility contract.' }
if ([Runtime.InteropServices.Marshal]::GenerateProgIdForType($type) -ne 'RimePPT.PowerPointAddin') { throw 'Unexpected ProgID.' }
$instance = [Activator]::CreateInstance($type)
[Array]$custom = @()
# A non-COM host deliberately fails event attachment; the optional add-in must remain harmless.
$instance.OnConnection([object]::new(), 1, [object]::new(), [ref]$custom)
$instance.OnDisconnection(1, [ref]$custom)
$instance.OnBeginShutdown([ref]$custom)
$connected = $type.GetField('_connected', [Reflection.BindingFlags]'NonPublic,Instance').GetValue($instance)
if ($connected -ne 0) { throw 'Event subscription leaked after failure.' }
Write-Output 'PASS Office interface, CLSID, ProgID and failure/disconnect cleanup. No registry changes or Office launch.'
