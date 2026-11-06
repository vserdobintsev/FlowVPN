# Validates a local signing key and optionally sends it to a GitHub environment.
# The default is read-only. Secret values are never printed or passed as CLI arguments.
#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string]$Repository,
    [Parameter(Mandatory)][string]$SigningDirectory,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_-]{0,49}$')][string]$Environment = 'release',
    [string]$GitHubCliPath,
    [switch]$Upload
)
$ErrorActionPreference = 'Stop'
$publicPath = Join-Path $PSScriptRoot '..\Assets\FlowVPN.cer'
$pfxPath = Join-Path $SigningDirectory 'FlowVPN.pfx'
$passwordPath = Join-Path $SigningDirectory 'password.dpapi'
foreach ($path in @($publicPath, $pfxPath, $passwordPath)) {
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw 'The public certificate, signing key, or protected password is missing' }
}
# Keep the Base64 value below GitHub's 48 KB secret limit.
if ((Get-Item -LiteralPath $pfxPath).Length -gt 32768) { throw 'The PFX is too large for the signing secret' }
if ((Get-Item -LiteralPath $passwordPath).Length -gt 65536) { throw 'The protected password file is too large' }

function Invoke-GitHubCommand([string[]]$Arguments, [AllowNull()][string]$InputValue) {
    $start = [Diagnostics.ProcessStartInfo]::new($script:gitHubExecutable)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment['GH_PROMPT_DISABLED'] = '1'
    $null = $start.Environment.Remove('GH_DEBUG')
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        # Consume both streams concurrently; neither is copied to a log or exception.
        $output = $process.StandardOutput.ReadToEndAsync()
        $errorOutput = $process.StandardError.ReadToEndAsync()
        if ($null -ne $InputValue) { $process.StandardInput.Write($InputValue) }
        $process.StandardInput.Close()
        if (!$process.WaitForExit(60000)) {
            $process.Kill($true)
            throw 'GitHub CLI timed out; no command output was recorded'
        }
        $null = $output.GetAwaiter().GetResult()
        $null = $errorOutput.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw 'GitHub CLI failed; check authentication, environment permissions, and network access' }
    } finally {
        try { if (!$process.HasExited) { $process.Kill($true) } }
        finally { $process.Dispose() }
    }
}

$passwordBytes = $null
$pfxBytes = $null
$password = $null
$pfxBase64 = $null
$privateCertificate = $null
$publicCertificate = $null
try {
    $passwordBytes = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($passwordPath), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    $password = [Text.Encoding]::UTF8.GetString($passwordBytes)
    if ([string]::IsNullOrEmpty($password) -or $passwordBytes.Length -gt 32768) { throw 'The signing password is empty or too large' }
    $pfxBytes = [IO.File]::ReadAllBytes($pfxPath)
    $privateCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($pfxBytes, $password, [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
    $publicCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($publicPath)
    $expectedPin = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($publicCertificate.RawData))
    $actualPin = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($privateCertificate.RawData))
    if (!$privateCertificate.HasPrivateKey -or $publicCertificate.HasPrivateKey -or $actualPin -cne $expectedPin) { throw 'The signing key does not match the checked-in public certificate' }
    [xml]$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\Package.appxmanifest') -Raw
    if ($privateCertificate.Subject -cne $manifest.Package.Identity.Publisher) { throw 'The signing certificate does not match the package publisher' }
    $now = [DateTime]::UtcNow
    if ($privateCertificate.NotBefore.ToUniversalTime() -gt $now -or $privateCertificate.NotAfter.ToUniversalTime() -le $now) { throw 'The signing certificate is not currently valid' }
    $usage = @($privateCertificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' })
    if ($usage.Count -ne 1 -or '1.3.6.1.5.5.7.3.3' -notin @($usage[0].EnhancedKeyUsages | ForEach-Object Value)) { throw 'The certificate must permit code signing' }

    if ($Upload) {
        $script:gitHubExecutable = if ($GitHubCliPath) {
            (Resolve-Path -LiteralPath $GitHubCliPath -ErrorAction Stop).ProviderPath
        } else {
            (Get-Command gh -CommandType Application -ErrorAction Stop).Source
        }
        if (!(Test-Path -LiteralPath $script:gitHubExecutable -PathType Leaf)) { throw 'GitHubCliPath must identify the GitHub CLI executable' }
        Invoke-GitHubCommand @('api', "repos/$Repository/environments/$Environment", '--method', 'GET', '--silent') $null
        $pfxBase64 = [Convert]::ToBase64String($pfxBytes)
        Invoke-GitHubCommand @('secret', 'set', 'PFX_BASE64', '--repo', $Repository, '--env', $Environment) $pfxBase64
        try {
            Invoke-GitHubCommand @('secret', 'set', 'PFX_PASSWORD', '--repo', $Repository, '--env', $Environment) $password
        } catch {
            throw 'PFX_BASE64 was uploaded, but PFX_PASSWORD could not be updated. Do not run a release until both secrets are set.'
        }
    }
    [pscustomobject]@{
        Repository = $Repository
        Environment = $Environment
        Publisher = $publicCertificate.Subject
        CertificateSha256 = $expectedPin
        ValidUntilUtc = $publicCertificate.NotAfter.ToUniversalTime().ToString('O')
        PrivateKeyMatches = $true
        Uploaded = [bool]$Upload
        Note = if ($Upload) { 'Both signing secrets were uploaded. Keep an independent backup of the PFX and password.' } else { 'Local validation only. No network request, secret upload, or certificate-store change was made.' }
    }
} finally {
    if ($privateCertificate) { $privateCertificate.Dispose() }
    if ($publicCertificate) { $publicCertificate.Dispose() }
    if ($passwordBytes) { [Array]::Clear($passwordBytes, 0, $passwordBytes.Length) }
    if ($pfxBytes) { [Array]::Clear($pfxBytes, 0, $pfxBytes.Length) }
    $password = $null
    $pfxBase64 = $null
}
