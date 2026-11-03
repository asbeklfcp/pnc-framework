param([Parameter(Mandatory=$true)][version]$Version,[Parameter(Mandatory=$true)][version]$PreviousVersion,[Parameter(Mandatory=$true)][string]$PayloadDirectory,[Parameter(Mandatory=$true)][string[]]$ChangedFiles,[Parameter(Mandatory=$true)][string]$KeyPath,[Parameter(Mandatory=$true)][string]$NotesFile,[Parameter(Mandatory=$true)][string]$HistoryXml,[Parameter(Mandatory=$true)][string]$OutputDirectory,[switch]$Approved)
$ErrorActionPreference='Stop'
if(-not $Approved){throw 'Explicit release approval is required'}
if($Version -le $PreviousVersion){throw 'Version must be newer'}
$allowed=@('PncModManager.dll','PncEdi.dll','PncControls.dll','PncCustomEnemies.dll')
if($ChangedFiles.Count -lt 1 -or @($ChangedFiles|Select-Object -Unique).Count -ne $ChangedFiles.Count){throw 'Invalid changed-file list'}
foreach($name in $ChangedFiles){if($allowed -notcontains $name){throw 'Unapproved update file'}}
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if([IO.Path]::GetFullPath($KeyPath).StartsWith($repo.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Keep the private key outside the repository'}
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Directory]::CreateDirectory($OutputDirectory)|Out-Null
$archive=Join-Path $OutputDirectory ('PNC-Framework-v'+$Version+'-PNC0.4.2-win64.update.zip')
if(Test-Path -LiteralPath $archive){throw 'Output archive already exists; never overwrite release artifacts'}
$zip=[IO.Compression.ZipFile]::Open($archive,[IO.Compression.ZipArchiveMode]::Create)
try{foreach($name in $ChangedFiles|Sort-Object){[IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,(Join-Path $PayloadDirectory $name),$name)|Out-Null}}finally{$zip.Dispose()}
if((Get-Item -LiteralPath $archive).Length -gt 33554432){throw 'Updater package size limit exceeded'}
$doc=[xml]'<release/>';$r=$doc.DocumentElement
foreach($kv in @{schema='1';id=$Version.ToString();channel='stable';approved='true';gameHash='86b559f74066d0013f32cffd9e38f5a5749c356f5bc29f761c1656ffe39433ce';publishedUtc=[DateTime]::UtcNow.AddMinutes(-1).ToString('o');expiresUtc=[DateTime]::UtcNow.AddDays(90).ToString('o')}.GetEnumerator()){$r.SetAttribute($kv.Key,$kv.Value)}
$n=$doc.CreateElement('notes');$n.InnerText=[IO.File]::ReadAllText($NotesFile);$r.AppendChild($n)|Out-Null
$p=$doc.CreateElement('package');$p.SetAttribute('url','https://github.com/asbeklfcp/pnc-framework/releases/download/v'+$Version+'/PNC-Framework-v'+$Version+'-PNC0.4.2-win64.update.zip');$p.SetAttribute('bytes',(Get-Item -LiteralPath $archive).Length);$p.SetAttribute('sha256',(Get-FileHash -LiteralPath $archive).Hash.ToLowerInvariant());$r.AppendChild($p)|Out-Null
$files=$doc.CreateElement('files');$inventory=$doc.CreateElement('inventory');$r.AppendChild($files)|Out-Null;$r.AppendChild($inventory)|Out-Null
foreach($name in $allowed){$path=Join-Path $PayloadDirectory $name;$hash=(Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant();$f=$doc.CreateElement('file');$f.SetAttribute('name',$name);$f.SetAttribute('sha256',$hash);$inventory.AppendChild($f)|Out-Null;if($ChangedFiles -contains $name){$f=$doc.CreateElement('file');$f.SetAttribute('name',$name);$f.SetAttribute('sha256',$hash);$f.SetAttribute('bytes',(Get-Item -LiteralPath $path).Length);$identity=[Reflection.AssemblyName]::GetAssemblyName($path);$f.SetAttribute('assembly',$identity.Name);$f.SetAttribute('version',$identity.Version.ToString());$files.AppendChild($f)|Out-Null}}
$rsa=[Security.Cryptography.RSA]::Create()
try{$rsa.FromXmlString([IO.File]::ReadAllText($KeyPath));foreach($item in @(@{name='latest.xml';xml=$doc.OuterXml},@{name='history.xml';xml=[IO.File]::ReadAllText($HistoryXml)})){$bytes=[Text.Encoding]::UTF8.GetBytes($item.xml);$signature=$rsa.SignData($bytes,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1);[IO.File]::WriteAllText((Join-Path $OutputDirectory $item.name),'<signedRelease><payload>'+[Convert]::ToBase64String($bytes)+'</payload><signature>'+[Convert]::ToBase64String($signature)+'</signature></signedRelease>')}}finally{$rsa.Dispose()}
'Prepared signed files locally. Nothing uploaded or activated. Validate before publication.'
