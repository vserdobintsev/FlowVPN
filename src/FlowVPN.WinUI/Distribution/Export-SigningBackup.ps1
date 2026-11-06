# Interactive export only: never called by the unattended build or release workflow.
#requires -Version 7.6
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SigningDirectory,
    [Parameter(Mandatory)][string]$DestinationDirectory,
    [string]$PublicCertificatePath = (Join-Path $PSScriptRoot '..\Assets\FlowVPN.cer')
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..')).TrimEnd('\')
$destination = [IO.Path]::GetFullPath($DestinationDirectory)
if ($destination -notmatch '^[A-Za-z]:\\' -or $destination.StartsWith($repositoryRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or $destination.TrimEnd('\') -ieq $repositoryRoot) {
    throw 'Choose a local backup directory outside the source repository'
}
if (Test-Path -LiteralPath $destination) { throw 'The backup destination already exists; choose a new directory' }
$drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($destination))
if ($drive.DriveType -notin @([IO.DriveType]::Fixed, [IO.DriveType]::Removable)) { throw 'Use a local fixed or removable drive for the protected export' }
$ancestor = [IO.DirectoryInfo]::new($destination).Parent
while ($null -ne $ancestor) {
    if ($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'The backup destination must not pass through a junction or symbolic link' }
    $ancestor = $ancestor.Parent
}
$pfxPath = Join-Path $SigningDirectory 'FlowVPN.pfx'
$passwordPath = Join-Path $SigningDirectory 'password.dpapi'
foreach ($path in @($pfxPath, $passwordPath, $PublicCertificatePath)) {
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw 'The signing key, protected password, or public certificate is missing' }
    if ((Get-Item -LiteralPath $path).Length -gt 1MB) { throw 'A signing input exceeds the size limit' }
}

function Test-SecurePasswordsEqual([Security.SecureString]$First, [Security.SecureString]$Second) {
    if ($First.Length -ne $Second.Length) { return $false }
    $firstPointer = [IntPtr]::Zero
    $secondPointer = [IntPtr]::Zero
    try {
        $firstPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($First)
        $secondPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Second)
        $difference = 0
        for ($index = 0; $index -lt $First.Length; $index++) {
            $difference = $difference -bor ([Runtime.InteropServices.Marshal]::ReadInt16($firstPointer, $index * 2) -bxor [Runtime.InteropServices.Marshal]::ReadInt16($secondPointer, $index * 2))
        }
        return $difference -eq 0
    } finally {
        if ($firstPointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($firstPointer) }
        if ($secondPointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($secondPointer) }
    }
}
function Write-NewBackupFile([string]$Name, [byte[]]$Bytes) {
    $stream = [IO.FileStream]::new((Join-Path $destination $Name), [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($Bytes, 0, $Bytes.Length); $stream.Flush($true) }
    finally { $stream.Dispose() }
}

$sourcePasswordBytes = $null
$sourcePassword = $null
$backupPassword = $null
$confirmation = $null
$backupPasswordPointer = [IntPtr]::Zero
$backupPasswordText = $null
$encryptedBackup = $null
$certificate = $null
$publicCertificate = $null
$roundTrip = $null
try {
    $sourcePasswordBytes = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($passwordPath), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    $sourcePassword = [Text.Encoding]::UTF8.GetString($sourcePasswordBytes)
    $flags = [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet -bor [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable
    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($pfxPath, $sourcePassword, $flags)
    $publicCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($PublicCertificatePath)
    $expectedPin = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($publicCertificate.RawData))
    $actualPin = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($certificate.RawData))
    if (!$certificate.HasPrivateKey -or $publicCertificate.HasPrivateKey -or $actualPin -cne $expectedPin) { throw 'The signing key does not match the public certificate' }

    Write-Host 'Создайте отдельный пароль резервной копии и сохраните его в менеджере паролей. Не отправляйте пароль в чат.'
    $backupPassword = Read-Host 'Новый пароль резервной копии (не менее 16 символов)' -AsSecureString
    $confirmation = Read-Host 'Повторите новый пароль' -AsSecureString
    if ($backupPassword.Length -lt 16) { throw 'The backup password must contain at least 16 characters' }
    if (!(Test-SecurePasswordsEqual $backupPassword $confirmation)) { throw 'The backup passwords do not match' }
    $backupPasswordPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($backupPassword)
    $backupPasswordText = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($backupPasswordPointer)
    $parameters = [Security.Cryptography.PbeParameters]::new([Security.Cryptography.PbeEncryptionAlgorithm]::Aes256Cbc, [Security.Cryptography.HashAlgorithmName]::SHA256, 200000)
    $encryptedBackup = $certificate.ExportPkcs12($parameters, $backupPasswordText)
    $roundTrip = [Security.Cryptography.X509Certificates.X509Certificate2]::new($encryptedBackup, $backupPasswordText, [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
    if (!$roundTrip.HasPrivateKey -or [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($roundTrip.RawData)) -cne $expectedPin) { throw 'The exported key failed recovery verification' }

    $null = [IO.Directory]::CreateDirectory($destination)
    if ($drive.DriveFormat -eq 'NTFS') {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        try {
            $acl = [Security.AccessControl.DirectorySecurity]::new()
            $acl.SetAccessRuleProtection($true, $false)
            $inheritance = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit
            $principals = @($identity.User, [Security.Principal.SecurityIdentifier]::new('S-1-5-18'), [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
            foreach ($principal in $principals) {
                $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($principal, [Security.AccessControl.FileSystemRights]::FullControl, $inheritance, [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow))
            }
            Set-Acl -LiteralPath $destination -AclObject $acl
        } finally { $identity.Dispose() }
    }
    Write-NewBackupFile 'FlowVPN.pfx' $encryptedBackup
    Write-NewBackupFile 'FlowVPN.cer' $publicCertificate.RawData
    $instructions = @"
# Резервная копия ключа FlowVPN

Создана: $([DateTime]::UtcNow.ToString('O')).
Издатель: $($publicCertificate.Subject).
SHA-256 открытого сертификата: $expectedPin.

FlowVPN.pfx содержит закрытый ключ и заново зашифрован новым паролем: AES-256-CBC, PBKDF2-SHA256, 200000 итераций.
FlowVPN.cer содержит только открытый сертификат. Пароль и исходный password.dpapi в комплект не включены.
Экспорт проверен обратным открытием в памяти; системные хранилища сертификатов не менялись.

## Хранение

1. Сохраните новый пароль в независимом менеджере паролей или другом защищённом месте отдельно от этой папки.
2. Перенесите весь комплект на отдельный носитель или в защищённое резервное хранилище. Копия на том же диске ещё не защищает от потери устройства.
3. После переноса сравните SHA-256 файлов с SHA256SUMS.txt. Не отправляйте PFX или пароль в чат, issue, репозиторий или публичные артефакты.

## Восстановление

Для расшифровки FlowVPN.pfx нужен именно новый пароль резервной копии. Исходная Windows-учётная запись, компьютер и password.dpapi не требуются.
Пароль вводите только локально при открытии PFX либо при настройке защищённых секретов выпуска. Не передавайте его аргументом команды.
GitHub workflow принимает PFX_BASE64 и PFX_PASSWORD: загрузите этот зашифрованный PFX и его новый пароль через Secrets защищённого environment release.
При восстановлении локальной подписи создайте новый password.dpapi под учётной записью нового компьютера; старый DPAPI-файл не является переносимым паролем.
Сверьте открытый сертификат с указанным SHA-256 и Assets/FlowVPN.cer доверенной копии исходников. Одного совпадения CN недостаточно.
Восстановление этого же ключа сохраняет прежнюю подпись. Новый сертификат для обычной потери компьютера не требуется.

Если ключ был скомпрометирован, резервную копию нельзя считать безопасной заменой: нужна ротация ключа и отдельное удаление старого доверия.
Подробная процедура: https://github.com/vserdobintsev/FlowVPN/blob/main/src/FlowVPN.WinUI/Distribution/SIGNING-RECOVERY.md
"@
    Write-NewBackupFile 'ВОССТАНОВЛЕНИЕ.md' ([Text.Encoding]::UTF8.GetBytes($instructions))
    $checksumLines = foreach ($name in @('FlowVPN.pfx', 'FlowVPN.cer', 'ВОССТАНОВЛЕНИЕ.md')) { (Get-FileHash -LiteralPath (Join-Path $destination $name) -Algorithm SHA256).Hash + '  ' + $name }
    Write-NewBackupFile 'SHA256SUMS.txt' ([Text.Encoding]::UTF8.GetBytes(($checksumLines -join "`n") + "`n"))
    [pscustomobject]@{
        Destination = $destination
        Publisher = $publicCertificate.Subject
        CertificateSha256 = $expectedPin
        RecoveryVerified = $true
        PasswordIncluded = $false
        IndependentStorageConfirmed = $false
        Note = 'Move this protected export to independent storage and keep its new password separately.'
    }
} finally {
    if ($roundTrip) { $roundTrip.Dispose() }
    if ($certificate) { $certificate.Dispose() }
    if ($publicCertificate) { $publicCertificate.Dispose() }
    if ($backupPassword) { $backupPassword.Dispose() }
    if ($confirmation) { $confirmation.Dispose() }
    if ($backupPasswordPointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($backupPasswordPointer) }
    if ($sourcePasswordBytes) { [Array]::Clear($sourcePasswordBytes, 0, $sourcePasswordBytes.Length) }
    if ($encryptedBackup) { [Array]::Clear($encryptedBackup, 0, $encryptedBackup.Length) }
    $sourcePassword = $null
    $backupPasswordText = $null
}
