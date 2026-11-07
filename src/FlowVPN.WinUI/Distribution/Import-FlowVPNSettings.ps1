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
function Check-ObjectFields($Node, [string[]]$Allowed) {
    Require-Kind $Node 'Object'
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($field in $Node.AsObject()) {
        if ($field.Key -cnotin $Allowed -or !$seen.Add($field.Key)) { throw 'Unknown or duplicate module transport field.' }
    }
}
function Check-Transport($Source) {
    # Legacy sources stay byte-for-byte equivalent; a null descriptor means no optional transport.
    if (!$Source.ContainsKey('Transport') -or $null -eq $Source['Transport']) { return }
    $transport = $Source['Transport']
    Check-ObjectFields $transport @('ModuleId','SchemaVersion','Outline')
    Require-Kind $Source['Kind'] 'String'
    Require-Kind $transport['ModuleId'] 'String'
    if ($Source['Kind'].ToString() -cne 'Outline' -or $transport['ModuleId'].ToString() -cne 'outline-transport') {
        throw 'Unsupported module transport.'
    }
    if ($transport.AsObject().ContainsKey('SchemaVersion')) {
        Require-Kind $transport['SchemaVersion'] 'Number'
        $version = 0
        if (![int]::TryParse($transport['SchemaVersion'].ToString(), [ref]$version) -or $version -ne 1) { throw 'Unsupported module transport schema.' }
    }
    $options = $transport['Outline']
    Check-ObjectFields $options @('PrefixBase64')
    Require-Kind $options['PrefixBase64'] 'String'
    Require-Kind $Source['ConnectionJson'] 'String'
    $raw = $Source['ConnectionJson'].ToString()
    if ($raw.Length -lt 1 -or $raw.Length -gt 4MB) { throw 'Invalid module source configuration size.' }
    try {
        $jsonOptions = [Text.Json.JsonDocumentOptions]::new()
        $jsonOptions.MaxDepth = 12
        $connection = [Text.Json.Nodes.JsonNode]::Parse($raw, $null, $jsonOptions)
    } catch { throw 'Invalid module source configuration JSON.' }
    # Match BrokerConfig.Source and ModuleTransport.OutlineParameters; do not accept raw routing/code fields.
    Check-ObjectFields $connection @('type','tag','server','server_port','method','password','domain_resolver')
    foreach ($name in @('type','tag','server','method','password','domain_resolver')) { Require-Kind $connection[$name] 'String' }
    if ($connection['type'].ToString() -cne 'shadowsocks' -or $connection['tag'].ToString() -cne 'vpn' -or $connection['domain_resolver'].ToString() -cne 'dns-direct') {
        throw 'Invalid module source configuration type or routing tags.'
    }
    $server = $connection['server'].ToString()
    if ($server.Length -lt 1 -or $server.Length -gt 253 -or $server -match '[\s\p{Cc}]' -or [Uri]::CheckHostName($server) -eq [UriHostNameType]::Unknown) {
        throw 'Invalid module server address.'
    }
    Require-Kind $connection['server_port'] 'Number'
    $port = 0
    if (![int]::TryParse($connection['server_port'].ToString(), [ref]$port) -or $port -lt 1 -or $port -gt 65535) { throw 'Invalid module server port.' }
    if ($connection['password'].ToString().Length -gt 65536) { throw 'Module credential exceeds its limit.' }
    $prefixLimit = switch -CaseSensitive ($connection['method'].ToString()) {
        'aes-128-gcm' { 8 }
        'aes-256-gcm' { 16 }
        'chacha20-ietf-poly1305' { 16 }
        default { throw 'The encryption method does not support this module transport.' }
    }
    $encoded = $options['PrefixBase64'].ToString()
    if ($encoded.Length -lt 1 -or $encoded.Length -gt 24) { throw 'Invalid module prefix encoding.' }
    $prefix = $null
    try {
        try { $prefix = [Convert]::FromBase64String($encoded) } catch { throw 'Invalid module prefix encoding.' }
        if ($prefix.Length -lt 1 -or $prefix.Length -gt $prefixLimit -or [Convert]::ToBase64String($prefix) -cne $encoded) {
            throw 'Invalid or oversized module prefix.'
        }
    } finally { if ($null -ne $prefix) { [Array]::Clear($prefix, 0, $prefix.Length) } }
}
function Check-ApplicationSets($Object) {
    # Older settings have no saved sets. Unknown application IDs remain portable references.
    if (!$Object.ContainsKey('ApplicationSets')) { return }
    Require-Kind $Object['ApplicationSets'] 'Array'
    $sets = $Object['ApplicationSets'].AsArray()
    if ($sets.Count -gt 128) { throw 'Too many saved application sets.' }
    $ids = [Collections.Generic.HashSet[guid]]::new()
    foreach ($entry in $sets) {
        Require-Kind $entry 'Object'
        $item = $entry.AsObject()
        foreach ($field in $item) {
            if ($field.Key -cnotin @('Id','Name','Icon','PresetIds')) { throw 'Unsupported saved-set field.' }
        }
        Require-Kind $item['Id'] 'String'
        $id = [guid]::Empty
        $rawId = $item['Id'].ToString()
        if ($rawId.Length -gt 68 -or ![guid]::TryParse($rawId, [ref]$id) -or !$ids.Add($id)) { throw 'Invalid or duplicate saved-set identifier.' }
        Require-Kind $item['Name'] 'String'
        $name = $item['Name'].ToString()
        if ([string]::IsNullOrWhiteSpace($name) -or $name.Length -gt 180 -or $name -match '\p{Cc}') { throw 'Invalid saved-set name.' }
        if ($item.ContainsKey('Icon')) {
            Require-Kind $item['Icon'] 'String'
            if ($item['Icon'].ToString() -cnotmatch '\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z') { throw 'Invalid saved-set icon.' }
        }
        Require-Kind $item['PresetIds'] 'Array'
        $members = $item['PresetIds'].AsArray()
        if ($members.Count -lt 1 -or $members.Count -gt 128) { throw 'A saved set must contain between 1 and 128 applications.' }
        $memberIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($member in $members) {
            Require-Kind $member 'String'
            $value = $member.ToString()
            if ($value -cnotmatch '\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z' -or !$memberIds.Add($value)) {
                throw 'Invalid or duplicate application identity in a saved set.'
            }
        }
    }
}
function Get-CanonicalDomainCategories([string[]]$Ids, [int]$Maximum = 33) {
    if ($Ids.Count -gt $Maximum) { throw 'Too many domain categories.' }
    $unique = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($value in $Ids) {
        if ([string]::IsNullOrWhiteSpace($value)) { throw 'Missing domain category.' }
        $id = $value.Trim().ToLowerInvariant()
        if ($id -cnotmatch '\A[a-z0-9][a-z0-9_!@.-]{0,100}\z' -or $id -ceq 'none') { throw 'Invalid domain category.' }
        $null = $unique.Add($id)
    }
    $result = [string[]]@($unique)
    [Array]::Sort($result, [StringComparer]::Ordinal)
    return ,$result
}
function Get-DomainRuleKey([string[]]$Ids) {
    $canonical = Get-CanonicalDomainCategories $Ids
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes([string]::Join("`n", $canonical))))
}
function Get-DomainSelection($Settings) {
    $object = $Settings.AsObject()
    $category = 'ru-blocked'
    if ($object.ContainsKey('BaseDomainCategory')) {
        Require-Kind $object['BaseDomainCategory'] 'String'
        $category = $object['BaseDomainCategory'].ToString().Trim().ToLowerInvariant()
        if ($category -cnotmatch '\A[a-z0-9][a-z0-9_!@.-]{0,100}\z') { throw 'Invalid primary domain category.' }
    }
    $extras = [Collections.Generic.List[string]]::new()
    if ($object.ContainsKey('AdditionalDomainCategories')) {
        Require-Kind $object['AdditionalDomainCategories'] 'Array'
        if ($object['AdditionalDomainCategories'].AsArray().Count -gt 32) { throw 'At most 32 additional domain categories are supported.' }
        foreach ($entry in $object['AdditionalDomainCategories'].AsArray()) {
            Require-Kind $entry 'String'
            $extras.Add($entry.ToString())
        }
    }
    $additional = Get-CanonicalDomainCategories $extras.ToArray() 32
    if ($category -cnotin @('ru-blocked','ru-blocked-all','none')) {
        $additional = Get-CanonicalDomainCategories ([string[]]@($additional + $category))
        if ($additional.Count -gt 32) { throw 'At most 32 additional domain categories are supported.' }
        $category = 'none'
    }
    [string[]]$categories = $additional
    if ($category -cne 'none') { $categories = Get-CanonicalDomainCategories ([string[]]@($additional + $category)) }
    return [pscustomobject]@{ BaseCategory = $category; AdditionalCategories = [string[]]$additional; Categories = [string[]]@($categories); RuleKey = Get-DomainRuleKey ([string[]]@($categories)) }
}
function Check-DomainCacheMetadata($Meta, [string[]]$Categories) {
    Require-Kind $Meta 'Object'
    Require-Kind $Meta['schemaVersion'] 'Number'
    if ($Meta['schemaVersion'].ToString() -cne '2') { throw 'Unsupported prepared domain cache schema.' }
    Require-Kind $Meta['categories'] 'Array'
    $ids = [Collections.Generic.List[string]]::new()
    foreach ($entry in $Meta['categories'].AsArray()) { Require-Kind $entry 'String'; $ids.Add($entry.ToString()) }
    $canonical = Get-CanonicalDomainCategories $ids.ToArray()
    if ($canonical.Count -eq 0 -or $canonical.Count -ne $ids.Count -or [string]::Join("`n", $canonical) -cne [string]::Join("`n", (Get-CanonicalDomainCategories $Categories))) { throw 'Prepared domain cache membership mismatch.' }
    foreach ($field in @('sha256','sourceSha256')) {
        Require-Kind $Meta[$field] 'String'
        if ($Meta[$field].ToString() -cnotmatch '\A[0-9a-fA-F]{64}\z') { throw 'Invalid prepared domain cache checksum.' }
    }
    Require-Kind $Meta['size'] 'Number'
    $size = 0L
    if (![long]::TryParse($Meta['size'].ToString(), [ref]$size) -or $size -le 0 -or $size -gt 32MB) { throw 'Invalid prepared domain cache size.' }
}
function Check-Settings($Settings) {
    Require-Kind $Settings 'Object'
    $object = $Settings.AsObject()
    Check-Strings $object @('BaseDomainCategory', 'SelectedProfileId')
    Check-Boolean $object @('BaseRulesEnabled', 'AutoUpdateRules', 'StartInTray', 'ProfileOnboardingCompleted')
    $null = Get-DomainSelection $Settings
    Check-ApplicationSets $object
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
                Check-Transport $item
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
function Add-SelectedDomainCache($Selection, [bool]$Required) {
    if ($Selection.Categories.Count -eq 0) { return }
    $dataName = if ($Selection.Categories.Count -eq 1) { "domains-$($Selection.Categories[0]).json" } else { "domain-selection-$($Selection.RuleKey).json" }
    $metaName = "domain-cache-$($Selection.RuleKey).json"
    if (!(Test-Path -LiteralPath (Join-Path $rules $metaName) -PathType Leaf)) {
        if ($Selection.Categories.Count -eq 1) {
            Add-CachePair $dataName "domains-$($Selection.Categories[0])-metadata.json" 32MB $Required $Selection.Categories[0]
        } elseif ($Required -or (Test-Path -LiteralPath (Join-Path $rules $dataName))) { throw 'The selected prepared domain union is missing its metadata.' }
        return
    }
    $metaBytes = Read-Limited (Join-Path $rules $metaName) 64KB
    $meta = Parse-Json $metaBytes
    Check-DomainCacheMetadata $meta $Selection.Categories
    Add-CacheFile $dataName 32MB $meta['sha256'].ToString()
    if ($meta['size'].ToString() -cne $plan[$dataName].Length.ToString()) { throw 'Prepared domain cache size mismatch.' }
    Add-CacheFile $metaName 64KB ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($metaBytes)))
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
    $domainSelection = Get-DomainSelection $settings
    $settings['BaseDomainCategory'] = [Text.Json.Nodes.JsonNode]::Parse('"' + $domainSelection.BaseCategory + '"')
    $settings['AdditionalDomainCategories'] = [Text.Json.Nodes.JsonArray]::new()
    foreach ($id in $domainSelection.AdditionalCategories) { $settings['AdditionalDomainCategories'].AsArray().Add([Text.Json.Nodes.JsonNode]::Parse('"' + $id + '"')) }
    $baseEnabled = $null -eq $settings['BaseRulesEnabled'] -or $settings['BaseRulesEnabled'].GetValueKind().ToString() -eq 'True'
    $rules = Assert-LocalPath $(if ($RulesSource) { (Resolve-Path -LiteralPath $RulesSource).ProviderPath } else { Join-Path $source 'rules' })
    if (Test-Path -LiteralPath $rules -PathType Container) {
        Add-CachePair 'ru-blocked.srs' 'metadata.json' 16MB $baseEnabled
        Add-SelectedDomainCache $domainSelection $baseEnabled
        Add-CachePair 'geosite.dat' 'geosite-metadata.json' 128MB $false
        # Only effective prepared rules are required. Other combinations can be rebuilt from the validated archive.
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
            if ($null -eq $meta['categories'] -or $meta['categories'].ToString() -cne $ids.Count.ToString()) { throw 'Domain category catalogue does not match its metadata.' }
            if ($baseEnabled) { foreach ($id in $domainSelection.Categories) { if (!$ids.Contains($id)) { throw 'A selected domain category is missing from the catalogue.' } } }
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
