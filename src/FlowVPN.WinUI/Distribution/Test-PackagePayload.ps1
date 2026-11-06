# Verifies signed payload bytes without installing a package or modifying certificate stores.
# Windows still validates ZIP-container signature, certificate trust and deployment policy on install.
param([Parameter(Mandatory)][string]$PackagePath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.Security
if ($PSVersionTable.PSVersion.Major -ge 6) { Add-Type -AssemblyName System.Security.Cryptography.Pkcs }

if (-not ('FlowVpnDistribution.SignedAppxDigests' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
namespace FlowVpnDistribution {
    public static class SignedAppxDigests {
        [StructLayout(LayoutKind.Sequential)] private struct Blob { public uint Size; public IntPtr Data; }
        [StructLayout(LayoutKind.Sequential)] private struct ObjectBlob { public IntPtr Oid; public Blob Value; }
        [StructLayout(LayoutKind.Sequential)] private struct Indirect { public ObjectBlob Data; public ObjectBlob Algorithm; public Blob Digest; }
        [DllImport("crypt32.dll", SetLastError=true)]
        private static extern bool CryptDecodeObjectEx(uint encoding, IntPtr type, byte[] input, uint size, uint flags, IntPtr parameters, out IntPtr decoded, ref uint decodedSize);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);

        public static Dictionary<string, byte[]> Decode(byte[] input) {
            if (input == null || input.Length == 0 || input.Length > 2097152) throw new InvalidDataException("Invalid signed content length");
            IntPtr decoded; uint size = 0;
            // CryptoAPI SPC_INDIRECT_DATA_CONTENT_STRUCT; native parsing also works in Windows PowerShell 5.1.
            if (!CryptDecodeObjectEx(0x10001, new IntPtr(2003), input, (uint)input.Length, 0x8000, IntPtr.Zero, out decoded, ref size))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try {
                var value = (Indirect)Marshal.PtrToStructure(decoded, typeof(Indirect));
                if (Marshal.PtrToStringAnsi(value.Algorithm.Oid) != "2.16.840.1.101.3.4.2.1" ||
                    Marshal.PtrToStringAnsi(value.Data.Oid) != "1.3.6.1.4.1.311.2.1.30")
                    throw new InvalidDataException("Expected SHA256 APPX indirect data");
                if (value.Digest.Size != 148 && value.Digest.Size != 184) throw new InvalidDataException("Invalid APPX digest table size");
                var bytes = new byte[value.Digest.Size];
                Marshal.Copy(value.Digest.Data, bytes, 0, bytes.Length);
                if (Encoding.ASCII.GetString(bytes, 0, 4) != "APPX") throw new InvalidDataException("Invalid APPX digest header");
                var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                for (int offset = 4; offset < bytes.Length; offset += 36) {
                    string name = Encoding.ASCII.GetString(bytes, offset, 4);
                    if ((name != "AXPC" && name != "AXCD" && name != "AXCT" && name != "AXBM" && name != "AXCI") || result.ContainsKey(name))
                        throw new InvalidDataException("Unexpected or duplicate APPX digest");
                    var hash = new byte[32]; Array.Copy(bytes, offset + 4, hash, 0, hash.Length); result.Add(name, hash);
                }
                foreach (string name in new[] { "AXPC", "AXCD", "AXCT", "AXBM" })
                    if (!result.ContainsKey(name)) throw new InvalidDataException("Required APPX digest is missing");
                return result;
            } finally { LocalFree(decoded); }
        }
    }
}
'@
}

function Get-PackageEntryName([string]$Name) {
    $normalized = $Name.Replace('\', '/')
    if (!$normalized -or $normalized.Length -gt 32767 -or $normalized -match '[\x00-\x1f:<>"|?*]') { throw 'Unsafe package entry name' }
    foreach ($part in $normalized.Split('/')) {
        if (!$part -or $part -eq '.' -or $part -eq '..' -or $part.EndsWith('.') -or $part.EndsWith(' ')) { throw 'Unsafe package entry name' }
    }
    return $normalized
}
function Read-PackageEntry([string]$Name, [long]$Limit) {
    if (!$entries.ContainsKey($Name)) { throw "Required package entry is missing: $Name" }
    $entry = $entries[$Name]
    if ($entry.Length -gt $Limit) { throw "Package metadata is too large: $Name" }
    $stream = $entry.Open(); $memory = [IO.MemoryStream]::new()
    try {
        $buffer = [byte[]]::new(65536)
        while (($count = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            if ($memory.Length + $count -gt $Limit) { throw "Package metadata is too large: $Name" }
            $memory.Write($buffer, 0, $count)
        }
        if ($memory.Length -ne $entry.Length) { throw "Package entry size mismatch: $Name" }
        return ,$memory.ToArray()
    } finally { $stream.Dispose(); $memory.Dispose() }
}
function Read-PackageXml([byte[]]$Bytes) {
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.MaxCharactersInDocument = 16MB
    $stream = [IO.MemoryStream]::new($Bytes, $false)
    $reader = [Xml.XmlReader]::Create($stream, $settings)
    try {
        $document = [Xml.XmlDocument]::new(); $document.XmlResolver = $null; $document.Load($reader)
        return ,$document
    } finally { $reader.Dispose(); $stream.Dispose() }
}
function Test-SignedFootprint([string]$Name, [string]$DigestName, [long]$Limit) {
    $bytes = Read-PackageEntry $Name $Limit
    if ([Convert]::ToBase64String($sha.ComputeHash($bytes)) -cne [Convert]::ToBase64String($digests[$DigestName])) {
        throw "Signed package digest mismatch: $Name"
    }
    return ,$bytes
}

$package = (Resolve-Path -LiteralPath $PackagePath).Path
if ((Get-Item -LiteralPath $package).Length -gt 700MB) { throw 'Package exceeds the download size limit' }
$archive = [IO.Compression.ZipFile]::OpenRead($package)
$sha = [Security.Cryptography.SHA256]::Create()
try {
    if ($archive.Entries.Count -gt 20000) { throw 'Package has too many entries' }
    $entries = [Collections.Generic.Dictionary[string, IO.Compression.ZipArchiveEntry]]::new([StringComparer]::OrdinalIgnoreCase)
    [long]$totalBytes = 0
    foreach ($entry in $archive.Entries) {
        $name = Get-PackageEntryName $entry.FullName
        if ($entries.ContainsKey($name)) { throw 'Duplicate package entry' }
        $entries.Add($name, $entry)
        if ($entry.Length -lt 0 -or $entry.Length -gt 2GB) { throw 'Invalid uncompressed package size' }
        $totalBytes += $entry.Length
        if ($totalBytes -gt 2GB) { throw 'Uncompressed package exceeds the verification limit' }
    }
    $signature = Read-PackageEntry 'AppxSignature.p7x' 2MB
    if ($signature.Length -lt 5 -or [Text.Encoding]::ASCII.GetString($signature, 0, 4) -cne 'PKCX') { throw 'Invalid package signature header' }
    $cms = [Security.Cryptography.Pkcs.SignedCms]::new()
    $cms.Decode($signature[4..($signature.Length - 1)])
    if ($cms.SignerInfos.Count -ne 1 -or !$cms.SignerInfos[0].Certificate) { throw 'Expected exactly one package signer' }
    $cms.CheckSignature($true)
    if ($cms.ContentInfo.ContentType.Value -ne '1.3.6.1.4.1.311.2.1.4') { throw 'Unexpected signed content type' }
    $digests = [FlowVpnDistribution.SignedAppxDigests]::Decode($cms.ContentInfo.Content)
    # Bind metadata to the verified CMS before trusting any hash recorded in its block map.
    $mapBytes = Test-SignedFootprint 'AppxBlockMap.xml' 'AXBM' 16MB
    $null = Test-SignedFootprint '[Content_Types].xml' 'AXCT' 1MB
    $hasCatalog = $entries.ContainsKey('AppxMetadata/CodeIntegrity.cat')
    if ($hasCatalog -ne $digests.ContainsKey('AXCI')) { throw 'Code integrity catalog and signed digest disagree' }
    if ($hasCatalog) { $null = Test-SignedFootprint 'AppxMetadata/CodeIntegrity.cat' 'AXCI' 16MB }

    $map = Read-PackageXml $mapBytes
    $mapNamespace = 'http://schemas.microsoft.com/appx/2010/blockmap'
    if ($map.DocumentElement.LocalName -ne 'BlockMap' -or $map.DocumentElement.NamespaceURI -ne $mapNamespace -or
        $map.DocumentElement.GetAttribute('HashMethod') -ne 'http://www.w3.org/2001/04/xmlenc#sha256') { throw 'Invalid SHA256 block map' }
    $mapped = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $footprints = @('AppxBlockMap.xml', '[Content_Types].xml', 'AppxSignature.p7x', 'AppxMetadata/CodeIntegrity.cat')
    $buffer = [byte[]]::new(65536); $checkedBlocks = 0
    foreach ($file in $map.DocumentElement.ChildNodes) {
        if ($file.NodeType -ne [Xml.XmlNodeType]::Element) { continue }
        if ($file.LocalName -ne 'File' -or $file.NamespaceURI -ne $mapNamespace) { throw 'Unexpected block map element' }
        $name = Get-PackageEntryName $file.GetAttribute('Name')
        if (!$mapped.Add($name) -or $name -in $footprints) { throw 'Duplicate or reserved block map file' }
        [long]$fileSize = 0
        if (![long]::TryParse($file.GetAttribute('Size'), [ref]$fileSize) -or $fileSize -lt 0 -or
            !$entries.ContainsKey($name) -or $entries[$name].Length -ne $fileSize) { throw "Mapped file size mismatch: $name" }
        $stream = $entries[$name].Open(); $fileSha = [Security.Cryptography.SHA256]::Create()
        try {
            [long]$remaining = $fileSize; $wholeFileHash = $null
            foreach ($block in $file.ChildNodes) {
                if ($block.NodeType -ne [Xml.XmlNodeType]::Element) { continue }
                if ($block.LocalName -eq 'FileHash' -and $block.NamespaceURI -eq 'http://schemas.microsoft.com/appx/2021/blockmap') {
                    if ($null -ne $wholeFileHash) { throw 'Duplicate whole-file hash' }
                    $wholeFileHash = [Convert]::FromBase64String($block.GetAttribute('Hash'))
                    if ($wholeFileHash.Length -ne 32) { throw 'Invalid whole-file hash length' }
                    continue
                }
                if ($block.LocalName -ne 'Block' -or $block.NamespaceURI -ne $mapNamespace -or $remaining -le 0) { throw 'Unexpected block map block' }
                $expectedHash = [Convert]::FromBase64String($block.GetAttribute('Hash'))
                if ($expectedHash.Length -ne 32) { throw 'Invalid block hash length' }
                $count = [int][Math]::Min($buffer.Length, $remaining); $read = 0
                while ($read -lt $count) {
                    $n = $stream.Read($buffer, $read, $count - $read)
                    if ($n -eq 0) { throw "Truncated package payload: $name" }; $read += $n
                }
                if ([Convert]::ToBase64String($sha.ComputeHash($buffer, 0, $count)) -cne [Convert]::ToBase64String($expectedHash)) { throw "Payload hash mismatch: $name" }
                $null = $fileSha.TransformBlock($buffer, 0, $count, $null, 0)
                $remaining -= $count; $checkedBlocks++
            }
            if ($remaining -ne 0 -or $stream.ReadByte() -ne -1) { throw "Unmapped payload data: $name" }
            $null = $fileSha.TransformFinalBlock([byte[]]::new(0), 0, 0)
            if ($null -ne $wholeFileHash -and [Convert]::ToBase64String($fileSha.Hash) -cne [Convert]::ToBase64String($wholeFileHash)) { throw "Whole-file hash mismatch: $name" }
        } finally { $stream.Dispose(); $fileSha.Dispose() }
    }
    foreach ($name in $entries.Keys) {
        if (!$mapped.Contains($name) -and $name -notin $footprints) { throw "Package entry is not authenticated: $name" }
    }
    if (!$mapped.Contains('AppxManifest.xml')) { throw 'Manifest is not authenticated by the block map' }
    $manifest = Read-PackageXml (Read-PackageEntry 'AppxManifest.xml' 1MB)
    $ns = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
    $ns.AddNamespace('a', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
    $identities = $manifest.SelectNodes('/a:Package/a:Identity', $ns)
    if ($identities.Count -ne 1) { throw 'Expected a single package identity' }
    $identity = $identities[0]
    $signer = $cms.SignerInfos[0].Certificate
    if ($identity.GetAttribute('Publisher') -cne $signer.Subject) { throw 'Signature publisher does not match the authenticated manifest' }
    [pscustomobject]@{
        PayloadIntegrityVerified = $true
        FilesVerified = $mapped.Count
        BlocksVerified = $checkedBlocks
        Publisher = $signer.Subject
        Identity = $identity.GetAttribute('Name')
        Version = $identity.GetAttribute('Version')
        SignerCertificateSha256 = ([BitConverter]::ToString($sha.ComputeHash($signer.RawData))).Replace('-', '')
        SignatureTrustValidated = $false
        ZipContainerSignatureVerified = $false
        VerificationNote = 'CMS signature, signed footprint digests and all payload bytes verified. Windows still validates container, trust and deployment policy during installation.'
    }
} finally { $sha.Dispose(); $archive.Dispose() }
