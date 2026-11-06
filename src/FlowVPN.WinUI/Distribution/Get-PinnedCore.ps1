# Prepare build inputs only. Never installs or changes a running VPN.
param([Parameter(Mandatory)][string]$Destination,[string]$ArchivePath,[string]$LockFile=(Join-Path $PSScriptRoot '..\engine.lock.json'))
$ErrorActionPreference='Stop'
$lock=Get-Content -LiteralPath $LockFile -Raw | ConvertFrom-Json
$destinationPath=[IO.Path]::GetFullPath($Destination)
New-Item -ItemType Directory -Path $destinationPath -Force | Out-Null
if(!$ArchivePath) {
 $cache=Join-Path ([IO.Path]::GetDirectoryName($destinationPath)) '.core-downloads'
 New-Item -ItemType Directory -Path $cache -Force | Out-Null
 $ArchivePath=Join-Path $cache ('sing-box-'+$lock.Version+'.zip')
 $uri=[uri]$lock.ArchiveUrl
 if($uri.Scheme -ne 'https' -or $uri.Host -ne 'github.com' -or $uri.AbsolutePath -notlike '/SagerNet/sing-box/releases/download/*'){throw 'Unexpected archive source'}
 Invoke-WebRequest -Uri $uri -OutFile $ArchivePath -TimeoutSec 300
}
if((Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash -ne $lock.ArchiveSha256){throw 'Archive checksum mismatch'}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive=[IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($ArchivePath))
try {
 foreach($name in (@($lock.Files.PSObject.Properties.Name)+@('LICENSE'))) {
  if([IO.Path]::GetFileName($name) -ne $name){throw 'Invalid locked filename'}
  $entries=@($archive.Entries | Where-Object Name -eq $name)
  if($entries.Count -ne 1){throw "Expected one archive entry: $name"}
  $target=Join-Path $destinationPath $name
  if($entries[0].Length -gt 400MB){throw 'Unexpected archive entry size'}
  $source=$entries[0].Open();$file=[IO.File]::Create($target)
  try{$source.CopyTo($file)}finally{$file.Dispose();$source.Dispose()}
 }
}finally{$archive.Dispose()}
& (Join-Path $PSScriptRoot 'Test-CoreLock.ps1') -CoreDirectory $destinationPath -LockFile $LockFile
