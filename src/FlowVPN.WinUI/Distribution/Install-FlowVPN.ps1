#requires -Version 5.1
[CmdletBinding(SupportsShouldProcess=$true)]
param(
    [Parameter(Mandatory=$true)][string]$PackagePath,
    [Parameter(Mandatory=$true)][string]$CertificatePath,
    [ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedCertificateSha256,
    [switch]$ValidateOnly,
    [Parameter(DontShow=$true)][switch]$TrustOnly,
    [Parameter(DontShow=$true)][string]$RequestingUserSid
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
if ($PSVersionTable.PSEdition -eq 'Desktop') { Add-Type -AssemblyName System.Security }
else { Add-Type -AssemblyName System.Security.Cryptography.Pkcs }

function Get-CurrentSid {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try { return $identity.User.Value } finally { $identity.Dispose() }
}
function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try { return ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
    finally { $identity.Dispose() }
}
function Get-BytesSha256([byte[]]$Bytes) {
    $hash = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($hash.ComputeHash($Bytes)).Replace('-','') } finally { $hash.Dispose() }
}
function Test-PeerTrusted([string]$Thumbprint, [string]$CertificateHash) {
    $store = [Security.Cryptography.X509Certificates.X509Store]::new('TrustedPeople','LocalMachine')
    try {
        $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
        foreach ($candidate in $store.Certificates.Find([Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,$Thumbprint,$false)) {
            if ((Get-BytesSha256 $candidate.RawData) -eq $CertificateHash) { return $true }
        }
        return $false
    } finally { $store.Close() }
}
function Read-ZipEntry($Archive, [string]$Name) {
    $entry = $Archive.GetEntry($Name)
    if (!$entry) { throw "В пакете отсутствует $Name." }
    if ($entry.Length -gt 4MB) { throw "Служебный файл $Name превышает допустимый размер." }
    $stream = $entry.Open()
    $memory = [IO.MemoryStream]::new()
    try {
        $buffer = New-Object byte[] 65536
        while (($read = $stream.Read($buffer,0,$buffer.Length)) -gt 0) {
            if ($memory.Length + $read -gt 4MB) { throw "Распакованный файл $Name превышает допустимый размер." }
            $memory.Write($buffer,0,$read)
        }
        return ,$memory.ToArray()
    } finally { $stream.Dispose(); $memory.Dispose() }
}
function Quote-PowerShellLiteral([string]$Value) { return "'" + $Value.Replace("'","''") + "'" }

$originalSid = Get-CurrentSid
if (!$ValidateOnly -and !$TrustOnly -and (Test-Administrator)) {
    throw 'Запустите Install-FlowVPN.cmd обычным способом, без «Запуск от имени администратора». Права для сертификата будут запрошены отдельно; само приложение устанавливается вашей учётной записи.'
}
$package = (Resolve-Path -LiteralPath $PackagePath).Path
$certificateFile = (Resolve-Path -LiteralPath $CertificatePath).Path
$packageLock = [IO.File]::Open($package,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
$certificateLock = $null
$certificate = $null
$codeLocks = [Collections.Generic.List[IO.FileStream]]::new()
try {
    $payloadVerifier = Join-Path $PSScriptRoot 'Test-PackagePayload.ps1'
    if (!(Test-Path -LiteralPath $payloadVerifier)) { throw 'Рядом с установщиком отсутствует Test-PackagePayload.ps1. Скачайте полный установочный комплект.' }
    foreach ($codeFile in @($PSCommandPath,$payloadVerifier)) {
        $codeLocks.Add([IO.File]::Open($codeFile,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read))
    }
    $certificateLock = [IO.File]::Open($certificateFile,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    if ($certificateLock.Length -gt 64KB) { throw 'Публичный сертификат превышает допустимый размер.' }
    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new([IO.File]::ReadAllBytes($certificateFile))
    if ($certificate.HasPrivateKey) { throw 'Используйте только публичный CER. Закрытый ключ и PFX не должны распространяться.' }
    $certificateHash = Get-BytesSha256 $certificate.RawData
    if ($ExpectedCertificateSha256 -and $certificateHash -ne $ExpectedCertificateSha256) { throw 'SHA-256 сертификата не совпадает с ожидаемым.' }
    $now = [DateTime]::UtcNow
    if ($now -lt $certificate.NotBefore.ToUniversalTime() -or $now -gt $certificate.NotAfter.ToUniversalTime()) { throw 'Сертификат ещё не действует или уже истёк.' }
    $usage = $certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' } | Select-Object -First 1
    if (!$usage -or !(@($usage.EnhancedKeyUsages | ForEach-Object { $_.Value }) -contains '1.3.6.1.5.5.7.3.3')) { throw 'Сертификат не разрешает подпись программ.' }
    $constraints = $certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.19' } | Select-Object -First 1
    if ($constraints -and $constraints.CertificateAuthority) { throw 'Сертификат центра сертификации нельзя использовать как сертификат издателя FlowVPN.' }
    $zip = [IO.Compression.ZipFile]::OpenRead($package)
    try {
        $signatureBytes = Read-ZipEntry $zip 'AppxSignature.p7x'
        if ($signatureBytes.Length -lt 5 -or [Text.Encoding]::ASCII.GetString($signatureBytes,0,4) -ne 'PKCX') { throw 'Некорректная подпись MSIX.' }
        $cms = [Security.Cryptography.Pkcs.SignedCms]::new()
        $cms.Decode($signatureBytes[4..($signatureBytes.Length-1)])
        $cms.CheckSignature($true)
        if ($cms.SignerInfos.Count -ne 1 -or (Get-BytesSha256 $cms.SignerInfos[0].Certificate.RawData) -ne $certificateHash) { throw 'MSIX подписан другим сертификатом.' }
        [xml]$manifest = [Text.Encoding]::UTF8.GetString((Read-ZipEntry $zip 'AppxManifest.xml')).TrimStart([char]0xFEFF)
        if ($manifest.Package.Identity.Name -ne 'FlowVPN' -or $manifest.Package.Identity.Publisher -ne $certificate.Subject) { throw 'Издатель или имя MSIX не соответствует FlowVPN и данному сертификату.' }
        $timestampPresent = $cms.SignerInfos[0].CounterSignerInfos.Count -gt 0 -or @($cms.SignerInfos[0].UnsignedAttributes | Where-Object { $_.Oid.Value -in @('1.3.6.1.4.1.311.3.3.1','1.2.840.113549.1.9.16.2.14') }).Count -gt 0
    } finally { $zip.Dispose() }
    $payload = & $payloadVerifier -PackagePath $package
    if (!$payload.PayloadIntegrityVerified) { throw 'Не удалось проверить связь содержимого MSIX с его цифровой подписью.' }
    $windowsSignature = Get-AuthenticodeSignature -LiteralPath $package
    $peerTrusted = Test-PeerTrusted $certificate.Thumbprint $certificateHash
    if ($ValidateOnly) {
        return [pscustomobject]@{
            Identity = [string]$manifest.Package.Identity.Name
            Version = [string]$manifest.Package.Identity.Version
            Publisher = $certificate.Subject
            CertificateSha256 = $certificateHash
            PackageSha256 = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash
            SignerMatchesPublicCertificate = $true
            PinnedCertificateMatched = [bool]$ExpectedCertificateSha256
            PayloadIntegrityVerified = $true
            TimestampPresent = $timestampPresent
            TrustedPeopleContainsCertificate = $peerTrusted
            WindowsSignatureStatus = $windowsSignature.Status.ToString()
            CertificateImported = $false
            PackageInstalled = $false
        }
    }
    if ($TrustOnly) {
        if (!(Test-Administrator) -or [string]::IsNullOrWhiteSpace($RequestingUserSid) -or !$ExpectedCertificateSha256) { throw 'Некорректный запрос повышенного помощника.' }
        $null = [Security.Principal.SecurityIdentifier]::new($RequestingUserSid)
        if (!$peerTrusted -and $PSCmdlet.ShouldProcess($certificate.Subject,'Добавить публичный сертификат в LocalMachine\TrustedPeople')) {
            Import-Certificate -FilePath $certificateFile -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
        }
        return
    }
    if (!$peerTrusted -and $windowsSignature.Status -ne 'Valid') {
        $message = "FlowVPN использует собственный сертификат издателя, а не сертификат общедоверенного центра.`n`nИздатель: $($certificate.Subject)`nSHA-256: $certificateHash`n`nРазрешить доверие этому сертификату в LocalMachine\TrustedPeople? Это влияет на все учётные записи этого компьютера. Trusted Root изменяться не будет. Windows отдельно запросит права администратора."
        if (!$PSCmdlet.ShouldProcess($certificate.Subject,'Добавить публичный сертификат в LocalMachine\TrustedPeople')) { return }
        if (!$PSCmdlet.ShouldContinue($message,'Однократное доверие сертификату FlowVPN')) { return }
        $arguments = '& ' + (Quote-PowerShellLiteral $PSCommandPath) + ' -PackagePath ' + (Quote-PowerShellLiteral $package) + ' -CertificatePath ' + (Quote-PowerShellLiteral $certificateFile) + ' -ExpectedCertificateSha256 ' + (Quote-PowerShellLiteral $certificateHash) + ' -RequestingUserSid ' + (Quote-PowerShellLiteral $originalSid) + ' -TrustOnly'
        $command = 'try { ' + $arguments + '; exit 0 } catch { exit 1 }'
        $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
        $start = [Diagnostics.ProcessStartInfo]::new()
        $start.FileName = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $start.Arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ' + $encoded
        $start.UseShellExecute = $true
        $start.Verb = 'runas'
        $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
        $child = [Diagnostics.Process]::Start($start)
        try { $child.WaitForExit(); if ($child.ExitCode -ne 0) { throw 'Не удалось добавить сертификат. Установка приложения не выполнялась.' } }
        finally { $child.Dispose() }
        if (!(Test-PeerTrusted $certificate.Thumbprint $certificateHash)) { throw 'Сертификат не найден в TrustedPeople после подтверждения.' }
    }
    if ((Get-CurrentSid) -ne $originalSid -or (Test-Administrator)) { throw 'Контекст пользователя изменился; установка остановлена.' }
    if ($PSCmdlet.ShouldProcess("FlowVPN $($manifest.Package.Identity.Version)","Установить MSIX для текущего пользователя $originalSid")) {
        # Windows performs its full package-signature and deployment validation here.
        Add-AppxPackage -Path $package -ErrorAction Stop
        Write-Host 'FlowVPN установлен. Его можно открыть через меню «Пуск». VPN подключается вручную.'
    }
} finally {
    foreach ($codeLock in $codeLocks) { $codeLock.Dispose() }
    if ($certificate) { $certificate.Dispose() }
    if ($certificateLock) { $certificateLock.Dispose() }
    $packageLock.Dispose()
}
