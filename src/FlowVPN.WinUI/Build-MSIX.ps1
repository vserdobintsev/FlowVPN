# Builds and optionally signs a working MSIX; -Preview disables VPN at compile time.
# Never installs a package, trusts a certificate, enables startup or starts a tunnel.
param([switch]$Unsigned, [switch]$Preview, [uri]$FeedBaseUri)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$dotnet = Join-Path $workspace 'work\desktop-build\sdk\dotnet.exe'
if (!(Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_HOME = Join-Path $workspace 'work\desktop-build\cli'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:NUGET_PACKAGES = Join-Path $workspace 'work\desktop-build\packages'
$variant = if ($Preview) { 'Preview' } else { 'Release' }
$configuration = 'Msix' + $variant
$generated = Join-Path $workspace ('work\msix-generated\' + $configuration + '\' + [guid]::NewGuid().ToString('N'))
$output = Join-Path $workspace 'outputs\FlowVPN-MSIX'
& (Join-Path $PSScriptRoot 'Distribution\Test-CoreLock.ps1') -CoreDirectory (Join-Path $workspace 'outputs\FlowVPN\core')
$manifestName = if ($Preview) { 'Package.Preview.appxmanifest' } else { 'Package.appxmanifest' }
[xml]$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot $manifestName)
$packageVersion = [version]$manifest.Package.Identity.Version
& $dotnet build (Join-Path $PSScriptRoot 'FlowVPN.WinUI.csproj') -c Release --nologo --configfile (Join-Path $PSScriptRoot 'NuGet.Config') -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Release validation build failed' }
$testRoot = Join-Path $workspace ('work\release-checks\' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $testRoot 'core') -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $workspace 'outputs\FlowVPN\core') | Copy-Item -Destination (Join-Path $testRoot 'core') -Force
$testExe = Join-Path $PSScriptRoot 'bin\Release\net10.0-windows10.0.26100.0\win-x64\FlowVPN.exe'
$start = [Diagnostics.ProcessStartInfo]::new($testExe)
$start.UseShellExecute=$false;$start.CreateNoWindow=$true
$start.ArgumentList.Add('--release-self-test');$start.ArgumentList.Add('--root');$start.ArgumentList.Add($testRoot)
$test = [Diagnostics.Process]::Start($start)
try { if (!$test.WaitForExit(120000)) { $test.Kill();throw 'Release checks timed out' }; if($test.ExitCode -ne 0) { throw "Release checks failed; see $testRoot\data\startup-error-winui.txt" } } finally {$test.Dispose()}
New-Item -ItemType Directory -Path $output -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $testRoot 'data\update-tests.json'),(Join-Path $testRoot 'data\release-tests.json') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $testRoot 'design-preview\feature-checks.json') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $testRoot 'data\core-api-checks.json') -Destination $output -Force
& $dotnet build (Join-Path $PSScriptRoot 'FlowVPN.WinUI.csproj') -c $configuration --nologo --configfile (Join-Path $PSScriptRoot 'NuGet.Config') "-p:AppxPackageDir=$generated\" -p:AppxSymbolPackageEnabled=false -v quiet
if ($LASTEXITCODE -ne 0) { throw 'MSIX build failed' }
$packages = @(Get-ChildItem -LiteralPath $generated -Recurse -Filter "*$configuration.msix")
if ($packages.Count -ne 1) { throw 'Expected exactly one new MSIX output' }
$packageName = if ($Preview) { 'FlowVPN-Preview' } else { 'FlowVPN' }
$suffix = if ($Unsigned) { '-unsigned' } else { '' }
$final = Join-Path $output ("{0}-{1}-x64{2}.msix" -f $packageName, $packageVersion.ToString(3), $suffix)
Copy-Item -LiteralPath $packages[0].FullName -Destination $final -Force
if (!$Unsigned) {
    $signingDirectory = if ($Preview) { 'work\msix-signing' } else { 'work\signing-release' }
    $pfxName = if ($Preview) { 'FlowVPN-Development.pfx' } else { 'FlowVPN.pfx' }
    $pfx = Join-Path (Join-Path $workspace $signingDirectory) $pfxName
    $passwordFile = Join-Path (Join-Path $workspace $signingDirectory) 'password.dpapi'
    if (!(Test-Path -LiteralPath $pfx) -or !(Test-Path -LiteralPath $passwordFile)) { throw 'Local signing key is unavailable; use -Unsigned to build without signing.' }
    $password = [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($passwordFile),$null,[Security.Cryptography.DataProtectionScope]::CurrentUser))
    try {
        $signTool = Join-Path $env:NUGET_PACKAGES 'microsoft.windows.sdk.buildtools\10.0.26100.9457\bin\10.0.26100.0\x64\signtool.exe'
        & $signTool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /f $pfx /p $password $final
        if ($LASTEXITCODE -ne 0) { throw 'MSIX signing failed' }
    } finally { $password = $null }
}
& (Join-Path $PSScriptRoot 'Distribution\Test-Msix.ps1') -PackagePath $final -ExpectedMode $variant -ExpectedVersion $packageVersion -AllowUnsigned:$Unsigned
if ($FeedBaseUri) {
    if ($Unsigned) { throw 'An unsigned package cannot produce an update feed' }
    $channel = if ($Preview) { 'preview' } else { 'stable' }
    $feedArguments = @{ PackagePath = $final; BaseUri = $FeedBaseUri; Channel = $channel; OutputDirectory = (Join-Path $output 'feed') }
    if ($Preview) { $feedArguments.AllowDevelopmentCertificate = $true }
    else { $feedArguments.PublicCertificate = Join-Path $PSScriptRoot 'Assets\FlowVPN.cer' }
    & (Join-Path $PSScriptRoot 'Distribution\New-UpdateFeed.ps1') @feedArguments
}
Write-Output "Package ready: $final"
