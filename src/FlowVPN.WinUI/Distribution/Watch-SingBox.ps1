# Reports upstream releases; never downloads executables or modifies engine.lock.json.
param([string]$LockFile=(Join-Path $PSScriptRoot '..\engine.lock.json'),[string]$OutputFile)
$ErrorActionPreference='Stop'
$lock=Get-Content -LiteralPath $LockFile -Raw | ConvertFrom-Json
$headers=@{'User-Agent'='FlowVPN-release-monitor';'Accept'='application/vnd.github+json'}
if($env:GITHUB_TOKEN){$headers.Authorization="Bearer $env:GITHUB_TOKEN"}
$latest=Invoke-RestMethod -Uri 'https://api.github.com/repos/SagerNet/sing-box/releases/latest' -Headers $headers -TimeoutSec 30
if($latest.draft -or $latest.prerelease){throw 'Expected a stable upstream release'}
$version=$latest.tag_name.TrimStart('v')
if($version -notmatch '^\d+\.\d+\.\d+$'){throw 'Unexpected upstream release version'}
$report=[ordered]@{Pinned=$lock.Version;Latest=$version;ReviewNeeded=([version]$version -gt [version]$lock.Version);ReleaseUrl=$latest.html_url;AutomaticEngineReplacement=$false}
if($OutputFile){$report|ConvertTo-Json|Set-Content -LiteralPath $OutputFile -Encoding utf8NoBOM}
$report|ConvertTo-Json
if($env:GITHUB_STEP_SUMMARY -and $report.ReviewNeeded){"sing-box $version is available (pinned $($lock.Version)). Compatibility review required: $($latest.html_url)" | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY}
