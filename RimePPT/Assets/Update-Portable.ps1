param([Parameter(Mandatory = $true)][string]$Plan)
$ErrorActionPreference = 'Stop'
$mutex = $null
$locked = $false
$modified = [Collections.Generic.List[object]]::new()
$ready = $false
$config = Get-Content -LiteralPath $Plan -Raw -Encoding UTF8 | ConvertFrom-Json
$target = [IO.Path]::GetFullPath($config.Target).TrimEnd('\')
$payload = [IO.Path]::GetFullPath($config.Payload).TrimEnd('\')
$backup = [IO.Path]::GetFullPath($config.Backup).TrimEnd('\')
$log = [string]$config.ResultPath
function Publish-Status([string]$stage, [double]$percent, [string]$message) {
    if ($config.StatusPath) {
        try {
        $temporary = [string]$config.StatusPath + '.tmp'
        @{ Stage = $stage; Percent = $percent; Message = $message } | ConvertTo-Json | Set-Content -LiteralPath $temporary -Encoding UTF8
        Move-Item -LiteralPath $temporary -Destination $config.StatusPath -Force
        } catch { } # Diagnostics must never prevent rollback.
    }
}
function Save-History([string]$outcome, [string]$message) {
    if ($config.HistoryPath) {
        try {
        New-Item -ItemType Directory -Path (Split-Path $config.HistoryPath -Parent) -Force | Out-Null
        $historyLog = [IO.Path]::ChangeExtension([string]$config.HistoryPath, '.log')
        if (Test-Path -LiteralPath $log) { Copy-Item -LiteralPath $log -Destination $historyLog -Force }
        @{ Version = $config.NewVersion; Channel = 'GitHub portable'; Time = [DateTimeOffset]::Now.ToString('o'); Outcome = $outcome; Detail = $message; Log = $historyLog } | ConvertTo-Json | Set-Content -LiteralPath $config.HistoryPath -Encoding UTF8
        } catch { }
    }
}
function Assert-PlainPath([string]$path) {
    $current = $path
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Update path contains a junction or symbolic link.' }
        }
        $parent = Split-Path $current -Parent
        if ($parent -eq $current) { break }
        $current = $parent
    }
}
try {
    if ($target -eq [IO.Path]::GetPathRoot($target).TrimEnd('\') -or -not (Test-Path -LiteralPath (Join-Path $target 'RimePPT.exe'))) { throw 'Invalid installed application directory.' }
    Assert-PlainPath $target; Assert-PlainPath $payload; Assert-PlainPath $backup
    if ($payload.StartsWith($target + '\', [StringComparison]::OrdinalIgnoreCase) -or $backup.StartsWith($target + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Update staging must be outside the application directory.' }
    $files = @(Get-ChildItem -LiteralPath $payload -File -Recurse)
    if ($files.Count -eq 0) { throw 'Empty update payload.' }
    $exe = Join-Path $payload 'RimePPT.exe'
    if (-not (Test-Path -LiteralPath $exe)) { throw 'Missing updated application.' }
    $version = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $payload 'RimePPT.dll')).Version.ToString()
    if ($version -ne $config.NewVersion) { throw 'Updated program version does not match release.' }
    Publish-Status 'waiting' -1 'Waiting for application exit.'
    if ($config.AuthorizationPath) {
        $handoffSeconds = 60
        if ($config.HandoffTimeoutSeconds) { $handoffSeconds = [Math]::Max(1, [Math]::Min(60, [int]$config.HandoffTimeoutSeconds)) }
        $deadline = [DateTime]::UtcNow.AddSeconds($handoffSeconds)
        while (-not (Test-Path -LiteralPath $config.AuthorizationPath) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
        if (-not (Test-Path -LiteralPath $config.AuthorizationPath)) { throw 'Update handoff was not confirmed; installed files were not changed.' }
    }
    $parentProcess = Get-Process -Id $config.ParentPid -ErrorAction SilentlyContinue
    if ($parentProcess -and -not $parentProcess.WaitForExit(60000)) { throw 'Application has not exited.' }
    $mutex = New-Object Threading.Mutex($false, [string]$config.MutexName)
    try { $locked = $mutex.WaitOne(60000) } catch [Threading.AbandonedMutexException] { $locked = $true }
    if (-not $locked) { throw 'Another application instance is running.' }
    New-Item -ItemType Directory -Path $backup -Force | Out-Null
    # Back up every replaced file before modifying any installed file.
    $total = [double](($files | Measure-Object -Property Length -Sum).Sum)
    $backupTotal = 0.0
    foreach ($file in $files) {
        $existing = Join-Path $target $file.FullName.Substring($payload.Length + 1)
        if (Test-Path -LiteralPath $existing) { $backupTotal += (Get-Item -LiteralPath $existing).Length }
    }
    $done = 0.0
    Publish-Status 'backup' 0 'Backing up installed files.'
    foreach ($file in $files) {
        Assert-PlainPath $file.FullName
        $relative = $file.FullName.Substring($payload.Length + 1)
        $destination = [IO.Path]::GetFullPath((Join-Path $target $relative))
        if (-not $destination.StartsWith($target + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid destination path.' }
        Assert-PlainPath $destination
        $old = Join-Path $backup $relative
        $exists = Test-Path -LiteralPath $destination
        if ($exists) {
            New-Item -ItemType Directory -Path (Split-Path $old -Parent) -Force | Out-Null
            Copy-Item -LiteralPath $destination -Destination $old -Force
        }
        if ($exists) { $done += (Get-Item -LiteralPath $old).Length }
        Publish-Status 'backup' (100 * $done / [Math]::Max(1, $backupTotal)) $relative
        $modified.Add([pscustomobject]@{ Source = $file.FullName; Target = $destination; Backup = $old; Existed = $exists })
    }
    $modified | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $backup 'restore-manifest.json') -Encoding UTF8
    $ready = $true
    $done = 0.0
    Publish-Status 'install' 0 'Installing program files.'
    foreach ($file in $modified) {
        New-Item -ItemType Directory -Path (Split-Path $file.Target -Parent) -Force | Out-Null
        Publish-Status 'install' (100 * $done / [Math]::Max(1, $total)) $file.Target
        Copy-Item -LiteralPath $file.Source -Destination $file.Target -Force
        Publish-Status 'verify' (100 * $done / [Math]::Max(1, $total)) $file.Target
        if ((Get-FileHash -LiteralPath $file.Source).Hash -ne (Get-FileHash -LiteralPath $file.Target).Hash) { throw 'Installed file verification failed.' }
        $done += (Get-Item -LiteralPath $file.Source).Length
    }
    $mutex.ReleaseMutex(); $locked = $false
    # Settings reads this result immediately during startup; a failed launch
    # replaces it with the rollback result before restarting the old app.
    'Update succeeded. Backup: ' + $backup | Set-Content -LiteralPath $log -Encoding UTF8
    Publish-Status 'restart' -1 'Starting updated application.'
    $newProcess = Start-Process -FilePath (Join-Path $target 'RimePPT.exe') -ArgumentList '--settings' -WorkingDirectory $target -PassThru
    Start-Sleep -Seconds 5
    if ($newProcess.HasExited) { throw 'Updated application exited immediately.' }
    Save-History 'success' ('Backup: ' + $backup)
    Publish-Status 'success' 100 ('Backup: ' + $backup)
} catch {
    $failure = $_.Exception.Message
    Publish-Status 'rollback' -1 $failure
    if ($ready) {
        try {
            if (-not $locked) {
                try { $locked = $mutex.WaitOne(60000) } catch [Threading.AbandonedMutexException] { $locked = $true }
                if (-not $locked) { throw 'Another application instance blocks rollback.' }
            }
            foreach ($file in $modified) {
                if ($file.Existed) {
                    if (-not (Test-Path -LiteralPath $file.Target) -or (Get-FileHash -LiteralPath $file.Backup).Hash -ne (Get-FileHash -LiteralPath $file.Target).Hash) {
                        Copy-Item -LiteralPath $file.Backup -Destination $file.Target -Force
                    }
                }
                elseif (Test-Path -LiteralPath $file.Target) { Remove-Item -LiteralPath $file.Target -Force }
            }
            $failure += ' Previous files restored.'
        } catch { $failure += ' Rollback incomplete: ' + $_.Exception.Message }
    }
    $failure | Set-Content -LiteralPath $log -Encoding UTF8
    if ($locked) { $mutex.ReleaseMutex(); $locked = $false }
    $original = Get-Process -Id $config.ParentPid -ErrorAction SilentlyContinue
    if ($original) { [void]$original.WaitForExit(10000) }
    if (-not (Get-Process -Id $config.ParentPid -ErrorAction SilentlyContinue)) {
        Start-Process -FilePath (Join-Path $target 'RimePPT.exe') -ArgumentList '--settings' -WorkingDirectory $target
    }
    Save-History 'error' $failure
    Publish-Status 'error' 0 $failure
    exit 1
} finally {
    if ($locked) { $mutex.ReleaseMutex() }
    if ($mutex) { $mutex.Dispose() }
}
