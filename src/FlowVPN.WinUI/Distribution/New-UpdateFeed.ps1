# Generates local release files. Does not upload, install or trust certificates.
param(
 [Parameter(Mandatory)][string]$PackagePath,
 [Parameter(Mandatory)][uri]$BaseUri,
 [Parameter(Mandatory)][ValidateSet('stable','preview')][string]$Channel,
 [Parameter(Mandatory)][string]$OutputDirectory,
 [string]$Notes='Обновление FlowVPN и проверенного движка sing-box.',
 [switch]$AllowDevelopmentCertificate,
 [uri]$PackageUri,
 [uri]$AppInstallerUri,
 [uri]$FeedUri,
 [string]$PublicCertificate
)
$ErrorActionPreference='Stop'
foreach($uri in @($BaseUri,$PackageUri,$AppInstallerUri,$FeedUri)){
 if($null -ne $uri -and (!$uri.IsAbsoluteUri -or $uri.Scheme -ne 'https' -or $uri.UserInfo -or $uri.Query -or $uri.Fragment)){throw 'Release URLs must be absolute HTTPS URLs without credentials, queries or fragments'}
}
if($Notes.Length -gt 16000){throw 'Release notes are too long'}
$base=[uri]($BaseUri.AbsoluteUri.TrimEnd('/')+'/')
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.Security.Cryptography.Pkcs
$payload = & (Join-Path $PSScriptRoot 'Test-PackagePayload.ps1') -PackagePath $PackagePath
if(!$payload.PayloadIntegrityVerified){throw 'The package payload does not match its cryptographic signature'}
$zip=[IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($PackagePath))
function Read-Entry([string]$name) {
 $entry=$zip.GetEntry($name);if(!$entry){throw "Package entry missing: $name"}
 $buffer=[IO.MemoryStream]::new();$stream=$entry.Open()
 try{$stream.CopyTo($buffer);return ,$buffer.ToArray()}finally{$stream.Dispose();$buffer.Dispose()}
}
try {
 [xml]$manifest=[Text.Encoding]::UTF8.GetString((Read-Entry 'AppxManifest.xml')).TrimStart([char]0xFEFF)
 $identity=$manifest.Package.Identity
 $expected=if($Channel -eq 'preview'){'FlowVPN.Preview'}else{'FlowVPN'}
 if($identity.Name -ne $expected){throw 'Channel and package identity do not match'}
 if($identity.ProcessorArchitecture -ne 'x64'){throw 'This release pipeline currently targets x64'}
 if($identity.Publisher -like '*Development*' -and !$AllowDevelopmentCertificate){throw 'Development certificate requires explicit local preview mode'}
 if($AllowDevelopmentCertificate -and $Channel -ne 'preview'){throw 'Development signing cannot produce a stable feed'}
 $bytes=Read-Entry 'AppxSignature.p7x';$cms=[Security.Cryptography.Pkcs.SignedCms]::new();$cms.Decode($bytes[4..($bytes.Length-1)]);$cms.CheckSignature($true)
 if($cms.SignerInfos.Count -ne 1 -or $cms.SignerInfos[0].Certificate.Subject -ne $identity.Publisher){throw 'Signature publisher mismatch'}
 if($PublicCertificate) {
   if($Channel -ne 'stable' -or $AllowDevelopmentCertificate){throw 'Pinned public certificates are supported only for stable release packages'}
   $pin=(Get-FileHash -LiteralPath $PublicCertificate -Algorithm SHA256).Hash
   $verification=& (Join-Path $PSScriptRoot 'Install-FlowVPN.ps1') -PackagePath $PackagePath -CertificatePath $PublicCertificate -ExpectedCertificateSha256 $pin -ValidateOnly
   if(!$verification.PinnedCertificateMatched -or !$verification.PayloadIntegrityVerified){throw 'The package does not match the explicitly pinned public certificate'}
 } elseif(!$AllowDevelopmentCertificate) {
   $chain=[Security.Cryptography.X509Certificates.X509Chain]::new()
   try{if(!$chain.Build($cms.SignerInfos[0].Certificate)){throw 'Signer is not trusted by this build machine'}}finally{$chain.Dispose()}
 }
 $minimum=([version]$manifest.Package.Dependencies.TargetDeviceFamily.MinVersion).Build
 $defer=$manifest.SelectSingleNode('//*[local-name()="UpdateWhileInUse"]')
 if(!$defer -or $defer.InnerText -ne 'defer'){throw 'Safe update deferral is missing from the manifest'}
 $lock=Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\engine.lock.json') -Raw | ConvertFrom-Json
 foreach($entry in $lock.Files.PSObject.Properties) {
   $hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData((Read-Entry ('core/'+$entry.Name))))
   if($hash -ne $entry.Value){throw "Bundled engine file does not match the compatibility lock: $($entry.Name)"}
 }
} finally {$zip.Dispose()}
$fileName=[IO.Path]::GetFileName($PackagePath)
$resolvedPackageUri=if($PackageUri){$PackageUri}else{[uri]::new($base,"packages/$fileName")}
$installerUri=if($AppInstallerUri){$AppInstallerUri}else{[uri]::new($base,"$Channel/FlowVPN.appinstaller")}
$release=[ordered]@{SchemaVersion=1;Channel=$Channel;Version=[string]$identity.Version;CoreVersion=$lock.Version;PackageName=[string]$identity.Name;
 Publisher=[string]$identity.Publisher;Architecture=[string]$identity.ProcessorArchitecture;MinimumWindowsBuild=$minimum;
 PackageUrl=$resolvedPackageUri.AbsoluteUri;AppInstallerUrl=$installerUri.AbsoluteUri;Sha256=(Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256).Hash;
 SizeBytes=(Get-Item -LiteralPath $PackagePath).Length;Notes=$Notes}
$channelDirectory=Join-Path $OutputDirectory $Channel
New-Item -ItemType Directory -Path $channelDirectory -Force | Out-Null
$release | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $channelDirectory 'latest.json') -Encoding utf8NoBOM
$namespace='http://schemas.microsoft.com/appx/appinstaller/2018'
$xml=[xml]::new();$root=$xml.CreateElement('AppInstaller',$namespace);$xml.AppendChild($root)|Out-Null
$root.SetAttribute('Version',[string]$identity.Version);$root.SetAttribute('Uri',$installerUri.AbsoluteUri)
$main=$xml.CreateElement('MainPackage',$namespace)
foreach($name in @('Name','Publisher','Version','ProcessorArchitecture')){$main.SetAttribute($name,[string]$identity.$name)}
$main.SetAttribute('Uri',$resolvedPackageUri.AbsoluteUri);$root.AppendChild($main)|Out-Null
$settings=$xml.CreateElement('UpdateSettings',$namespace);$root.AppendChild($settings)|Out-Null
$launch=$xml.CreateElement('OnLaunch',$namespace);$launch.SetAttribute('HoursBetweenUpdateChecks','8');$launch.SetAttribute('ShowPrompt','true');$launch.SetAttribute('UpdateBlocksActivation','false');$settings.AppendChild($launch)|Out-Null
$settings.AppendChild($xml.CreateElement('AutomaticBackgroundTask',$namespace))|Out-Null
$writerSettings=[Xml.XmlWriterSettings]::new();$writerSettings.Indent=$true;$writerSettings.Encoding=[Text.UTF8Encoding]::new($false)
$writer=[Xml.XmlWriter]::Create((Join-Path $channelDirectory 'FlowVPN.appinstaller'),$writerSettings)
try{$xml.Save($writer)}finally{$writer.Dispose()}
$stableFeed=if($FeedUri -and $Channel -eq 'stable'){$FeedUri.AbsoluteUri}else{[uri]::new($base,'stable/latest.json').AbsoluteUri}
$previewFeed=if($FeedUri -and $Channel -eq 'preview'){$FeedUri.AbsoluteUri}else{[uri]::new($base,'preview/latest.json').AbsoluteUri}
$configuration=[ordered]@{SchemaVersion=1;Channels=@(
 @{Id='stable';Name='Стабильный';PackageName='FlowVPN';FeedUrl=$stableFeed},
 @{Id='preview';Name='Предварительный';PackageName='FlowVPN.Preview';FeedUrl=$previewFeed})}
$configuration | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'UpdateChannels.for-next-build.json') -Encoding utf8NoBOM
Write-Output "Generated local release metadata in $channelDirectory. Nothing has been published."
