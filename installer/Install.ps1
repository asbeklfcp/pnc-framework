param([string]$GameRoot)
$ErrorActionPreference='Stop'
if(-not $GameRoot){$GameRoot=Read-Host 'Folder containing Post Nut Calamity.exe'}
$GameRoot=[IO.Path]::GetFullPath($GameRoot.Trim('"'))
$package=$PSScriptRoot
$manifest=Get-Content -LiteralPath (Join-Path $package 'package-manifest.json') -Raw|ConvertFrom-Json
function Hash([string]$path){(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}
function Target([string]$relative){
 if($relative -notmatch '^(BepInEx/(plugins/[^/]+\.dll|framework-updater/[A-Za-z0-9_./-]+|config/[^/]+\.cfg))$' -or $relative.Contains('..')){throw 'Unsafe package path'}
 $p=[IO.Path]::GetFullPath((Join-Path $GameRoot $relative))
 if(-not $p.StartsWith($GameRoot.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Path escapes game folder'}
 $dir=[IO.DirectoryInfo]::new((Split-Path $p))
 while($dir){if($dir.Exists -and ($dir.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Linked install path refused'};$dir=$dir.Parent}
 if((Test-Path -LiteralPath $p) -and ((Get-Item -LiteralPath $p).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Linked file refused'}
 return $p
}
function EnsureClosed {
 foreach($process in [Diagnostics.Process]::GetProcessesByName('Post Nut Calamity')){
  try{$running=$process.MainModule.FileName}catch{throw 'Could not verify whether the game is closed'}
  if([IO.Path]::GetFullPath($running) -eq (Join-Path $GameRoot 'Post Nut Calamity.exe')){throw 'Close this game before installing'}
 }
}
EnsureClosed
if((Hash (Join-Path $GameRoot 'Post Nut Calamity_Data/Managed/Assembly-CSharp.dll')) -ne $manifest.gameHash){throw 'This package requires the verified PNC 0.4.2 Windows Mono build'}
foreach($required in @('BepInEx/core/BepInEx.Unity.Mono.dll','winhttp.dll','doorstop_config.ini')){if(-not(Test-Path -LiteralPath (Join-Path $GameRoot $required))){throw 'Install the documented BepInEx 6 Unity Mono prerequisite first'}}
if(Test-Path -LiteralPath (Join-Path $GameRoot 'BepInEx/framework-updates/pending')){throw 'Finish or cancel the pending in-game update first'}
$record=Join-Path $GameRoot 'BepInEx/framework-install.json'
$prior=$null;if(Test-Path -LiteralPath $record){$prior=Get-Content -LiteralPath $record -Raw|ConvertFrom-Json}
$names=@('PncEdi','PncControls','PncCustomEnemies','PncModManager')
foreach($dll in Get-ChildItem -LiteralPath (Join-Path $GameRoot 'BepInEx/plugins') -Filter '*.dll' -Recurse -File -ErrorAction SilentlyContinue){
 try{$identity=[Reflection.AssemblyName]::GetAssemblyName($dll.FullName).Name}catch{continue}
 if($names -contains $identity -and $dll.FullName -ne (Join-Path $GameRoot ('BepInEx/plugins/'+$identity+'.dll'))){throw 'Duplicate or renamed framework plugin found; remove the duplicate before installing'}
}
. (Join-Path $PSScriptRoot 'PackageSupport.ps1')
$authenticated=Get-PncInstalledPluginHashes -GameRoot $GameRoot -PackageManifest $manifest -PriorManifest $prior
$changes=@()
foreach($f in $manifest.files){
 $source=Join-Path $package ('payload/'+$f.path);$destination=Target $f.path
 if((Hash $source) -ne $f.sha256){throw 'Package checksum mismatch'}
 if($f.mode -eq 'seed' -and (Test-Path -LiteralPath $destination)){continue}
 $old=$null
 if(Test-Path -LiteralPath $destination){
  $old=Hash $destination;$allowed=@($f.sha256)+@($f.allowedExisting)
  if($f.path -like 'BepInEx/plugins/*' -and $authenticated.ContainsKey([IO.Path]::GetFileName($destination))){$allowed+=@($authenticated[[IO.Path]::GetFileName($destination)])}
  if($prior){$allowed+=@($prior.files|Where-Object path -EQ $f.path|ForEach-Object sha256)}
  if($allowed -notcontains $old){throw ('Existing framework file differs from supported versions: '+$f.path)}
 }
 $changes+=@{path=$f.path;old=$old;new=$f.sha256}
}
# All ownership and payload checks finish before any target file is changed.
$backup=Join-Path $GameRoot ('BepInEx/framework-install-backups/'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $backup -Force|Out-Null
if(Test-Path -LiteralPath $record){Copy-Item -LiteralPath $record -Destination (Join-Path $backup 'previous-install.json')}
foreach($f in $changes){if($f.old){$dest=Join-Path $backup $f.path;New-Item -ItemType Directory -Path (Split-Path $dest) -Force|Out-Null;Copy-Item -LiteralPath (Target $f.path) -Destination $dest;if((Hash $dest) -ne $f.old){throw 'Backup verification failed'}}}
@{state='prepared';files=$changes} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $backup 'journal.json')
try{
 foreach($f in $changes){EnsureClosed;$dest=Target $f.path;New-Item -ItemType Directory -Path (Split-Path $dest) -Force|Out-Null;$temp=$dest+'.pnc-stage';Copy-Item -LiteralPath (Join-Path $package ('payload/'+$f.path)) -Destination $temp;if((Hash $temp) -ne $f.new){throw 'Staged copy failed'};if(Test-Path -LiteralPath $dest){[IO.File]::Replace($temp,$dest,$dest+".pnc-replaced");Remove-Item -LiteralPath ($dest+".pnc-replaced")}else{[IO.File]::Move($temp,$dest)}}
 @{version=$manifest.version;files=$manifest.files;backup=$backup} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $record
 @{state='complete';files=$changes} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $backup 'journal.json')
}catch{
 foreach($f in $changes){$dest=Target $f.path;if($f.old){Copy-Item -LiteralPath (Join-Path $backup $f.path) -Destination $dest -Force}elseif(Test-Path -LiteralPath $dest){Remove-Item -LiteralPath $dest}}
 if(Test-Path -LiteralPath (Join-Path $backup 'previous-install.json')){Copy-Item -LiteralPath (Join-Path $backup 'previous-install.json') -Destination $record -Force}elseif(Test-Path -LiteralPath $record){Remove-Item -LiteralPath $record}
 throw
}
Write-Output ('PNC Framework '+$manifest.version+' installed. Settings and unrelated mods retained. Verified rollback backup: '+$backup)
