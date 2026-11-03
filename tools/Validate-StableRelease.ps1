param([Parameter(Mandatory=$true)][string]$ManifestPath,[Parameter(Mandatory=$true)][string]$HistoryPath,[Parameter(Mandatory=$true)][version]$ExpectedVersion,[string]$PackagePath,[switch]$Download)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Add-Type -Path (Join-Path $repo 'updater/UpdatePolicy.cs') -ReferencedAssemblies @('System.Xml.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll')
Add-Type -Path (Join-Path $repo 'src/PncModManager/ReleaseHistory.cs') -ReferencedAssemblies @('System.Xml.dll','System.Core.dll')
$trust=[IO.File]::ReadAllText((Join-Path $repo 'updater/Trust.xml'))
$release=[PncFramework.Update.Policy]::Verify([IO.File]::ReadAllBytes($ManifestPath),$trust,'stable',[DateTime]::UtcNow)
if($release.Id -ne $ExpectedVersion.ToString()){throw 'Unexpected version'}
$feed=[Uri]'https://raw.githubusercontent.com/asbeklfcp/pnc-framework/main/updates/stable/latest.xml'
$url=[PncFramework.Update.Policy]::ValidateUrl($release.PackageUrl,'stable',$feed)
$history=[PncFramework.UpdateUI.ReleaseHistory]::Parse([PncFramework.Update.Policy]::VerifySigned([IO.File]::ReadAllBytes($HistoryPath),$trust),'stable')
if(@($history.Releases|Where-Object Version -EQ $release.Id).Count -ne 1){throw 'Expected release missing from signed history'}
if($Download){$bytes=[PncFramework.Update.Policy]::Download($url,33554432,$release.PackageSize)}else{if(-not $PackagePath){throw 'Provide PackagePath or Download'};$bytes=[IO.File]::ReadAllBytes($PackagePath)}
$root=Join-Path ([IO.Path]::GetTempPath()) ('pnc-release-check-'+[Guid]::NewGuid().ToString('N'));[IO.Directory]::CreateDirectory($root)|Out-Null
[PncFramework.Update.Policy]::Extract($bytes,$release,(Join-Path $root 'payload'))
'PASS: pinned signature, version, complete inventory, history, URL, package checksum/size and assembly identities. Verification copy: '+$root
