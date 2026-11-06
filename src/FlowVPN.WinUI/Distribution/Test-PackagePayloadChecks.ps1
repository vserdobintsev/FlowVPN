# Regression checks use temporary package copies. No installation or trust-store changes.
param([Parameter(Mandatory)][string]$PackagePath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.Security
if ($PSVersionTable.PSVersion.Major -ge 6) { Add-Type -AssemblyName System.Security.Cryptography.Pkcs }
$verify = Join-Path $PSScriptRoot 'Test-PackagePayload.ps1'
$source = (Resolve-Path -LiteralPath $PackagePath).Path
$positive = & $verify -PackagePath $source
if (!$positive.PayloadIntegrityVerified) { throw 'Valid package did not pass verification' }
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('FlowVPN-PayloadChecks-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $scratch
$copy = Join-Path $scratch 'tampered.msix'
function Read-CheckEntry($Zip, [string]$Name) {
    $stream = $Zip.GetEntry($Name).Open(); $memory = [IO.MemoryStream]::new()
    try { $stream.CopyTo($memory); return ,$memory.ToArray() }
    finally { $stream.Dispose(); $memory.Dispose() }
}
function Write-CheckEntry($Zip, [string]$Name, [byte[]]$Bytes) {
    $existing = $Zip.GetEntry($Name)
    if ($existing) { $existing.Delete() }
    $stream = $Zip.CreateEntry($Name).Open()
    try { $stream.Write($Bytes, 0, $Bytes.Length) } finally { $stream.Dispose() }
}
function Assert-PackageRejected([string]$ExpectedError) {
    $failure = $null
    try { $null = & $verify -PackagePath $copy } catch { $failure = $_.Exception.Message }
    if (!$failure -or !$failure.StartsWith($ExpectedError, [StringComparison]::Ordinal)) {
        throw "Expected rejection '$ExpectedError'; actual: $failure"
    }
}
try {
    Copy-Item -LiteralPath $source -Destination $copy
    $zip = [IO.Compression.ZipFile]::Open($copy, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $mapBytes = Read-CheckEntry $zip 'AppxBlockMap.xml'
        $xml = [Xml.XmlDocument]::new(); $xml.XmlResolver = $null
        $memory = [IO.MemoryStream]::new($mapBytes, $false)
        $settings = [Xml.XmlReaderSettings]::new(); $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit; $settings.XmlResolver = $null
        $reader = [Xml.XmlReader]::Create($memory, $settings)
        try { $xml.Load($reader) } finally { $reader.Dispose(); $memory.Dispose() }
        $ns = [Xml.XmlNamespaceManager]::new($xml.NameTable)
        $ns.AddNamespace('b', 'http://schemas.microsoft.com/appx/2010/blockmap')
        $ns.AddNamespace('b4', 'http://schemas.microsoft.com/appx/2021/blockmap')
        $file = $xml.SelectNodes('/b:BlockMap/b:File', $ns) | Where-Object {
            [long]$_.GetAttribute('Size') -gt 0 -and [long]$_.GetAttribute('Size') -le 4096 -and $_.GetAttribute('Name') -ne 'AppxManifest.xml'
        } | Select-Object -First 1
        if (!$file) { throw 'Test needs a small mapped payload file' }
        $name = $file.GetAttribute('Name').Replace('\', '/')
        $payload = Read-CheckEntry $zip $name
        $payload[0] = $payload[0] -bxor 1
        Write-CheckEntry $zip $name $payload
    } finally { $zip.Dispose() }
    Assert-PackageRejected 'Payload hash mismatch:'

    # An attacker also updates block and file hashes to match the replacement, keeping the old valid CMS.
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $replacementHash = [Convert]::ToBase64String($sha.ComputeHash($payload)) } finally { $sha.Dispose() }
    $file.SelectSingleNode('b:Block', $ns).SetAttribute('Hash', $replacementHash)
    $wholeFile = $file.SelectSingleNode('b4:FileHash', $ns)
    if ($wholeFile) { $wholeFile.SetAttribute('Hash', $replacementHash) }
    $zip = [IO.Compression.ZipFile]::Open($copy, [IO.Compression.ZipArchiveMode]::Update)
    try {
        Write-CheckEntry $zip 'AppxBlockMap.xml' ([Text.UTF8Encoding]::new($false).GetBytes($xml.OuterXml))
        $signature = Read-CheckEntry $zip 'AppxSignature.p7x'
        $cms = [Security.Cryptography.Pkcs.SignedCms]::new()
        $cms.Decode($signature[4..($signature.Length - 1)]); $cms.CheckSignature($true)
    } finally { $zip.Dispose() }
    Assert-PackageRejected 'Signed package digest mismatch: AppxBlockMap.xml'

    Copy-Item -LiteralPath $source -Destination $copy -Force
    $zip = [IO.Compression.ZipFile]::Open($copy, [IO.Compression.ZipArchiveMode]::Update)
    try { Write-CheckEntry $zip 'unmapped-injection.exe' ([byte[]]@(1, 2, 3)) } finally { $zip.Dispose() }
    Assert-PackageRejected 'Package entry is not authenticated:'
    [pscustomobject]@{
        ValidPackageAccepted = $true
        ChangedPayloadRejected = $true
        RehashedPayloadWithOldSignatureRejected = $true
        UnmappedPayloadRejected = $true
        CertificateStoresModified = $false
        InstalledPackages = $false
        PowerShellVersion = $PSVersionTable.PSVersion.ToString()
    }
} finally {
    # Only the exact file and empty directory created above are removed; never recurse.
    if (Test-Path -LiteralPath $copy) { Remove-Item -LiteralPath $copy -Force }
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch }
}
