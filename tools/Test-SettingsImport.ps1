#Requires -Version 7.4
[CmdletBinding()]
param([string]$Root = (Join-Path $PSScriptRoot '..'))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = (Resolve-Path -LiteralPath $Root).ProviderPath
$file = Join-Path $repo 'src/FlowVPN.WinUI/Distribution/Import-FlowVPNSettings.ps1'
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($file, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Import helper contains syntax errors.' }
$required = @('Require-Kind','Check-Strings','Check-Boolean','Check-ObjectFields','Check-Transport','Check-ApplicationSets','Get-CanonicalDomainCategories','Get-DomainRuleKey','Get-DomainSelection','Check-DomainCacheMetadata','Check-Settings')
$functions = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -in $required }, $false))
if ($functions.Count -ne $required.Count) { throw 'Missing validation functions.' }
foreach ($function in $functions) { . ([scriptblock]::Create($function.Extent.Text)) }
# Only pure validation definitions are loaded. The import workflow and user directories are never invoked.
$tests = [Collections.Generic.List[string]]::new()
function New-Set {
    @{ Id = [guid]::NewGuid().ToString('N'); Name = 'Переносимый набор'; Icon = 'network'; PresetIds = @('alpha','future-app') }
}
function Check-Fixture([string]$Label, [hashtable]$Settings, [bool]$Expected) {
    Check-JsonFixture $Label ($Settings | ConvertTo-Json -Depth 20 -Compress) $Expected
}
function Check-JsonFixture([string]$Label, [string]$Json, [bool]$Expected) {
    $valid = $true
    $node = $null
    $original = $null
    try {
        $node = [Text.Json.Nodes.JsonNode]::Parse($Json)
        $original = $node.ToJsonString()
        Check-Settings $node
    } catch { $valid = $false }
    if ($valid -ne $Expected) { throw "Unexpected validation outcome: $Label" }
    if ($valid -and $node.ToJsonString() -cne $original) { throw "Validation changed settings: $Label" }
    $tests.Add($Label)
}
function Check-Sets([string]$Label, $Sets, [bool]$Expected) {
    Check-Fixture $Label @{ Sources = @(); Profiles = @(); BaseRulesEnabled = $false; ApplicationSets = $Sets } $Expected
}
Check-Fixture 'Legacy settings without saved-set property remain valid' @{ Sources = @(); Profiles = @(); BaseRulesEnabled = $false } $true
Check-Sets 'An empty saved-set list remains valid' @() $true
Check-Sets 'Unknown future application identities remain portable' @((New-Set)) $true
$set = New-Set; $set.Remove('Icon'); Check-Sets 'An omitted optional icon uses the application default' @($set) $true
$set = New-Set; $set.PresetIds = @(0..127 | ForEach-Object { "app-$_" }); Check-Sets '128 unique application members are accepted' @($set) $true
Check-Sets '128 unique saved sets are accepted' @(1..128 | ForEach-Object { New-Set }) $true
Check-Fixture 'A completed onboarding marker is preserved' @{ Sources = @(); Profiles = @(); BaseRulesEnabled = $false; ProfileOnboardingCompleted = $true; ApplicationSets = @((New-Set)) } $true
Check-Sets 'A null saved-set list is rejected' $null $false
Check-Sets 'A scalar saved-set list is rejected' 'invalid' $false
Check-Sets 'An object instead of a saved-set list is rejected' @{} $false
Check-Sets '129 saved sets are rejected' @(1..129 | ForEach-Object { New-Set }) $false
Check-Sets 'A null set is rejected' @($null) $false
Check-Sets 'A scalar set is rejected' @('invalid') $false
foreach ($value in @($null,42,'not-a-guid',((' ' * 40) + [guid]::NewGuid().ToString('N')))) {
    $set = New-Set; $set.Id = $value; Check-Sets ('Malformed GUID variant ' + $tests.Count) @($set) $false
}
$set = New-Set; $set.Remove('Id'); Check-Sets 'An absent set identity is rejected' @($set) $false
$one = New-Set; $two = New-Set; $two.Id = ([guid]$one.Id).ToString('D').ToUpperInvariant()
Check-Sets 'GUID aliases cannot evade duplicate-identity detection' @($one,$two) $false
foreach ($value in @($null,42,'','  ',('x' * 181),"line`nname")) {
    $set = New-Set; $set.Name = $value; Check-Sets ('Malformed name variant ' + $tests.Count) @($set) $false
}
$set = New-Set; $set.Remove('Name'); Check-Sets 'An absent set name is rejected' @($set) $false
foreach ($value in @($null,42,'C:\Private\icon.ico',('x' * 65))) {
    $set = New-Set; $set.Icon = $value; Check-Sets ('Malformed icon variant ' + $tests.Count) @($set) $false
}
foreach ($value in @($null,'alpha',42)) {
    $set = New-Set; $set.PresetIds = $value; Check-Sets ('Malformed member-list variant ' + $tests.Count) @($set) $false
}
$set = New-Set; $set.Remove('PresetIds'); Check-Sets 'An absent member list is rejected' @($set) $false
$set = New-Set; $set.PresetIds = @(); Check-Sets 'An empty saved set is rejected' @($set) $false
$set = New-Set; $set.PresetIds = @(0..128 | ForEach-Object { "app-$_" }); Check-Sets '129 members are rejected' @($set) $false
foreach ($value in @($null,42,'C:\Private\app.exe','preset:alpha','union(alpha,beta)',('a' * 129),'two words','*.exe')) {
    $set = New-Set; $set.PresetIds = @($value); Check-Sets ('Malformed application identity variant ' + $tests.Count) @($set) $false
}
$set = New-Set; $set.PresetIds = @('alpha','alpha'); Check-Sets 'Duplicate members are rejected in persisted sets' @($set) $false
$set = New-Set; $set['Path'] = 'C:\Private\app.exe'; Check-Sets 'Unknown fields cannot smuggle local paths into portable sets' @($set) $false
$set = New-Set; $set['SourceId'] = 'private-vpn'; Check-Sets 'Portable sets cannot contain a VPN source binding' @($set) $false

function New-TransportSource([string]$Method = 'aes-128-gcm', [int]$Length = 8) {
    @{
        Id = [guid]::NewGuid().ToString('N'); Kind = 'Outline'; Name = 'Synthetic module source'
        ConnectionJson = (@{ type = 'shadowsocks'; tag = 'vpn'; server = '192.0.2.1'; server_port = 443; method = $Method; password = " opaque`ncredential "; domain_resolver = 'dns-direct' } | ConvertTo-Json -Compress)
        Transport = @{ ModuleId = 'outline-transport'; SchemaVersion = 1; Outline = @{ PrefixBase64 = [Convert]::ToBase64String([byte[]]::new($Length)) } }
    }
}
function Check-Source([string]$Label, [hashtable]$Source, [bool]$Expected) {
    Check-Fixture $Label @{ Sources = @($Source); Profiles = @(); BaseRulesEnabled = $false } $Expected
}
Check-Source 'Canonical eight-byte AES-128 prefix and opaque credentials are preserved' (New-TransportSource) $true
Check-Source 'Canonical sixteen-byte AES-256 prefix is preserved' (New-TransportSource 'aes-256-gcm' 16) $true
Check-Source 'Canonical sixteen-byte ChaCha20 prefix is preserved' (New-TransportSource 'chacha20-ietf-poly1305' 16) $true
$source = New-TransportSource; $source.Transport.Remove('SchemaVersion'); Check-Source 'Omitted module schema uses the runtime default of one' $source $true
$source = New-TransportSource; $source.Remove('Transport'); Check-Source 'Legacy source without transport remains valid' $source $true
$source = New-TransportSource; $source.Transport = $null; $source.Kind = 'OpenVPN'; $source.ConnectionJson = '{}'; Check-Source 'Null transport leaves legacy OpenVPN settings unchanged' $source $true
$source = New-TransportSource; $source.DynamicKey = 'ssconf://example.invalid/test'; Check-Source 'Dynamic source and its typed descriptor are preserved without fetching' $source $true
$source = New-TransportSource; $source.Kind = 'OpenVPN'; Check-Source 'Outline transport cannot be attached to OpenVPN' $source $false
foreach ($value in @('other-module','Outline-Transport','',42,$null)) {
    $source = New-TransportSource; $source.Transport.ModuleId = $value; Check-Source ('Unsupported module identity variant ' + $tests.Count) $source $false
}
foreach ($value in @(0,2,1.5,'1',$null)) {
    $source = New-TransportSource; $source.Transport.SchemaVersion = $value; Check-Source ('Unsupported module schema variant ' + $tests.Count) $source $false
}
foreach ($value in @('invalid',42,@(),$null)) {
    $source = New-TransportSource; $source.Transport.Outline = $value; Check-Source ('Malformed module options variant ' + $tests.Count) $source $false
}
foreach ($field in @('ExecutablePath','DownloadUrl','Capability','moduleid')) {
    $source = New-TransportSource; $source.Transport[$field] = 'unexpected'; Check-Source ('Unknown transport field ' + $field) $source $false
}
$source = New-TransportSource; $source.Transport.Outline['Prefix'] = 'unexpected'; Check-Source 'Unknown Outline options are rejected' $source $false
foreach ($value in @('','!invalid','Zg','Zh==',"Zg==`n",42,$null)) {
    $source = New-TransportSource; $source.Transport.Outline.PrefixBase64 = $value; Check-Source ('Malformed or noncanonical prefix variant ' + $tests.Count) $source $false
}
Check-Source 'AES-128 prefix is capped at eight bytes' (New-TransportSource 'aes-128-gcm' 9) $false
Check-Source 'AES-256 prefix is capped at sixteen bytes' (New-TransportSource 'aes-256-gcm' 17) $false
Check-Source 'An empty decoded prefix is rejected' (New-TransportSource 'aes-256-gcm' 0) $false
foreach ($method in @('none','aes-192-gcm','2022-blake3-aes-128-gcm','AES-128-GCM')) {
    Check-Source ('Unsupported transport cipher ' + $method) (New-TransportSource $method) $false
}
foreach ($field in @('detour','plugin','executable')) {
    $source = New-TransportSource; $connection = $source.ConnectionJson | ConvertFrom-Json -AsHashtable; $connection[$field] = 'unexpected'; $source.ConnectionJson = $connection | ConvertTo-Json -Compress
    Check-Source ('Unknown module outbound field ' + $field) $source $false
}
foreach ($value in @(0,65536,1.5,'443',$null)) {
    $source = New-TransportSource; $connection = $source.ConnectionJson | ConvertFrom-Json -AsHashtable; $connection.server_port = $value; $source.ConnectionJson = $connection | ConvertTo-Json -Compress
    Check-Source ('Invalid module outbound port variant ' + $tests.Count) $source $false
}
foreach ($value in @('','https://example.invalid/path',"bad`nhost")) {
    $source = New-TransportSource; $connection = $source.ConnectionJson | ConvertFrom-Json -AsHashtable; $connection.server = $value; $source.ConnectionJson = $connection | ConvertTo-Json -Compress
    Check-Source ('Invalid module host variant ' + $tests.Count) $source $false
}
$source = New-TransportSource; $connection = $source.ConnectionJson | ConvertFrom-Json -AsHashtable; $connection.password = 'x' * 65537; $source.ConnectionJson = $connection | ConvertTo-Json -Compress
Check-Source 'Oversized module credential is rejected' $source $false
foreach ($value in @('[]','{broken}',(' ' * (4MB + 1)))) {
    $source = New-TransportSource; $source.ConnectionJson = $value; Check-Source ('Invalid module source JSON variant ' + $tests.Count) $source $false
}
foreach ($duplicate in @('ModuleId','SchemaVersion','Outline','PrefixBase64')) {
    $source = New-TransportSource
    $fixture = @{ Sources = @($source); Profiles = @(); BaseRulesEnabled = $false } | ConvertTo-Json -Depth 20 -Compress
    $duplicateValue = switch ($duplicate) { 'ModuleId' { '"outline-transport"' } 'SchemaVersion' { '1' } 'Outline' { '{}' } 'PrefixBase64' { '"Zg=="' } }
    $fixture = $fixture.Replace(('"' + $duplicate + '":'), ('"' + $duplicate + '":' + $duplicateValue + ',"' + $duplicate + '":'))
    Check-JsonFixture ('Duplicate transport field ' + $duplicate) $fixture $false
}
$source = New-TransportSource; $source.ConnectionJson = $source.ConnectionJson.Replace('"method":', '"method":"aes-128-gcm","method":')
Check-Source 'Duplicate outbound cipher fields are rejected' $source $false
Check-Fixture 'Legacy custom GeoSite category remains valid before explicit migration' @{ Sources=@(); Profiles=@(); BaseDomainCategory='youtube' } $true
Check-Fixture 'Primary and additional GeoSite categories are portable' @{ Sources=@(); Profiles=@(); BaseDomainCategory='ru-blocked-all'; AdditionalDomainCategories=@('github','youtube') } $true
Check-Fixture 'An empty domain selection leaves the independent IP base enabled' @{ Sources=@(); Profiles=@(); BaseDomainCategory='none'; AdditionalDomainCategories=@(); BaseRulesEnabled=$true } $true
Check-Fixture '32 additional categories plus the primary list are accepted' @{ Sources=@(); Profiles=@(); AdditionalDomainCategories=@(0..31 | ForEach-Object { "category-$_" }) } $true
Check-Fixture '33 additional categories are rejected' @{ Sources=@(); Profiles=@(); AdditionalDomainCategories=@(0..32 | ForEach-Object { "category-$_" }) } $false
foreach ($invalid in @($null,'youtube',42)) {
    Check-Fixture ('Malformed additional category list ' + $tests.Count) @{ Sources=@(); Profiles=@(); AdditionalDomainCategories=$invalid } $false
}
foreach ($invalid in @($null,42,'none','../private','https://example.org','')) {
    Check-Fixture ('Malformed additional category identity ' + $tests.Count) @{ Sources=@(); Profiles=@(); AdditionalDomainCategories=@($invalid) } $false
}
$legacy = [Text.Json.Nodes.JsonNode]::Parse('{"BaseDomainCategory":"youtube"}')
$selection = Get-DomainSelection $legacy
if ($selection.BaseCategory -cne 'none' -or $selection.AdditionalCategories.Count -ne 1 -or $selection.Categories[0] -cne 'youtube' -or $legacy['BaseDomainCategory'].ToString() -cne 'youtube') { throw 'Legacy category migration changed routing or mutated input.' }
$tests.Add('Legacy category migration preserves only its previous routes and does not mutate validation input')
$emptySelection = Get-DomainSelection ([Text.Json.Nodes.JsonNode]::Parse('{"BaseDomainCategory":"none","AdditionalDomainCategories":[]}'))
if ($emptySelection.Categories.Count -ne 0) { throw 'Empty domain selection was not preserved.' }
$tests.Add('Empty selection does not synthesize an unwanted domain category')
if ((Get-DomainRuleKey @('youtube','github','github')) -cne (Get-DomainRuleKey @('github','youtube'))) { throw 'Domain union cache identity is not canonical.' }
$tests.Add('Prepared union identity ignores ordering and duplicate membership')
function Check-DomainMetadataFixture([string]$Label, [hashtable]$Metadata, [string[]]$ExpectedCategories, [bool]$Expected) {
    $valid = $true
    try { Check-DomainCacheMetadata ([Text.Json.Nodes.JsonNode]::Parse(($Metadata | ConvertTo-Json -Depth 5 -Compress))) $ExpectedCategories } catch { $valid = $false }
    if ($valid -ne $Expected) { throw "Unexpected metadata validation result: $Label" }
    $tests.Add($Label)
}
$metadata = @{ schemaVersion=2; categories=@('github','youtube'); sourceSha256=('A'*64); sha256=('B'*64); size=123 }
Check-DomainMetadataFixture 'Prepared cache metadata binds the exact effective membership and payload size' $metadata @('github','youtube') $true
Check-DomainMetadataFixture 'Prepared cache membership mismatch is rejected' $metadata @('github') $false
$metadata.categories=@('github','github'); Check-DomainMetadataFixture 'Duplicate prepared cache membership is rejected' $metadata @('github') $false
$metadata.categories=@('github','youtube'); $metadata.size=32MB+1; Check-DomainMetadataFixture 'Oversized prepared cache is rejected' $metadata @('github','youtube') $false
$metadata.size=123; $metadata.schemaVersion=1; Check-DomainMetadataFixture 'Unknown prepared cache schema is rejected' $metadata @('github','youtube') $false
[pscustomobject]@{ Passed = $tests.Count; UsesUserData = $false; ExecutesImportWorkflow = $false; Tests = $tests }
