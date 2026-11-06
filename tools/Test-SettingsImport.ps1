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
$required = @('Require-Kind','Check-Strings','Check-Boolean','Check-ApplicationSets','Check-Settings')
$functions = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -in $required }, $false))
if ($functions.Count -ne $required.Count) { throw 'Missing validation functions.' }
foreach ($function in $functions) { . ([scriptblock]::Create($function.Extent.Text)) }
# Only pure validation definitions are loaded. The import workflow and user directories are never invoked.
$tests = [Collections.Generic.List[string]]::new()
function New-Set {
    @{ Id = [guid]::NewGuid().ToString('N'); Name = 'Переносимый набор'; Icon = 'network'; PresetIds = @('alpha','future-app') }
}
function Check-Fixture([string]$Label, [hashtable]$Settings, [bool]$Expected) {
    $node = [Text.Json.Nodes.JsonNode]::Parse(($Settings | ConvertTo-Json -Depth 20 -Compress))
    $original = $node.ToJsonString()
    $valid = $true
    try { Check-Settings $node } catch { $valid = $false }
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
[pscustomobject]@{ Passed = $tests.Count; UsesUserData = $false; ExecutesImportWorkflow = $false; Tests = $tests }
