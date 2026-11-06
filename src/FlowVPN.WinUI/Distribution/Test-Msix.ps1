param(
    [Parameter(Mandatory)][string]$PackagePath,
    [ValidateSet('Release','Preview')][string]$ExpectedMode = 'Release',
    [version]$ExpectedVersion,
    [switch]$AllowUnsigned
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.Security.Cryptography.Pkcs
function Read-ZipBytes($Entry) {
    if ($null -eq $Entry) { throw 'Required package entry is missing' }
    $memory = [IO.MemoryStream]::new()
    $stream = $Entry.Open()
    try { $stream.CopyTo($memory); return ,$memory.ToArray() }
    finally { $stream.Dispose(); $memory.Dispose() }
}
function Read-AssemblyBuildInfo($Entry) {
    # Inspect the compiled binary without loading or executing the packaged application.
    $stream = [IO.MemoryStream]::new((Read-ZipBytes $Entry), $false)
    $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
    try {
        $reader = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        $assembly = $reader.GetAssemblyDefinition()
        $modes = @()
        foreach ($handle in $assembly.GetCustomAttributes()) {
            $attribute = $reader.GetCustomAttribute($handle)
            if ($attribute.Constructor.Kind -ne 'MemberReference') { continue }
            $constructor = $reader.GetMemberReference([System.Reflection.Metadata.MemberReferenceHandle]$attribute.Constructor)
            if ($constructor.Parent.Kind -ne 'TypeReference') { continue }
            $type = $reader.GetTypeReference([System.Reflection.Metadata.TypeReferenceHandle]$constructor.Parent)
            if ($reader.GetString($type.Name) -ne 'AssemblyMetadataAttribute' -or $reader.GetString($type.Namespace) -ne 'System.Reflection') { continue }
            $blob = $reader.GetBlobReader($attribute.Value)
            if ($blob.ReadUInt16() -ne 1) { throw 'Invalid assembly metadata' }
            $key = $blob.ReadSerializedString()
            $value = $blob.ReadSerializedString()
            if ($key -eq 'FlowVPN.PackageMode') { $modes += $value }
        }
        if ($modes.Count -ne 1) { throw 'Compiled package mode is missing or ambiguous' }
        return [pscustomobject]@{ Mode = $modes[0]; Version = $assembly.Version }
    } finally { $pe.Dispose(); $stream.Dispose() }
}
$zip = [IO.Compression.ZipFile]::OpenRead($PackagePath)
$sha = [Security.Cryptography.SHA256]::Create()
try {
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $zip.Entries) {
        if (!$names.Add($entry.FullName.Replace('\','/'))) { throw 'Duplicate package entry' }
    }
    [xml]$map = [Text.Encoding]::UTF8.GetString((Read-ZipBytes $zip.GetEntry('AppxBlockMap.xml')))
    if ($map.BlockMap.HashMethod -ne 'http://www.w3.org/2001/04/xmlenc#sha256') { throw 'Unexpected block map hash method' }
    $checkedFiles = 0; $checkedBlocks = 0
    foreach ($file in $map.BlockMap.File) {
        $entry = $zip.GetEntry($file.Name.Replace('\','/'))
        if ($null -eq $entry) { throw "Missing package file: $($file.Name)" }
        if ($entry.Length -ne [long]$file.Size) { throw "File size mismatch: $($file.Name)" }
        $stream = $entry.Open(); $buffer = [byte[]]::new(65536)
        try {
            foreach ($block in $file.Block) {
                $read = 0
                while ($read -lt $buffer.Length) {
                    $n = $stream.Read($buffer,$read,$buffer.Length-$read)
                    if ($n -eq 0) { break }; $read += $n
                }
                $hash = $sha.ComputeHash($buffer,0,$read)
                if ([Convert]::ToBase64String($hash) -ne $block.Hash) { throw "Hash mismatch: $($file.Name)" }
                $checkedBlocks++
            }
            if ($stream.ReadByte() -ne -1) { throw "Unmapped data in $($file.Name)" }
        } finally { $stream.Dispose() }
        $checkedFiles++
    }
    $signatureEntry = $zip.GetEntry('AppxSignature.p7x')
    $signatureValid = $null
    $signer = $null
    if ($signatureEntry) {
        $signature = Read-ZipBytes $signatureEntry
        if ($signature.Length -lt 5 -or [Text.Encoding]::ASCII.GetString($signature,0,4) -ne 'PKCX') { throw 'Invalid package signature header' }
        $cms = [Security.Cryptography.Pkcs.SignedCms]::new()
        $cms.Decode($signature[4..($signature.Length-1)])
        $cms.CheckSignature($true) # Cryptographic CMS check only; no trust-store change or Windows install validation.
        if ($cms.SignerInfos.Count -ne 1) { throw 'Expected exactly one package signer' }
        $signer = $cms.SignerInfos[0].Certificate.Subject
        $signatureValid = $true
    } elseif (!$AllowUnsigned) { throw 'Package signature is missing; use -AllowUnsigned only for an intentionally unsigned build' }
    $privateFiles = @($zip.Entries | Where-Object { $_.FullName -match '(?i)(^|/)(settings\.dat$|data/|logs/)|\.ovpn$|\.pfx$|\.key$|\.pem$' })
    if ($privateFiles.Count -ne 0) { throw 'Private configuration detected in package' }
    [xml]$manifest = [Text.Encoding]::UTF8.GetString((Read-ZipBytes $zip.GetEntry('AppxManifest.xml'))).TrimStart([char]0xFEFF)
    if ($signer -and $manifest.Package.Identity.Publisher -ne $signer) { throw 'Publisher mismatch' }
    $identity = if ($ExpectedMode -eq 'Preview') { 'FlowVPN.Preview' } else { 'FlowVPN' }
    if ($manifest.Package.Identity.Name -ne $identity) { throw 'Package identity does not match the expected mode' }
    $build = Read-AssemblyBuildInfo $zip.GetEntry('FlowVPN.dll')
    if ($build.Mode -ne $ExpectedMode) { throw 'Compiled application mode does not match the package' }
    $version = [version]$manifest.Package.Identity.Version
    if ($build.Version -ne $version -or ($ExpectedVersion -and $version -ne $ExpectedVersion)) { throw 'Package and assembly versions disagree' }
    $capabilities = @($manifest.Package.Capabilities.ChildNodes | ForEach-Object { $_.GetAttribute('Name') })
    if ('runFullTrust' -notin $capabilities -or ('allowElevation' -in $capabilities) -ne ($ExpectedMode -eq 'Release')) { throw 'Unexpected package capabilities' }
    if ($zip.GetEntry('preview-only.flag')) { throw 'A portable preview marker must not be included in MSIX' }
    if (!$zip.GetEntry('coreclr.dll')) { throw 'Self-contained .NET runtime is missing' }
    $lock = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\engine.lock.json') -Raw | ConvertFrom-Json
    foreach ($file in $lock.Files.PSObject.Properties) {
        $entry = $zip.GetEntry('core/' + $file.Name)
        if (!$entry) { throw "Bundled engine file is missing: $($file.Name)" }
        $stream = $entry.Open()
        try { if ([Convert]::ToHexString($sha.ComputeHash($stream)) -ne $file.Value) { throw "Bundled engine lock mismatch: $($file.Name)" } }
        finally { $stream.Dispose() }
    }
    $report = [ordered]@{
        Package = [IO.Path]::GetFileName($PackagePath)
        SizeBytes = (Get-Item -LiteralPath $PackagePath).Length
        SHA256 = (Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256).Hash
        FilesVerified = $checkedFiles
        BlocksVerified = $checkedBlocks
        CmsSignatureValid = $signatureValid
        SignatureTrustValidated = $false
        Signer = $signer
        InstalledByBuild = $false
        InstalledLaunchTested = $false
        CertificateAddedToTrustStore = $false
        ForbiddenPrivateFileNamesFound = $privateFiles.Count
        PrivateFileScanNote = 'Checks file-name patterns, not the semantic content of every file.'
        HasDotNetRuntime = $true
        BundledEngineLockVerified = $true
        CoreVersion = $lock.Version
        Identity = $manifest.Package.Identity.Name
        Version = $version.ToString()
        CompiledPackageMode = $build.Mode
        ElevationCapability = ('allowElevation' -in $capabilities)
    }
    $report | ConvertTo-Json | Set-Content -LiteralPath (Join-Path (Split-Path $PackagePath) 'package-checks.json') -Encoding utf8
    $report | ConvertTo-Json
} finally { $sha.Dispose(); $zip.Dispose() }
