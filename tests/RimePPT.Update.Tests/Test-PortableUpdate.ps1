#Requires -Version 7.2
$ErrorActionPreference = 'Stop'
$repository = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$testRoot = Join-Path $repository ('.tasks/updater-probe-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$fixture = Join-Path $testRoot 'fixture-payload'
& dotnet publish (Join-Path $PSScriptRoot 'Fixture/Fixture.csproj') -c Release -o $fixture *> (Join-Path $testRoot 'build.log')
if ($LASTEXITCODE -ne 0) { throw 'Headless fixture build failed.' }
$helper = Join-Path $repository 'RimePPT/Assets/Update-Portable.ps1'
$windowsPowerShell = Join-Path $env:WINDIR 'System32/WindowsPowerShell/v1.0/powershell.exe'
foreach ($test in @('success', 'rollback', 'nohandoff')) {
    # Include non-ASCII path characters to cover PowerShell 5.1 JSON decoding.
    $case = Join-Path $testRoot ('测试-' + $test)
    $target = Join-Path $case 'installed'; $payload = Join-Path $case 'payload'; $backup = Join-Path $case 'backup'
    New-Item -ItemType Directory -Path $target, $payload -Force | Out-Null
    Get-ChildItem -LiteralPath $fixture -File | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $target
        Copy-Item -LiteralPath $_.FullName -Destination $payload
    }
    Set-Content (Join-Path $target 'aa-marker.txt') 'old'
    Set-Content (Join-Path $payload 'aa-marker.txt') 'new'
    Set-Content (Join-Path $target 'zz-lock.txt') 'old-lock'
    Set-Content (Join-Path $payload 'zz-lock.txt') 'new-lock'
    $result = Join-Path $case 'result.txt'; $plan = Join-Path $case 'plan.json'
    $authorization = Join-Path $case 'authorized.txt'
    if ($test -ne 'nohandoff') { Set-Content -LiteralPath $authorization -Value 'install' }
    @{ ParentPid = 2147483647; Target = $target; Payload = $payload; Backup = $backup; NewVersion = '1.2.3.0'; ResultPath = $result;
       AuthorizationPath = $authorization; HandoffTimeoutSeconds = 1;
       StatusPath = (Join-Path $case 'status.json'); HistoryPath = (Join-Path $case 'history.json');
       MutexName = ('Local\RimePPT.UpdateProbe-' + [guid]::NewGuid()) } | ConvertTo-Json | Set-Content -LiteralPath $plan -Encoding utf8
    $lock = $null
    try {
        if ($test -eq 'rollback') {
            $lock = [IO.File]::Open((Join-Path $target 'zz-lock.txt'), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        }
        & $windowsPowerShell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $helper -Plan $plan
        $code = $LASTEXITCODE
        if ($test -eq 'success') {
            if ($code -ne 0 -or (Get-Content (Join-Path $target 'aa-marker.txt')).Trim() -ne 'new' -or (Get-Content $result) -notlike 'Update succeeded*') { throw 'Success probe failed.' }
            if ((Get-Content (Join-Path $backup 'aa-marker.txt')).Trim() -ne 'old') { throw 'Backup missing.' }
        } elseif ($test -eq 'rollback') {
            if ($code -ne 1 -or (Get-Content (Join-Path $target 'aa-marker.txt')).Trim() -ne 'old' -or (Get-Content $result) -notlike '*Previous files restored*') { throw 'Rollback probe failed.' }
        } else {
            if ($code -ne 1 -or (Get-Content (Join-Path $target 'aa-marker.txt')).Trim() -ne 'old' -or (Get-Content $result) -notlike '*handoff was not confirmed*') { throw 'Unconfirmed handoff modified installed files.' }
        }
        $status = Get-Content (Join-Path $case 'status.json') -Raw | ConvertFrom-Json
        $history = Get-Content (Join-Path $case 'history.json') -Raw | ConvertFrom-Json
        if ($test -eq 'success' -and ($status.Stage -ne 'success' -or $status.Percent -ne 100 -or $history.Outcome -ne 'success')) { throw 'Success progress/history missing.' }
        if ($test -eq 'rollback' -and ($status.Stage -ne 'error' -or $history.Outcome -ne 'error' -or $status.Message -notlike '*Previous files restored*')) { throw 'Rollback progress/history missing.' }
        Write-Output "PASS isolated updater $test and progress/history"
    } finally { if ($lock) { $lock.Dispose() } }
}
$global:LASTEXITCODE = 0
