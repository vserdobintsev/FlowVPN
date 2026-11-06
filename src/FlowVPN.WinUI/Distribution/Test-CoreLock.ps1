param([Parameter(Mandatory)][string]$CoreDirectory,[string]$LockFile=(Join-Path $PSScriptRoot '..\engine.lock.json'))
$ErrorActionPreference='Stop'
$lock=Get-Content -LiteralPath $LockFile -Raw | ConvertFrom-Json
foreach($entry in $lock.Files.PSObject.Properties) {
    $path=Join-Path $CoreDirectory $entry.Name
    if(!(Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.Value){throw "Core lock mismatch: $($entry.Name)"}
}
$version=& (Join-Path $CoreDirectory 'sing-box.exe') version
if($LASTEXITCODE -ne 0 -or $version[0] -ne "sing-box version $($lock.Version)"){throw 'Unexpected sing-box version'}
Write-Output "Verified sing-box $($lock.Version) and locked companion files."
