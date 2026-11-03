param([string]$GameRoot)
$ErrorActionPreference='Stop'
if(-not $GameRoot){$GameRoot=Read-Host 'Folder containing Post Nut Calamity.exe'}
$GameRoot=[IO.Path]::GetFullPath($GameRoot.Trim('"'))
foreach($p in [Diagnostics.Process]::GetProcessesByName('Post Nut Calamity')){if($p.MainModule.FileName -eq (Join-Path $GameRoot 'Post Nut Calamity.exe')){throw 'Close this game first'}}
. (Join-Path $PSScriptRoot 'PackageSupport.ps1')
$record=Join-Path $GameRoot 'BepInEx/framework-install.json';$manifest=Get-Content -LiteralPath $record -Raw|ConvertFrom-Json
$backup=[IO.Path]::GetFullPath($manifest.backup)
if(-not $backup.StartsWith((Join-Path $GameRoot 'BepInEx/framework-install-backups/'),[StringComparison]::OrdinalIgnoreCase)){throw 'Unexpected backup location'}
$journal=Get-Content -LiteralPath (Join-Path $backup 'journal.json') -Raw|ConvertFrom-Json
foreach($f in $journal.files){
 if($f.path -like 'BepInEx/config/*'){continue}
 if($f.path.Contains('..') -or $f.path -notmatch '^BepInEx/(plugins/[^/]+\.dll|framework-updater/[A-Za-z0-9_./-]+)$'){throw 'Unsafe rollback record'}
 $target=Join-Path $GameRoot $f.path;Assert-PncNoLinks $target
 if((Get-FileHash -LiteralPath $target).Hash.ToLowerInvariant() -ne $f.new){throw 'Installed files changed since installer backup; use in-game update rollback first'}
 if($f.old -and (Get-FileHash -LiteralPath (Join-Path $backup $f.path)).Hash.ToLowerInvariant() -ne $f.old){throw 'Backup checksum mismatch'}
}
foreach($f in $journal.files){if($f.path -like 'BepInEx/config/*'){continue};$target=Join-Path $GameRoot $f.path;if($f.old){Copy-Item -LiteralPath (Join-Path $backup $f.path) -Destination $target -Force}else{Remove-Item -LiteralPath $target}}
if(Test-Path -LiteralPath (Join-Path $backup 'previous-install.json')){Copy-Item -LiteralPath (Join-Path $backup 'previous-install.json') -Destination $record -Force}else{Remove-Item -LiteralPath $record}
Write-Output 'Previous framework files restored. Current settings, bindings and unrelated plugins retained.'
