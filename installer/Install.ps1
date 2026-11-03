param([string]$GameRoot)
$ErrorActionPreference='Stop'
if(-not $GameRoot){
 Add-Type -AssemblyName System.Windows.Forms
 $picker=New-Object System.Windows.Forms.OpenFileDialog
 try{$picker.Title='Select Post Nut Calamity.exe';$picker.Filter='Post Nut Calamity.exe|Post Nut Calamity.exe';$picker.CheckFileExists=$true
 if($picker.ShowDialog() -ne [Windows.Forms.DialogResult]::OK){Write-Output 'Installation cancelled.';return}
 $GameRoot=Split-Path -Parent $picker.FileName
 }finally{$picker.Dispose()}
}
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
function EnsureLoader {
 $required=@('BepInEx/core/BepInEx.Unity.Mono.dll','winhttp.dll','doorstop_config.ini')
 $present=@($required|Where-Object {Test-Path -LiteralPath (Join-Path $GameRoot $_)})
 if($present.Count -eq $required.Count){
  $identity=[Reflection.AssemblyName]::GetAssemblyName((Join-Path $GameRoot $required[0]))
  if($identity.Name -ne 'BepInEx.Unity.Mono' -or $identity.Version.Major -ne 6){throw 'Existing BepInEx is incompatible; no loader files were changed'}
  $stream=[IO.File]::OpenRead((Join-Path $GameRoot 'winhttp.dll'))
  try{$reader=[IO.BinaryReader]::new($stream);$stream.Position=0x3c;$offset=$reader.ReadInt32();$stream.Position=$offset;if($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne 0x8664){throw 'Existing loader is not Windows x64'}}finally{$stream.Dispose()}
  Write-Output 'Existing BepInEx 6 Unity Mono x64 retained.'
  return
 }
 if($present.Count -gt 0 -or (Test-Path -LiteralPath (Join-Path $GameRoot 'BepInEx/core')) -or (Test-Path -LiteralPath (Join-Path $GameRoot '.doorstop_version'))){throw 'Partial or incompatible loader found; no loader files were changed'}
 $scratch=Join-Path ([IO.Path]::GetTempPath()) ('pnc-loader-'+[Guid]::NewGuid().ToString('N'))
 $created=@()
 try{
  New-Item -ItemType Directory -Path $scratch|Out-Null
  $archive=Join-Path $scratch 'loader.zip'
  Write-Output 'Downloading tested BepInEx 6.0.0-be.785 Unity Mono x64...'
  [Net.ServicePointManager]::SecurityProtocol=[Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
  Invoke-WebRequest -UseBasicParsing -Uri 'https://builds.bepinex.dev/projects/bepinex_be/785/BepInEx-Unity.Mono-win-x64-6.0.0-be.785%2B6abdba4.zip' -OutFile $archive
  if((Hash $archive) -ne 'db430f14d6661eb38ba96fcc13c07a163e87e553710821d87e5129f915a1b26b'){throw 'BepInEx download checksum mismatch; nothing extracted'}
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  $zip=[IO.Compression.ZipFile]::OpenRead($archive)
  try{
   $plan=@();$seen=@{}
   foreach($entry in $zip.Entries){
    if($entry.FullName.EndsWith('/')){continue}
    $relative=$entry.FullName
    if($relative -notmatch '^(BepInEx/(core|patchers|plugins)/[A-Za-z0-9_.+/-]+|changelog\.txt|doorstop_config\.ini|winhttp\.dll|\.doorstop_version)$' -or $relative.Contains('..')){throw 'Unsafe loader archive path'}
    $dest=[IO.Path]::GetFullPath((Join-Path $GameRoot $relative))
    if(-not $dest.StartsWith($GameRoot.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase) -or $seen.ContainsKey($dest)){throw 'Unsafe or duplicate loader path'}
    $seen[$dest]=$true
    $dir=[IO.DirectoryInfo]::new((Split-Path $dest))
    while($dir){if($dir.Exists -and ($dir.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Linked loader path refused'};$dir=$dir.Parent}
    if(Test-Path -LiteralPath $dest){throw ('Existing file would be overwritten: '+$relative)}
    $plan+=@{entry=$entry;destination=$dest}
   }
   foreach($requiredFile in $required){if(-not $seen.ContainsKey([IO.Path]::GetFullPath((Join-Path $GameRoot $requiredFile)))){throw 'Incomplete loader archive'}}
   EnsureClosed
   foreach($file in $plan){EnsureClosed;New-Item -ItemType Directory -Path (Split-Path $file.destination) -Force|Out-Null;$created+=$file.destination;[IO.Compression.ZipFileExtensions]::ExtractToFile($file.entry,$file.destination,$false)}
  }finally{$zip.Dispose()}
  Write-Output 'Verified BepInEx 6.0.0-be.785 installed.'
 }catch{
  foreach($file in $created){if(Test-Path -LiteralPath $file){Remove-Item -LiteralPath $file}}
  throw
 }finally{
  # This unique temporary directory is owned by this invocation only.
  if(Test-Path -LiteralPath $scratch){Remove-Item -LiteralPath $scratch -Recurse -Force}
 }
}
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
# Configuration-only migration: never touch Unity saves, registry or discoveries.
$configPath=Target 'BepInEx/config/com.edi.pnc.cfg'
$configOverride=$null
if(Test-Path -LiteralPath $configPath){
 $configText=[IO.File]::ReadAllText($configPath)
 $galleryPattern='(?ims)(^\[Gallery\][^\r\n]*\r?\n)(.*?)(?=^\[|\z)'
 $gallery=[regex]::Match($configText,$galleryPattern)
 $unlock=if($gallery.Success){[regex]::Match($gallery.Groups[2].Value,'(?im)^\s*UnlockAll\s*=\s*(true|false)\s*$')}else{$null}
 if(-not $unlock -or -not $unlock.Success -or $unlock.Groups[1].Value -ieq 'true'){
  Write-Output 'An older install may have enabled Gallery Unlock All by default. Saved discoveries will not be changed.'
  $choice=Read-Host 'Use normal gallery progression (N), or KEEP your existing unlock preference (K)? Enter N or K'
  while($choice -notmatch '^(N|K)$'){$choice=Read-Host 'Enter N for normal progression or K to keep Unlock All'}
  if($choice -ieq 'N'){
   if($gallery.Success){
    $body=$gallery.Groups[2].Value
    if($unlock.Success){$body=[regex]::Replace($body,'(?im)^(\s*UnlockAll\s*=\s*)true\s*$','${1}false')}else{$body="UnlockAll = false`r`n"+$body}
    $configOverride=$configText.Substring(0,$gallery.Index)+$gallery.Groups[1].Value+$body+$configText.Substring($gallery.Index+$gallery.Length)
   }else{$configOverride=$configText+"`r`n[Gallery]`r`nUnlockAll = false`r`n"}
  }
 }
}
$changes=@()
foreach($f in $manifest.files){
 $source=Join-Path $package ('payload/'+$f.path);$destination=Target $f.path
 if((Hash $source) -ne $f.sha256){throw 'Package checksum mismatch'}
 $correctConfig=$f.path -eq 'BepInEx/config/com.edi.pnc.cfg' -and $null -ne $configOverride
 if($f.mode -eq 'seed' -and (Test-Path -LiteralPath $destination) -and -not $correctConfig){continue}
 $old=$null
 if(Test-Path -LiteralPath $destination){
  $old=Hash $destination;$allowed=@($f.sha256)+@($f.allowedExisting)
  if($f.path -like 'BepInEx/plugins/*' -and $authenticated.ContainsKey([IO.Path]::GetFileName($destination))){$allowed+=@($authenticated[[IO.Path]::GetFileName($destination)])}
  if($prior){$allowed+=@($prior.files|Where-Object path -EQ $f.path|ForEach-Object sha256)}
  if(-not $correctConfig -and $allowed -notcontains $old){throw ('Existing framework file differs from supported versions: '+$f.path)}
 }
 $newHash=$f.sha256
 if($correctConfig){$sha=[Security.Cryptography.SHA256]::Create();try{$newHash=([BitConverter]::ToString($sha.ComputeHash([Text.UTF8Encoding]::new($false).GetBytes($configOverride)))).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose()}}
 $changes+=@{path=$f.path;old=$old;new=$newHash;correctConfig=$correctConfig}
}
# All ownership and payload checks finish before any target file is changed.
EnsureLoader
$backup=Join-Path $GameRoot ('BepInEx/framework-install-backups/'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $backup -Force|Out-Null
if(Test-Path -LiteralPath $record){Copy-Item -LiteralPath $record -Destination (Join-Path $backup 'previous-install.json')}
foreach($f in $changes){if($f.old){$dest=Join-Path $backup $f.path;New-Item -ItemType Directory -Path (Split-Path $dest) -Force|Out-Null;Copy-Item -LiteralPath (Target $f.path) -Destination $dest;if((Hash $dest) -ne $f.old){throw 'Backup verification failed'}}}
@{state='prepared';files=$changes} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $backup 'journal.json')
try{
 foreach($f in $changes){EnsureClosed;$dest=Target $f.path;New-Item -ItemType Directory -Path (Split-Path $dest) -Force|Out-Null;$temp=$dest+'.pnc-stage';if($f.correctConfig){[IO.File]::WriteAllText($temp,$configOverride,[Text.UTF8Encoding]::new($false))}else{Copy-Item -LiteralPath (Join-Path $package ('payload/'+$f.path)) -Destination $temp};if((Hash $temp) -ne $f.new){throw 'Staged copy failed'};if(Test-Path -LiteralPath $dest){[IO.File]::Replace($temp,$dest,$dest+".pnc-replaced");Remove-Item -LiteralPath ($dest+".pnc-replaced")}else{[IO.File]::Move($temp,$dest)}}
 @{version=$manifest.version;files=$manifest.files;backup=$backup} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $record
 @{state='complete';files=$changes} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $backup 'journal.json')
}catch{
 foreach($f in $changes){$dest=Target $f.path;if($f.old){Copy-Item -LiteralPath (Join-Path $backup $f.path) -Destination $dest -Force}elseif(Test-Path -LiteralPath $dest){Remove-Item -LiteralPath $dest}}
 if(Test-Path -LiteralPath (Join-Path $backup 'previous-install.json')){Copy-Item -LiteralPath (Join-Path $backup 'previous-install.json') -Destination $record -Force}elseif(Test-Path -LiteralPath $record){Remove-Item -LiteralPath $record}
 throw
}
Write-Output ('PNC Framework '+$manifest.version+' installed. Settings and unrelated mods retained. Verified rollback backup: '+$backup)
