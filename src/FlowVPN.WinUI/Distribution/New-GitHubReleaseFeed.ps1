# Creates release assets for an already trusted, timestamped MSIX. Does not publish anything.
param(
    [Parameter(Mandatory)][string]$PackagePath,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9_.-]+$')][string]$Repository,
    [Parameter(Mandatory)][ValidatePattern('^v[0-9]+\.[0-9]+\.[0-9]+$')][string]$Tag,
    [Parameter(Mandatory)][string]$ExpectedPublisher,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$PublicCertificate
)
$ErrorActionPreference = 'Stop'
$package = (Resolve-Path -LiteralPath $PackagePath).Path
$signature = Get-AuthenticodeSignature -LiteralPath $package
if ($signature.Status -ne 'Valid' -or !$signature.TimeStamperCertificate) { throw 'A trusted Windows package signature and a timestamp are required for public release' }
if ($signature.SignerCertificate.Subject -ne $ExpectedPublisher -or $ExpectedPublisher -like '*Development*') { throw 'The public release signer does not match the configured publisher' }
$version = [version]($Tag.Substring(1) + '.0')
& (Join-Path $PSScriptRoot 'Test-Msix.ps1') -PackagePath $package -ExpectedMode Release -ExpectedVersion $version

$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$parent = [IO.Path]::GetDirectoryName($output)
$staging = Join-Path $parent ('.flowvpn-feed-' + [guid]::NewGuid().ToString('N'))
$baseUri = [uri]("https://github.com/$Repository/releases/download/$Tag/")
try {
    $packageUrl = [uri]::new($baseUri, [uri]::EscapeDataString([IO.Path]::GetFileName($package))).AbsoluteUri
    $installerUrl = "https://github.com/$Repository/releases/latest/download/FlowVPN.appinstaller"
    $feedUrl = "https://github.com/$Repository/releases/latest/download/FlowVPN-stable.json"
    $pinArguments=@{}
    if($PublicCertificate){$pinArguments.PublicCertificate=(Resolve-Path -LiteralPath $PublicCertificate).Path}
    & (Join-Path $PSScriptRoot 'New-UpdateFeed.ps1') -PackagePath $package -BaseUri $baseUri -Channel stable -OutputDirectory $staging -PackageUri $packageUrl -AppInstallerUri $installerUrl -FeedUri $feedUrl @pinArguments
    Copy-Item -LiteralPath (Join-Path $staging 'stable\latest.json') -Destination (Join-Path $output 'FlowVPN-stable.json') -Force
    Copy-Item -LiteralPath (Join-Path $staging 'stable\FlowVPN.appinstaller') -Destination (Join-Path $output 'FlowVPN.appinstaller') -Force
    $reportPath = Join-Path ([IO.Path]::GetDirectoryName($package)) 'package-checks.json'
    $reportTarget = Join-Path $output 'package-checks.json'
    if ($reportPath -ne $reportTarget) { Copy-Item -LiteralPath $reportPath -Destination $reportTarget -Force }
    $assets = @($package, (Join-Path $output 'FlowVPN-stable.json'), (Join-Path $output 'FlowVPN.appinstaller'), $reportTarget)
    $checksums = foreach ($asset in $assets) { (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash + '  ' + [IO.Path]::GetFileName($asset) }
    $checksums | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding utf8NoBOM
} finally {
    if (Test-Path -LiteralPath $staging) {
        $resolved = (Resolve-Path -LiteralPath $staging).Path
        if ($resolved -ne [IO.Path]::GetFullPath($staging) -or !$resolved.StartsWith($parent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe temporary feed cleanup path' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
Write-Output "Release assets prepared in $output. Nothing was published."
