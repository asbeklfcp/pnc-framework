param([string]$GameRoot)
$ErrorActionPreference='Stop'
if(-not $GameRoot){$GameRoot=Read-Host 'Folder containing Post Nut Calamity.exe'}
$GameRoot=[IO.Path]::GetFullPath($GameRoot.Trim('"'))
foreach($p in [Diagnostics.Process]::GetProcessesByName('Post Nut Calamity')){if($p.MainModule.FileName -eq (Join-Path $GameRoot 'Post Nut Calamity.exe')){throw 'Close this game first'}}
$record=Join-Path $GameRoot 'BepInEx/framework-install.json'
if(-not(Test-Path -LiteralPath $record)){throw 'No framework installation record; do not remove files by guessing'}
$manifest=Get-Content -LiteralPath $record -Raw|ConvertFrom-Json
. (Join-Path $PSScriptRoot 'PackageSupport.ps1')
$authenticated=Get-PncInstalledPluginHashes -GameRoot $GameRoot -PackageManifest $manifest -PriorManifest $manifest
$owned=@()
foreach($f in $manifest.files){
 if($f.mode -eq 'seed'){continue}
 if($f.path.Contains('..') -or $f.path -notmatch '^BepInEx/(plugins/[^/]+\.dll|framework-updater/[A-Za-z0-9_./-]+)$'){throw 'Unsafe removal record'}
 $target=[IO.Path]::GetFullPath((Join-Path $GameRoot $f.path));if(-not $target.StartsWith($GameRoot.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe removal path'}
 Assert-PncNoLinks $target
 if(Test-Path -LiteralPath $target){$expected=$f.sha256;if($f.path -like 'BepInEx/plugins/*' -and $authenticated.ContainsKey([IO.Path]::GetFileName($target))){$expected=$authenticated[[IO.Path]::GetFileName($target)]};if(((Get-Item -LiteralPath $target).Attributes -band [IO.FileAttributes]::ReparsePoint) -or (Get-FileHash -LiteralPath $target).Hash.ToLowerInvariant() -ne $expected){throw ('Changed file retained; inspect before removal: '+$f.path)};$owned+=$target}
}
$quarantine=Join-Path $GameRoot ('BepInEx/framework-removed/'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $quarantine -Force|Out-Null
foreach($target in $owned){$relative=$target.Substring($GameRoot.TrimEnd('\').Length+1);$dest=Join-Path $quarantine $relative;New-Item -ItemType Directory -Path (Split-Path $dest) -Force|Out-Null;Move-Item -LiteralPath $target -Destination $dest}
Move-Item -LiteralPath $record -Destination (Join-Path $quarantine 'framework-install.json')
Write-Output 'Framework removed from loading paths. Settings, saves, unrelated plugins, BepInEx and rollback backups retained.'
