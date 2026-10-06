#Requires -Version 7.2
param(
    [string]$PublishedDirectory = '',
    [string]$Version = '',
    [string]$OutputDirectory = '',
    [switch]$Store
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (-not $Version) {
    [xml]$versionManifest = Get-Content (Join-Path $projectRoot 'RimePPT/Package.appxmanifest') -Raw
    $Version = [string]$versionManifest.Package.Identity.Version
}
# Reject invalid Store versions before publishing or creating package files.
if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$') {
    throw 'Package version must contain four numeric components.'
}
$parsedVersion = [version]$Version
if ($parsedVersion.Major -lt 1 -or $parsedVersion.Major -gt 65535 -or $parsedVersion.Minor -gt 65535 -or $parsedVersion.Build -gt 65535 -or $parsedVersion.Revision -gt 65535) {
    throw 'Package version components must be 0..65535, with a nonzero major version.'
}
if ($Store -and $parsedVersion.Revision -ne 0) {
    throw 'Microsoft Store reserves the fourth version component; it must be 0.'
}
$Version = $parsedVersion.ToString(4)
$projectRoot = Split-Path $PSScriptRoot -Parent
$sdkRoot = 'C:\Program Files (x86)\Windows Kits\10\bin'
$sdk = Get-ChildItem -LiteralPath $sdkRoot -Directory | Where-Object {
    $_.Name -match '^10\.0\.' -and (Test-Path -LiteralPath (Join-Path $_.FullName 'x64\makeappx.exe'))
} | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not $sdk) { throw 'Windows SDK MakeAppx and SignTool are required.' }
$makeappx = Join-Path $sdk.FullName 'x64\makeappx.exe'
$signtool = Join-Path $sdk.FullName 'x64\signtool.exe'
$stage = Join-Path $projectRoot ('.tasks\msix-package-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
if (-not $PublishedDirectory) {
    $PublishedDirectory = Join-Path $stage 'payload'
    $build = (Join-Path $stage 'build') + '\'
    # Build the self-contained desktop payload, then create its full-trust package
    # with MakeAppx; this avoids the SDK recipe task's dependency on Visual Studio.
    & dotnet publish (Join-Path $projectRoot 'RimePPT\RimePPT.csproj') -c Release -r win-x64 -p:Platform=x64 "-p:Version=$Version" "-p:OutputPath=$build" -p:WindowsPackageType=None -p:GenerateAppxPackageOnBuild=false -p:AppxPackageSigningEnabled=false --self-contained true -p:WindowsAppSDKSelfContained=true -p:PublishTrimmed=false -p:PublishSingleFile=false -o $PublishedDirectory
    if ($LASTEXITCODE -ne 0) { throw 'MSIX payload publish failed.' }
}
$PublishedDirectory = [IO.Path]::GetFullPath($PublishedDirectory)
foreach ($required in @('RimePPT.exe', 'coreclr.dll', 'Microsoft.UI.Xaml.dll', 'RimePPT.pri')) {
    if (-not (Test-Path -LiteralPath (Join-Path $PublishedDirectory $required))) { throw "Missing payload: $required" }
}
$assets = Join-Path $PublishedDirectory 'Assets'
New-Item -ItemType Directory -Path $assets -Force | Out-Null
Copy-Item -LiteralPath (Get-ChildItem (Join-Path $projectRoot 'RimePPT\Assets') -Filter '*.png').FullName -Destination $assets

# Use a staging manifest; retain the project's publisher and package identity.
[xml]$manifest = Get-Content -LiteralPath (Join-Path $projectRoot 'RimePPT\Package.appxmanifest') -Raw
$manifest.Package.Identity.SetAttribute('Version', $Version)
$manifest.Package.Identity.SetAttribute('ProcessorArchitecture', 'x64')
$app = $manifest.Package.Applications.Application
$app.SetAttribute('Executable', 'RimePPT.exe')
$app.SetAttribute('EntryPoint', 'Windows.FullTrustApplication')
$resources = $manifest.Package.Resources
$resources.RemoveAll()
foreach ($language in @('zh-CN', 'en-US')) {
    $resource = $manifest.CreateElement('Resource', $manifest.DocumentElement.NamespaceURI)
    $resource.SetAttribute('Language', $language)
    [void]$resources.AppendChild($resource)
}
# This desktop app does not use AI model permissions or phone activation.
foreach ($node in @($manifest.SelectNodes("//*[local-name()='Capability' and @Name='systemAIModels'] | //*[local-name()='PhoneIdentity']"))) {
    [void]$node.ParentNode.RemoveChild($node)
}
$manifestPath = Join-Path $stage 'AppxManifest.xml'
$manifest.Save($manifestPath)
$mapping = [Collections.Generic.List[string]]::new()
$mapping.Add('[Files]')
$mapping.Add(('"{0}" "AppxManifest.xml"' -f $manifestPath))
$keepLanguages = @('en-US', 'zh-CN', 'zh-Hans', 'zh')
foreach ($file in Get-ChildItem -LiteralPath $PublishedDirectory -File -Recurse) {
    $relative = [IO.Path]::GetRelativePath($PublishedDirectory, $file.FullName)
    if ($relative -eq 'RimePPT.pri') { $relative = 'resources.pri' }
    if ($file.Extension -eq '.pdb' -or $relative -in @('AppxManifest.xml', 'Package.appxmanifest', 'AppxBlockMap.xml', 'AppxSignature.p7x', '[Content_Types].xml')) { continue }
    $firstDirectory = $relative.Split([IO.Path]::DirectorySeparatorChar)[0]
    if ($file.Extension -eq '.mui' -or $file.Name.EndsWith('.resources.dll')) {
        try { $culture = [Globalization.CultureInfo]::GetCultureInfo($firstDirectory) } catch { $culture = $null }
        if ($culture -and $culture.Name -and $firstDirectory -notin $keepLanguages) { continue }
    }
    $mapping.Add(('"{0}" "{1}"' -f $file.FullName, $relative))
}
$mappingPath = Join-Path $stage 'mapping.txt'
[IO.File]::WriteAllLines($mappingPath, $mapping, [Text.UTF8Encoding]::new($false))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'artifacts\MSIX' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$name = 'RimePPT_' + $Version + '_x64_' + $(if ($Store) { 'Store_' } else { 'Test_' }) + (Get-Date -Format 'yyyyMMdd-HHmmss')
$package = Join-Path $OutputDirectory ($name + '.msix')
$publicCertificate = if ($Store) { $null } else { Join-Path $OutputDirectory ($name + '.cer') }
& $makeappx pack /f $mappingPath /p $package /h SHA256
if ($LASTEXITCODE -ne 0) { throw 'MSIX validation/packaging failed.' }

# Create an isolated development certificate in memory; never add machine trust.
if (-not $Store) {
$rsa = [Security.Cryptography.RSA]::Create(3072)
$request = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
    [string]$manifest.Package.Identity.Publisher, $rsa,
    [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
$request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
$request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new([Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature, $true))
$usage = [Security.Cryptography.OidCollection]::new()
[void]$usage.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3'))
$request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($usage, $true))
$certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-5), [DateTimeOffset]::UtcNow.AddYears(1))
$password = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$privateCertificate = Join-Path $stage 'signing.pfx'
try {
    [IO.File]::WriteAllBytes($privateCertificate, $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $password))
    [IO.File]::WriteAllBytes($publicCertificate, $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
    & $signtool sign /fd SHA256 /a /f $privateCertificate /p $password $package
    if ($LASTEXITCODE -ne 0) { throw 'MSIX signing failed.' }
} finally {
    if (Test-Path -LiteralPath $privateCertificate) { [IO.File]::Delete($privateCertificate) }
    $certificate.Dispose(); $rsa.Dispose(); $password = $null
}
}
$validation = Join-Path $stage 'validation'
& $makeappx unpack /p $package /d $validation
if ($LASTEXITCODE -ne 0) { throw 'Signed package unpack validation failed.' }
if ((Get-FileHash (Join-Path $PublishedDirectory 'RimePPT.exe')).Hash -ne (Get-FileHash (Join-Path $validation 'RimePPT.exe')).Hash) { throw 'Packaged executable differs from build.' }
if (-not $Store) {
Add-Type -AssemblyName System.Security.Cryptography.Pkcs
$signature = [IO.File]::ReadAllBytes((Join-Path $validation 'AppxSignature.p7x'))
$cms = [Security.Cryptography.Pkcs.SignedCms]::new()
$cms.Decode([byte[]]$signature[4..($signature.Length - 1)])
$cms.CheckSignature($true)
}
[xml]$checkedManifest = Get-Content -LiteralPath (Join-Path $validation 'AppxManifest.xml') -Raw
if ($checkedManifest.Package.Identity.Version -ne $Version) { throw 'Packaged version differs from the requested version.' }
if ($checkedManifest.Package.Identity.Name -ne $manifest.Package.Identity.Name -or $checkedManifest.Package.Identity.Publisher -ne $manifest.Package.Identity.Publisher -or $checkedManifest.Package.Properties.PublisherDisplayName -ne $manifest.Package.Properties.PublisherDisplayName) { throw 'Packaged identity differs from the project manifest.' }
[pscustomobject]@{ Package = $package; Certificate = $publicCertificate; Distribution = $(if ($Store) { 'Microsoft Store submission; signed by Store' } else { 'Self-signed local test' }); SizeMB = [Math]::Round((Get-Item -LiteralPath $package).Length / 1MB, 1); Payload = $PublishedDirectory; Validation = $validation } |
    Tee-Object -Variable result
$result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stage 'result.json')
$stage | Set-Content -LiteralPath (Join-Path $projectRoot '.tasks\msix-last-package.path')
