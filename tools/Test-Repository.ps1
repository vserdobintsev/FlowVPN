#Requires -Version 7.4
[CmdletBinding()]
param([string]$Root = (Join-Path $PSScriptRoot '..'))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$rootPath = (Resolve-Path -LiteralPath $Root).ProviderPath
$projectPath = Join-Path $rootPath 'src\FlowVPN.WinUI'
$failures = [Collections.Generic.List[string]]::new()
$files = [Collections.Generic.List[IO.FileInfo]]::new()
foreach ($file in Get-ChildItem -LiteralPath $rootPath -File -Force) { $files.Add($file) }
foreach ($folder in @('.github', 'docs', 'tools', 'src')) {
    $path = Join-Path $rootPath $folder
    if (!(Test-Path -LiteralPath $path -PathType Container)) { continue }
    foreach ($file in Get-ChildItem -LiteralPath $path -Recurse -File -Force) {
        if ($file.FullName -notmatch '[\\/](bin|obj|AppPackages|\.git)[\\/]') { $files.Add($file) }
    }
}

$sourceCheck = & (Join-Path $projectPath 'Distribution\Test-Source.ps1') -Root $projectPath
foreach ($file in $files) {
    $relative = [IO.Path]::GetRelativePath($rootPath, $file.FullName)
    if ($file.Name -match '(?i)^(settings\.dat|runtime\.json|password\.dpapi)$|\.(pfx|p12|ovpn|key|pem|log)$') {
        $failures.Add("Private file type: $relative")
    }
    if ($file.Extension -eq '.ps1') {
        $tokens = $null
        $errors = $null
        $null = [Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$errors)
        foreach ($error in $errors) { $failures.Add("PowerShell syntax: ${relative}:$($error.Extent.StartLineNumber): $($error.Message)") }
    }
    if ($file.Extension -eq '.json') {
        try { $document = [Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText($file.FullName)); $document.Dispose() }
        catch { $failures.Add("Invalid JSON: $relative") }
    }
    if ($file.Extension -in @('.cs', '.ps1', '.cmd', '.yml', '.yaml', '.json', '.md', '.txt', '.xml', '.xaml', '.config', '.csproj')) {
        $text = [IO.File]::ReadAllText($file.FullName)
        if ($text -match '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----|gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}|sk-[A-Za-z0-9]{30,}') {
            $failures.Add("Possible secret: $relative")
        }
    }
}

# Check repository-relative Markdown links without contacting external sites.
$linkCount = 0
foreach ($file in $files | Where-Object Extension -eq '.md') {
    $markdown = [regex]::Replace([IO.File]::ReadAllText($file.FullName), '(?ms)^```.*?^```\s*$', '')
    foreach ($match in [regex]::Matches($markdown, '\]\((?<target><[^>]+>|[^\s)]+)(?:\s+"[^"]*")?\)')) {
        $target = $match.Groups['target'].Value.Trim('<', '>')
        if ($target -match '^(?:[a-z][a-z0-9+.-]*:|//|#)' -or $target.Contains('${{')) { continue }
        $target = [Uri]::UnescapeDataString(($target -split '[?#]', 2)[0])
        if (!$target) { continue }
        $base = if ($target.StartsWith('/')) { $rootPath } else { $file.DirectoryName }
        $resolved = [IO.Path]::GetFullPath((Join-Path $base $target.TrimStart('/')))
        if ((!$resolved.Equals($rootPath, [StringComparison]::OrdinalIgnoreCase) -and !$resolved.StartsWith($rootPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) -or !(Test-Path -LiteralPath $resolved)) {
            $failures.Add("Broken local link: $([IO.Path]::GetRelativePath($rootPath, $file.FullName)) -> $target")
        }
        $linkCount++
    }
}

[xml]$project = Get-Content -LiteralPath (Join-Path $projectPath 'FlowVPN.WinUI.csproj') -Raw
$version = $project.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
foreach ($manifestName in @('Package.appxmanifest', 'Package.Preview.appxmanifest')) {
    [xml]$manifest = Get-Content -LiteralPath (Join-Path $projectPath $manifestName) -Raw
    if ($manifest.Package.Identity.Version -cne "$version.0") { $failures.Add("Version mismatch: $manifestName") }
}
if ($failures.Count) { throw ($failures -join [Environment]::NewLine) }
[pscustomobject]@{
    FilesChecked = $files.Count
    LocalLinksChecked = $linkCount
    SourceFilesChecked = $sourceCheck.FilesChecked
    PowerShellSyntaxValid = $true
    VersionsMatch = $true
    Note = 'Local hygiene checks; not a security audit, network link checker, or complete Markdown parser.'
}
