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
    $parentProcess = Get-Process -Id $config.ParentPid -ErrorAction SilentlyContinue
    if ($parentProcess -and -not $parentProcess.WaitForExit(60000)) { throw 'Application has not exited.' }
    $mutex = New-Object Threading.Mutex($false, [string]$config.MutexName)
    try { $locked = $mutex.WaitOne(60000) } catch [Threading.AbandonedMutexException] { $locked = $true }
    if (-not $locked) { throw 'Another application instance is running.' }
    New-Item -ItemType Directory -Path $backup -Force | Out-Null
    # Back up every replaced file before modifying any installed file.
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
        $modified.Add([pscustomobject]@{ Source = $file.FullName; Target = $destination; Backup = $old; Existed = $exists })
    }
    $modified | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $backup 'restore-manifest.json') -Encoding UTF8
    $ready = $true
    foreach ($file in $modified) {
        New-Item -ItemType Directory -Path (Split-Path $file.Target -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $file.Source -Destination $file.Target -Force
        if ((Get-FileHash -LiteralPath $file.Source).Hash -ne (Get-FileHash -LiteralPath $file.Target).Hash) { throw 'Installed file verification failed.' }
    }
    $mutex.ReleaseMutex(); $locked = $false
    # Settings reads this result immediately during startup; a failed launch
    # replaces it with the rollback result before restarting the old app.
    'Update succeeded. Backup: ' + $backup | Set-Content -LiteralPath $log -Encoding UTF8
    $newProcess = Start-Process -FilePath (Join-Path $target 'RimePPT.exe') -ArgumentList '--settings' -WorkingDirectory $target -PassThru
    Start-Sleep -Seconds 5
    if ($newProcess.HasExited) { throw 'Updated application exited immediately.' }
} catch {
    $failure = $_.Exception.Message
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
    exit 1
} finally {
    if ($locked) { $mutex.ReleaseMutex() }
    if ($mutex) { $mutex.Dispose() }
}
