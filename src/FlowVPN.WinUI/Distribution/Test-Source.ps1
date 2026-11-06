param([Parameter(Mandatory)][string]$Root)
$ErrorActionPreference = 'Stop'
$rootPath = (Resolve-Path -LiteralPath $Root).Path
$files = @(Get-ChildItem -LiteralPath $rootPath -Recurse -File | Where-Object { $_.FullName -notmatch '[\\/](bin|obj|\.git)[\\/]' })
$forbidden = @($files | Where-Object { $_.Name -match '(?i)^(settings\.dat|runtime\.json|password\.dpapi)$|\.(pfx|p12|ovpn|key|pem|log)$' })
if ($forbidden.Count) { throw ('Private file types found: ' + ($forbidden.Name -join ', ')) }
$pattern = '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----|gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}|sk-[A-Za-z0-9]{30,}'
$matches = @($files | Where-Object { $_.Extension -in '.cs','.xml','.xaml','.json','.ps1','.md','.yml','.txt','.config','.csproj' } | Select-String -Pattern $pattern)
if ($matches.Count) { throw ('Possible private content at ' + (($matches | ForEach-Object { [IO.Path]::GetRelativePath($rootPath, $_.Path) + ':' + $_.LineNumber }) -join ', ')) }
[pscustomobject]@{ FilesChecked = $files.Count; SecretPatternsFound = 0; PrivateFileTypesFound = 0; Note = 'Pattern-based check; not a guarantee that no secret exists.' }
