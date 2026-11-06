# Copies only validated settings and known rule caches into an already installed MSIX.
# Run as the same Windows user who saved the source settings; never pass private data to GitHub.
#Requires -Version 7.4
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
param(
    [Parameter(Mandatory)][string]$SourceData,
    [string]$RulesSource,
    [ValidateSet('FlowVPN', 'FlowVPN.Preview', IgnoreCase = $false)][string]$PackageName = 'FlowVPN',
    [switch]$ValidateOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (!$IsWindows) { throw 'Settings import requires Windows.' }
$limit = 8MB
$clear = $null
$protected = $null
$stage = $null
$rulesCommitted = $false
$settingsCommitted = $false
$migrationGate = $null
$plan = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
$cacheBudget = [pscustomobject]@{ Bytes = 0L }

function Assert-LocalPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if ($full -notmatch '^[A-Za-z]:\\' -or $full.Substring(2).Contains(':')) { throw 'Only local filesystem paths are supported.' }
    if ([IO.DriveInfo]::new([IO.Path]::GetPathRoot($full)).DriveType -eq [IO.DriveType]::Network) { throw 'Network drives are not supported during settings import.' }
    $current = $full
    while ($current) {
        try {
            if ([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse points are not supported during settings import.' }
        } catch [IO.FileNotFoundException] { } catch [IO.DirectoryNotFoundException] { }
        $parent = [IO.Path]::GetDirectoryName($current.TrimEnd('\'))
        if ($parent -eq $current) { break }
        $current = $parent
    }
    return $full
}
function Read-Limited([string]$Path, [long]$Maximum) {
    $null = Assert-LocalPath $Path
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        if ($stream.Length -le 0 -or $stream.Length -gt $Maximum) { throw 'An import file exceeds its size limit or is empty.' }
        $bytes = [byte[]]::new([int]$stream.Length)
        $stream.ReadExactly($bytes, 0, $bytes.Length)
        return ,$bytes
    } finally { $stream.Dispose() }
}
function Parse-Json([byte[]]$Bytes) {
    try {
        $text = [Text.UTF8Encoding]::new($false, $true).GetString($Bytes)
        $node = [Text.Json.Nodes.JsonNode]::Parse($text)
        if ($null -eq $node) { throw 'Empty JSON' }
        return ,$node
    } catch { throw 'An import file contains invalid JSON or UTF-8.' }
}
function Require-Kind($Node, [string]$Kind) {
    if ($null -eq $Node -or $Node.GetValueKind().ToString() -ne $Kind) { throw 'Invalid settings or cache metadata structure.' }
}
function Check-Strings($Object, [string[]]$Names) {
    foreach ($name in $Names) { if ($Object.ContainsKey($name)) { Require-Kind $Object[$name] 'String' } }
}
function Check-Boolean($Object, [string[]]$Names) {
    foreach ($name in $Names) {
        if ($Object.ContainsKey($name) -and ($null -eq $Object[$name] -or $Object[$name].GetValueKind().ToString() -notin @('True', 'False'))) {
            throw 'Invalid boolean setting.'
        }
    }
}
function Check-Settings($Settings) {
    Require-Kind $Settings 'Object'
    $object = $Settings.AsObject()
    Check-Strings $object @('BaseDomainCategory', 'SelectedProfileId')
    Check-Boolean $object @('BaseRulesEnabled', 'AutoUpdateRules', 'StartInTray')
    foreach ($name in @('Version', 'Appearance')) {
        if ($object.ContainsKey($name)) {
            Require-Kind $object[$name] 'Number'
            $integer = 0
            if (![int]::TryParse($object[$name].ToString(), [ref]$integer)) { throw 'Invalid numeric setting.' }
            if (($name -eq 'Version' -and $integer -lt 1) -or ($name -eq 'Appearance' -and $integer -notin @(0,1,2))) { throw 'Unsupported settings version or appearance.' }
        }
    }
    foreach ($collection in @('Sources', 'Profiles')) {
        Require-Kind $object[$collection] 'Array'
        $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($entry in $object[$collection].AsArray()) {
            Require-Kind $entry 'Object'
            $item = $entry.AsObject()
            Require-Kind $item['Id'] 'String'
            if ([string]::IsNullOrWhiteSpace($item['Id'].ToString()) -or !$ids.Add($item['Id'].ToString())) { throw 'Missing or duplicate settings identifier.' }
            if ($collection -eq 'Sources') {
                Check-Strings $item @('Name','Kind','DynamicKey','ConnectionJson','Origin','Fingerprint')
                if ($item.ContainsKey('Kind') -and $item['Kind'].ToString() -cnotin @('OpenVPN','Outline')) { throw 'Unsupported connection type.' }
            } else {
                Check-Strings $item @('Name','SourceId','Mode','Icon')
                if ($item.ContainsKey('Mode') -and $item['Mode'].ToString() -cnotin @('include','exclude','all')) { throw 'Unsupported profile mode.' }
                if ($item.ContainsKey('IconColor')) {
                    Require-Kind $item['IconColor'] 'Number'
                    $integer = 0
                    if (![int]::TryParse($item['IconColor'].ToString(), [ref]$integer)) { throw 'Invalid profile color.' }
                }
                if ($item.ContainsKey('Apps')) {
                    Require-Kind $item['Apps'] 'Array'
                    foreach ($app in $item['Apps'].AsArray()) {
                        Require-Kind $app 'Object'
                        Check-Strings $app.AsObject() @('Name','Path','Pattern','GroupId','GroupName','IconPath','ComponentRole','ScopeNote')
                    }
                }
                if ($item.ContainsKey('Domains')) {
                    Require-Kind $item['Domains'] 'Array'
                    foreach ($domain in $item['Domains'].AsArray()) { Require-Kind $domain 'String' }
                }
            }
        }
    }
    if ($object.ContainsKey('Updates')) {
        Require-Kind $object['Updates'] 'Object'
        $updates = $object['Updates'].AsObject()
        Check-Strings $updates @('Channel','LastCheckedChannel','PendingVersion')
        Check-Boolean $updates @('CheckWhileOpen')
        if ($null -ne $updates['LastCheckedUtc']) {
            Require-Kind $updates['LastCheckedUtc'] 'String'
            $date = [DateTimeOffset]::MinValue
            if (![DateTimeOffset]::TryParse($updates['LastCheckedUtc'].ToString(), [ref]$date)) { throw 'Invalid update timestamp.' }
        }
    }
}
function Add-CacheFile([string]$Name, [long]$Maximum, [string]$Expected = '') {
    $path = Assert-LocalPath (Join-Path $rules $Name)
    $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        if ($stream.Length -le 0 -or $stream.Length -gt $Maximum) { throw 'Invalid rule cache size.' }
        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream))
        if ($Expected -and ($Expected -notmatch '\A[0-9a-fA-F]{64}\z' -or $hash -cne $Expected.ToUpperInvariant())) { throw 'Rule cache checksum mismatch.' }
        $previous = if ($plan.ContainsKey($Name)) { $plan[$Name].Length } else { 0L }
        $nextSize = $cacheBudget.Bytes - $previous + $stream.Length
        if ($nextSize -gt 1GB -or (!$plan.ContainsKey($Name) -and $plan.Count -ge 4096)) { throw 'The rule cache exceeds the import budget.' }
        $plan[$Name] = [pscustomobject]@{ Path = $path; Hash = $hash; Length = $stream.Length }
        $cacheBudget.Bytes = $nextSize
    } finally { $stream.Dispose() }
}
function Add-CachePair([string]$DataName, [string]$MetaName, [long]$Maximum, [bool]$Required, [string]$Category = '') {
    $dataExists = Test-Path -LiteralPath (Join-Path $rules $DataName) -PathType Leaf
    $metaExists = Test-Path -LiteralPath (Join-Path $rules $MetaName) -PathType Leaf
    if (!$dataExists -and !$metaExists -and !$Required) { return }
    if (!$dataExists -or !$metaExists) { throw 'A required rule cache or its metadata is missing.' }
    $metaBytes = Read-Limited (Join-Path $rules $MetaName) 64KB
    $meta = Parse-Json $metaBytes
    Require-Kind $meta 'Object'
    Require-Kind $meta['sha256'] 'String'
    if ($Category) {
        Require-Kind $meta['category'] 'String'
        if ($meta['category'].ToString() -cne $Category) { throw 'Rule cache category mismatch.' }
    }
    Add-CacheFile $DataName $Maximum $meta['sha256'].ToString()
    if ($meta.AsObject().ContainsKey('bytes') -and ($null -eq $meta['bytes'] -or $meta['bytes'].ToString() -cne $plan[$DataName].Length.ToString())) { throw 'Rule cache size does not match its metadata.' }
    Add-CacheFile $MetaName 64KB ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($metaBytes)))
}
function Set-PrivateDirectory([string]$Path) {
    $null = Assert-LocalPath $Path
    $null = [IO.Directory]::CreateDirectory($Path)
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    $acl.SetOwner($sid)
    foreach ($identity in @($sid, [Security.Principal.SecurityIdentifier]::new('S-1-5-18'), [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($identity, 'FullControl', 'ContainerInherit, ObjectInherit', 'None', 'Allow'))
    }
    [IO.FileSystemAclExtensions]::SetAccessControl([IO.DirectoryInfo]::new($Path), $acl)
}

try {
    $source = Assert-LocalPath (Resolve-Path -LiteralPath $SourceData).ProviderPath
    try { $clear = [Security.Cryptography.ProtectedData]::Unprotect((Read-Limited (Join-Path $source 'settings.dat') ($limit + 64KB)), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser) }
    catch { throw 'Cannot decrypt source settings for the current Windows user, or the source file is invalid.' }
    if ($clear.Length -gt $limit) { throw 'Decrypted settings exceed 8 MiB.' }
    $settings = Parse-Json $clear
    Check-Settings $settings
    $category = if ($null -eq $settings['BaseDomainCategory']) { 'ru-blocked' } else { $settings['BaseDomainCategory'].ToString() }
    if ($category -notmatch '\A[a-z0-9][a-z0-9_!@.-]{0,100}\z') { throw 'Invalid selected domain category.' }
    $baseEnabled = $null -eq $settings['BaseRulesEnabled'] -or $settings['BaseRulesEnabled'].GetValueKind().ToString() -eq 'True'
    $rules = Assert-LocalPath $(if ($RulesSource) { (Resolve-Path -LiteralPath $RulesSource).ProviderPath } else { Join-Path $source 'rules' })
    if (Test-Path -LiteralPath $rules -PathType Container) {
        Add-CachePair 'ru-blocked.srs' 'metadata.json' 16MB $baseEnabled
        Add-CachePair "domains-$category.json" "domains-$category-metadata.json" 128MB $baseEnabled $category
        Add-CachePair 'geosite.dat' 'geosite-metadata.json' 128MB $false
        foreach ($file in Get-ChildItem -LiteralPath $rules -File -Filter 'domains-*-metadata.json') {
            if ($file.Name -cmatch '\Adomains-([a-z0-9][a-z0-9_!@.-]{0,100})-metadata\.json\z') {
                $id = $Matches[1]
                if ($id -cne $category) { Add-CachePair "domains-$id.json" $file.Name 128MB $true $id }
            }
        }
        $catalogPath = Join-Path $rules 'domain-categories.json'
        if ($plan.ContainsKey('geosite.dat') -and (Test-Path -LiteralPath $catalogPath -PathType Leaf)) {
            $catalogBytes = Read-Limited $catalogPath 8MB
            $catalog = Parse-Json $catalogBytes
            Require-Kind $catalog 'Array'
            $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            foreach ($id in $catalog.AsArray()) {
                Require-Kind $id 'String'
                if ($id.ToString() -notmatch '\A[a-z0-9][a-z0-9_!@.-]{0,100}\z' -or !$ids.Add($id.ToString())) { throw 'Invalid domain category catalogue.' }
            }
            $metaBytes = Read-Limited (Join-Path $rules 'geosite-metadata.json') 64KB
            if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($metaBytes)) -cne $plan['geosite-metadata.json'].Hash) { throw 'Rule metadata changed during validation.' }
            $meta = Parse-Json $metaBytes
            if ($null -eq $meta['categories'] -or $meta['categories'].ToString() -cne $ids.Count.ToString() -or ($baseEnabled -and !$ids.Contains($category))) { throw 'Domain category catalogue does not match its metadata.' }
            Add-CacheFile 'domain-categories.json' 8MB ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($catalogBytes)))
        }
    } elseif ($baseEnabled) { throw 'The active base requires a validated GeoIP cache and selected domain category.' }

    if ($null -eq $settings['Updates']) { $settings['Updates'] = [Text.Json.Nodes.JsonObject]::new() }
    if ($PackageName -eq 'FlowVPN') { $settings['Updates']['Channel'] = [Text.Json.Nodes.JsonNode]::Parse('"stable"') }
    $settings['Updates']['PendingVersion'] = [Text.Json.Nodes.JsonNode]::Parse('""')
    $serialized = [Text.Encoding]::UTF8.GetBytes($settings.ToJsonString())
    if ($serialized.Length -gt $limit) { throw 'Normalized settings exceed 8 MiB.' }
    try {
        if ($ValidateOnly) {
            [pscustomobject]@{ Valid = $true; PackageName = $PackageName; Profiles = $settings['Profiles'].AsArray().Count; Sources = $settings['Sources'].AsArray().Count; CacheFiles = $plan.Count; Written = $false }
            return
        }
        $protected = [Security.Cryptography.ProtectedData]::Protect($serialized, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    } finally { [Array]::Clear($serialized, 0, $serialized.Length) }
    $packages = @(Get-AppxPackage -Name $PackageName | Where-Object Name -CEQ $PackageName)
    if ($packages.Count -ne 1) { throw 'Install exactly one matching FlowVPN package for the current Windows user before importing.' }
    $expectedPublisher = if ($PackageName -ceq 'FlowVPN') { 'CN=FlowVPN' } else { 'CN=FlowVPN Development' }
    if (![string]::Equals([string]$packages[0].Publisher, $expectedPublisher, [StringComparison]::Ordinal)) {
        throw 'The installed package publisher does not match the selected FlowVPN edition.'
    }
    if (Get-Process -Name 'FlowVPN' -ErrorAction SilentlyContinue) { throw 'Close FlowVPN, including its tray instance, before importing settings.' }
    $family = [string]$packages[0].PackageFamilyName
    if ($family -notmatch '\A[A-Za-z0-9._-]+\z') { throw 'Invalid installed package family.' }
    $prefix = Assert-LocalPath (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Packages')
    $destination = Assert-LocalPath (Join-Path $prefix "$family\LocalState\data")
    if (!$destination.StartsWith($prefix + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'The destination is outside the package data directory.' }
    $settingsPath = Join-Path $destination 'settings.dat'
    $rulesTarget = Join-Path $destination 'rules'
    if (Test-Path -LiteralPath $settingsPath) { throw 'Destination settings already exist. Import never overwrites settings.' }
    if ((Test-Path -LiteralPath $rulesTarget) -and @(Get-ChildItem -LiteralPath $rulesTarget -Force).Count) { throw 'Destination rule cache already exists. Import never overwrites cache files.' }
    if (!$PSCmdlet.ShouldProcess("Installed $PackageName settings", 'Import validated settings and rule caches')) { return }
    # Shares the app's single-instance gate, preventing startup during the commit.
    $created = $false
    $migrationGate = [Threading.Mutex]::new($true, ('Local\FlowVPN-Fluent-' + [Environment]::UserName), [ref]$created)
    if (!$created) { $migrationGate.Dispose(); $migrationGate = $null; throw 'FlowVPN is already running or another import is active.' }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try { $sid = $identity.User } finally { $identity.Dispose() }
    Set-PrivateDirectory $destination
    $stage = Join-Path $destination ('.import-' + [guid]::NewGuid().ToString('N'))
    Set-PrivateDirectory $stage
    $stageRules = Join-Path $stage 'rules'
    Set-PrivateDirectory $stageRules
    foreach ($entry in $plan.GetEnumerator()) {
        $target = Join-Path $stageRules $entry.Key
        [IO.File]::Copy($entry.Value.Path, $target, $false)
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -cne $entry.Value.Hash) { throw 'A cache file changed during import.' }
    }
    $stageSettings = Join-Path $stage 'settings.dat'
    $stream = [IO.File]::Open($stageSettings, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($protected, 0, $protected.Length); $stream.Flush($true) } finally { $stream.Dispose() }
    if (Get-Process -Name 'FlowVPN' -ErrorAction SilentlyContinue) { throw 'FlowVPN started during import. Close it and try again.' }
    if (Test-Path -LiteralPath $settingsPath) { throw 'Destination settings appeared during import; nothing will be overwritten.' }
    if (Test-Path -LiteralPath $rulesTarget) { [IO.Directory]::Delete($rulesTarget, $false) }
    [IO.Directory]::Move($stageRules, $rulesTarget)
    $rulesCommitted = $true
    [IO.File]::Move($stageSettings, $settingsPath, $false)
    $settingsCommitted = $true
    [pscustomobject]@{ Imported = $true; PackageName = $PackageName; Profiles = $settings['Profiles'].AsArray().Count; Sources = $settings['Sources'].AsArray().Count; CacheFiles = $plan.Count }
} finally {
    if ($null -ne $migrationGate) { $migrationGate.ReleaseMutex(); $migrationGate.Dispose() }
    if ($null -ne $clear) { [Array]::Clear($clear, 0, $clear.Length) }
    if ($null -ne $protected) { [Array]::Clear($protected, 0, $protected.Length) }
    if ($stage -and (Test-Path -LiteralPath $stage)) {
        $safeStage = Assert-LocalPath $stage
        if (!$safeStage.StartsWith($destination + '\.import-', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe import staging path.' }
        Remove-Item -LiteralPath $safeStage -Recurse -Force
    }
    if ($rulesCommitted -and !$settingsCommitted) {
        Write-Warning 'Settings were not imported. Validated rule caches remain in the package data directory.'
    }
}
