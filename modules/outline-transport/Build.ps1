#requires -Version 7.4
[CmdletBinding()]
param(
    [string]$GoExe = 'go',
    [string]$OutputDirectory = '',
    [string]$SingBoxPath = ''
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$GoExe = (Get-Command -Name $GoExe -CommandType Application -ErrorAction Stop).Source
$callerDirectory = $PWD.Path
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot 'work/outline-artifacts' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory, $callerDirectory)
if ($SingBoxPath) { $SingBoxPath = [IO.Path]::GetFullPath($SingBoxPath, $callerDirectory) }
$lock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'toolchain.lock.json') -Raw | ConvertFrom-Json
$environmentNames = @('GOTOOLCHAIN', 'GOWORK', 'GOOS', 'GOARCH', 'GOAMD64', 'CGO_ENABLED', 'GOFLAGS', 'FLOWVPN_SING_BOX')
$previousEnvironment = @{}
foreach ($name in $environmentNames) { $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
Push-Location -LiteralPath $PSScriptRoot
try {
    $env:GOTOOLCHAIN = 'local'
    $env:GOWORK = 'off'
    $env:GOOS = 'windows'
    $env:GOARCH = 'amd64'
    $env:GOAMD64 = 'v1'
    $env:CGO_ENABLED = '0'
    $env:GOFLAGS = ''
    $env:FLOWVPN_SING_BOX = $SingBoxPath
    $version = & $GoExe version
    if ($LASTEXITCODE -ne 0 -or $version -ne "go version $($lock.Version) windows/amd64") {
        throw "Use the pinned Windows amd64 toolchain: $($lock.Version)."
    }
    & $GoExe mod verify
    if ($LASTEXITCODE -ne 0) { throw 'Go module verification failed.' }
    & $GoExe vet -mod=readonly ./...
    if ($LASTEXITCODE -ne 0) { throw 'Go vet failed.' }
    & $GoExe test -mod=readonly -count=1 -timeout 60s ./...
    if ($LASTEXITCODE -ne 0) { throw 'Adapter tests failed.' }
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $executable = Join-Path $OutputDirectory 'flowvpn-outline-transport.exe'
    & $GoExe build -mod=readonly -trimpath -buildvcs=false -ldflags '-s -w -buildid=' -o $executable .
    if ($LASTEXITCODE -ne 0) { throw 'Adapter build failed.' }
    Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination (Join-Path $OutputDirectory 'LICENSE')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.md') -Destination $OutputDirectory
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'licenses') -Destination $OutputDirectory -Recurse -Force
    $metadata = [ordered]@{
        Id = 'outline-transport'
        Version = '1.0.0'
        ProtocolVersion = 1
        Architecture = 'win-x64'
        Filename = 'flowvpn-outline-transport.exe'
        SizeBytes = (Get-Item -LiteralPath $executable).Length
        Sha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant()
        GoVersion = $lock.Version
        OutlineSdkVersion = 'v0.0.23'
        Capabilities = @('outline.ss-prefix.tcp.v1')
    }
    $metadata | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'artifact.json') -Encoding utf8NoBOM
    $metadata | ConvertTo-Json -Depth 4
}
finally {
    Pop-Location
    foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process') }
}
