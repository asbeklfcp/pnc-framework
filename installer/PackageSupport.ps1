function Get-PncInstalledPluginHashes {
 param([string]$GameRoot,$PackageManifest,$PriorManifest)
 $helper=Join-Path $GameRoot 'BepInEx/framework-updater'
 if(-not(Test-Path -LiteralPath (Join-Path $helper 'baseline-inventory.xml'))){return @{}}
 # Never compile helper code or trust a key which is not one of the recorded helper versions.
 foreach($f in $PackageManifest.files|Where-Object {$_.path -like 'BepInEx/framework-updater/*'}){
  $path=Join-Path $GameRoot $f.path
  if(-not(Test-Path -LiteralPath $path)){continue}
  $known=@($f.sha256)+@($f.allowedExisting)
  if($PriorManifest){$known+=@($PriorManifest.files|Where-Object path -EQ $f.path|ForEach-Object sha256)}
  if($known -notcontains (Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant()){throw 'Installed updater helper was modified; inspect before maintenance'}
 }
 if(-not('PncFramework.Update.Policy' -as [type])){Add-Type -Path (Join-Path $helper 'UpdatePolicy.cs') -ReferencedAssemblies @('System.Xml.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll')}
 $key=[IO.File]::ReadAllText((Join-Path $helper 'Trust.xml'))
 $baseline=[xml][Text.Encoding]::UTF8.GetString([PncFramework.Update.Policy]::VerifySigned([IO.File]::ReadAllBytes((Join-Path $helper 'baseline-inventory.xml')),$key))
 try{$verified=[PncFramework.Update.Policy]::VerifyInstalled($GameRoot,$key,$baseline.DocumentElement.channel,$baseline.DocumentElement.id)}
 catch{
  $receipt=Join-Path $GameRoot 'BepInEx/framework-updates/installed-release.xml'
  if(-not(Test-Path -LiteralPath $receipt)){throw}
  $doc=[xml][Text.Encoding]::UTF8.GetString([PncFramework.Update.Policy]::VerifySigned([IO.File]::ReadAllBytes($receipt),$key))
  $verified=[PncFramework.Update.Policy]::VerifyInstalled($GameRoot,$key,$doc.DocumentElement.channel,$doc.DocumentElement.id)
 }
 $result=@{};foreach($pair in $verified.GetEnumerator()){$result[$pair.Key]=$pair.Value};return $result
}
function Assert-PncNoLinks([string]$Path){
 $item=[IO.FileInfo]::new([IO.Path]::GetFullPath($Path))
 if($item.Exists -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Linked framework file refused'}
 $directory=[IO.DirectoryInfo]::new((Split-Path ([IO.Path]::GetFullPath($Path))))
 while($directory){if($directory.Exists -and ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Linked maintenance path refused'};$directory=$directory.Parent}
}
