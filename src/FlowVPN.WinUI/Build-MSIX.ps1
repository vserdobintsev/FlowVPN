# Builds and optionally signs a working MSIX; -Preview disables VPN at compile time.
# Never installs a package, trusts a certificate, enables startup or starts a tunnel.
param(
    [switch]$Unsigned,
    [switch]$Preview,
    [uri]$FeedBaseUri,
    [string]$SigningDirectory,
    [string]$DotNetPath
)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if ($Unsigned -and $FeedBaseUri) { throw 'An unsigned package cannot produce an update feed' }
if ($DotNetPath) {
    $dotnet = (Resolve-Path -LiteralPath $DotNetPath -ErrorAction Stop).ProviderPath
    if (!(Test-Path -LiteralPath $dotnet -PathType Leaf)) { throw 'DotNetPath must identify the dotnet executable' }
} else {
    $dotnet = Join-Path $workspace 'work\desktop-build\sdk\dotnet.exe'
    if (!(Test-Path -LiteralPath $dotnet -PathType Leaf)) { $dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop).Source }
}
if ([string]::IsNullOrWhiteSpace($env:DOTNET_CLI_HOME)) { $env:DOTNET_CLI_HOME = Join-Path $workspace 'work\desktop-build\cli' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
if ([string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) { $env:NUGET_PACKAGES = Join-Path $workspace 'work\desktop-build\packages' }

function Read-SigningPassword([string]$Path) {
    $bytes = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($Path), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    try {
        $value = [Text.Encoding]::UTF8.GetString($bytes)
        if ([string]::IsNullOrEmpty($value)) { throw 'The signing password is empty' }
        return $value
    } finally { [Array]::Clear($bytes, 0, $bytes.Length) }
}
function Test-SigningCertificate([string]$Path, [string]$Password, [string]$Publisher, [string]$PublicPath) {
    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($Path, $Password, [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
    $publicCertificate = $null
    try {
        if (!$certificate.HasPrivateKey -or $certificate.Subject -cne $Publisher) { throw 'The signing key does not match the package publisher' }
        $now = [DateTime]::UtcNow
        if ($certificate.NotBefore.ToUniversalTime() -gt $now -or $certificate.NotAfter.ToUniversalTime() -le $now) { throw 'The signing certificate is not currently valid' }
        $usage = @($certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' })
        if ($usage.Count -ne 1 -or '1.3.6.1.5.5.7.3.3' -notin @($usage[0].EnhancedKeyUsages | ForEach-Object Value)) { throw 'The certificate must permit code signing' }
        if ($PublicPath) {
            $publicCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($PublicPath)
            $expected = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($publicCertificate.RawData))
            $actual = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($certificate.RawData))
            if ($publicCertificate.HasPrivateKey -or $actual -cne $expected) { throw 'The signing key does not match the checked-in public certificate' }
        }
    } finally {
        if ($publicCertificate) { $publicCertificate.Dispose() }
        $certificate.Dispose()
    }
}

$variant = if ($Preview) { 'Preview' } else { 'Release' }
$configuration = 'Msix' + $variant
$generated = Join-Path $workspace ('work\msix-generated\' + $configuration + '\' + [guid]::NewGuid().ToString('N'))
$output = Join-Path $workspace 'outputs\FlowVPN-MSIX'
$manifestName = if ($Preview) { 'Package.Preview.appxmanifest' } else { 'Package.appxmanifest' }
[xml]$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot $manifestName)
$packageVersion = [version]$manifest.Package.Identity.Version
if (!$Unsigned) {
    if ([string]::IsNullOrWhiteSpace($SigningDirectory)) {
        $SigningDirectory = Join-Path $workspace $(if ($Preview) { 'work\msix-signing' } else { 'work\signing-release' })
    }
    $SigningDirectory = [IO.Path]::GetFullPath($SigningDirectory)
    $pfx = Join-Path $SigningDirectory $(if ($Preview) { 'FlowVPN-Development.pfx' } else { 'FlowVPN.pfx' })
    $passwordFile = Join-Path $SigningDirectory 'password.dpapi'
    $publicCertificatePath = if ($Preview) { '' } else { Join-Path $PSScriptRoot 'Assets\FlowVPN.cer' }
    if (!(Test-Path -LiteralPath $pfx -PathType Leaf) -or !(Test-Path -LiteralPath $passwordFile -PathType Leaf)) { throw 'The signing key is unavailable; supply -SigningDirectory or use -Unsigned' }
    $password = $null
    try {
        $password = Read-SigningPassword $passwordFile
        Test-SigningCertificate $pfx $password $manifest.Package.Identity.Publisher $publicCertificatePath
    } finally { $password = $null }
}
& (Join-Path $PSScriptRoot 'Distribution\Test-CoreLock.ps1') -CoreDirectory (Join-Path $workspace 'outputs\FlowVPN\core')
& $dotnet build (Join-Path $PSScriptRoot 'FlowVPN.WinUI.csproj') -c Release --nologo --configfile (Join-Path $PSScriptRoot 'NuGet.Config') -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Release validation build failed' }
$testRoot = Join-Path $workspace ('work\release-checks\' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$testExe = Join-Path $PSScriptRoot 'bin\Release\net10.0-windows10.0.26100.0\win-x64\FlowVPN.exe'
& (Join-Path $PSScriptRoot 'Distribution\Test-CoreLock.ps1') -CoreDirectory (Join-Path (Split-Path -Parent $testExe) 'core')
$start = [Diagnostics.ProcessStartInfo]::new($testExe)
$start.UseShellExecute=$false;$start.CreateNoWindow=$true
$start.ArgumentList.Add('--release-self-test');$start.ArgumentList.Add('--root');$start.ArgumentList.Add($testRoot)
$test = [Diagnostics.Process]::Start($start)
try { if (!$test.WaitForExit(120000)) { $test.Kill();throw 'Release checks timed out' }; if($test.ExitCode -ne 0) { throw "Release checks failed; see $testRoot\data\startup-error-winui.txt" } } finally {$test.Dispose()}
New-Item -ItemType Directory -Path $output -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $testRoot 'data\update-tests.json'),(Join-Path $testRoot 'data\release-tests.json') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $testRoot 'design-preview\feature-checks.json') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $testRoot 'data\core-api-checks.json') -Destination $output -Force
$brokerReportPath = Join-Path $testRoot 'data\broker-checks.json'
$brokerReport = Get-Content -LiteralPath $brokerReportPath -Raw | ConvertFrom-Json
if ($brokerReport.Passed -lt 1 -or $brokerReport.Tests.Count -ne $brokerReport.Passed -or $brokerReport.UsesTun -ne $false -or $brokerReport.UsesElevation -ne $false -or $brokerReport.ExternalTraffic -ne $false) { throw 'Broker validation report is missing, incomplete, or describes privileged/network tests' }
Copy-Item -LiteralPath $brokerReportPath -Destination $output -Force
& $dotnet build (Join-Path $PSScriptRoot 'FlowVPN.WinUI.csproj') -c $configuration --nologo --configfile (Join-Path $PSScriptRoot 'NuGet.Config') "-p:AppxPackageDir=$generated\" -p:AppxSymbolPackageEnabled=false -v quiet
if ($LASTEXITCODE -ne 0) { throw 'MSIX build failed' }
$packages = @(Get-ChildItem -LiteralPath $generated -Recurse -Filter "*$configuration.msix")
if ($packages.Count -ne 1) { throw 'Expected exactly one new MSIX output' }
$packageName = if ($Preview) { 'FlowVPN-Preview' } else { 'FlowVPN' }
$suffix = if ($Unsigned) { '-unsigned' } else { '' }
$final = Join-Path $output ("{0}-{1}-x64{2}.msix" -f $packageName, $packageVersion.ToString(3), $suffix)
$stagedPackage = Join-Path $generated ([IO.Path]::GetFileName($final))
Copy-Item -LiteralPath $packages[0].FullName -Destination $stagedPackage
if (!$Unsigned) {
    $password = $null
    try {
        $password = Read-SigningPassword $passwordFile
        Test-SigningCertificate $pfx $password $manifest.Package.Identity.Publisher $publicCertificatePath
        $signTool = Join-Path $env:NUGET_PACKAGES 'microsoft.windows.sdk.buildtools\10.0.26100.9457\bin\10.0.26100.0\x64\signtool.exe'
        if (!(Test-Path -LiteralPath $signTool -PathType Leaf)) { throw 'The pinned Windows SDK SignTool is unavailable in the NuGet package cache' }
        & $signTool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /f $pfx /p $password $stagedPackage
        if ($LASTEXITCODE -ne 0) { throw 'MSIX signing failed' }
    } finally { $password = $null }
}
& (Join-Path $PSScriptRoot 'Distribution\Test-Msix.ps1') -PackagePath $stagedPackage -ExpectedMode $variant -ExpectedVersion $packageVersion -AllowUnsigned:$Unsigned
if (!$Unsigned) {
    $payloadCheck = & (Join-Path $PSScriptRoot 'Distribution\Test-PackagePayload.ps1') -PackagePath $stagedPackage
    if (!$Preview -and $payloadCheck.SignerCertificateSha256 -cne (Get-FileHash -LiteralPath $publicCertificatePath -Algorithm SHA256).Hash) { throw 'The completed package signature does not match the pinned release certificate' }
    $payloadCheck
}
# Expose the final artifact only after its signature and payload checks have passed.
Copy-Item -LiteralPath $stagedPackage -Destination $final -Force
Copy-Item -LiteralPath (Join-Path $generated 'package-checks.json') -Destination $output -Force
if ($FeedBaseUri) {
    $channel = if ($Preview) { 'preview' } else { 'stable' }
    $feedArguments = @{ PackagePath = $final; BaseUri = $FeedBaseUri; Channel = $channel; OutputDirectory = (Join-Path $output 'feed') }
    if ($Preview) { $feedArguments.AllowDevelopmentCertificate = $true }
    else { $feedArguments.PublicCertificate = Join-Path $PSScriptRoot 'Assets\FlowVPN.cer' }
    & (Join-Path $PSScriptRoot 'Distribution\New-UpdateFeed.ps1') @feedArguments
}
Write-Output "Package ready: $final"
