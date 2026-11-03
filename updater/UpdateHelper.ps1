param([Parameter(Mandatory=$true)][string]$RequestPath,[switch]$Rollback,[switch]$Quiet)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.IO.Compression
# This helper ships with the trusted baseline, never in a downloaded package.
Add-Type -Path (Join-Path $PSScriptRoot 'UpdatePolicy.cs') -ReferencedAssemblies @('System.Xml.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll')
$root='';$installed='';$channel='';$envelope=$null;$old=$null
[PncFramework.Update.Policy]::ReadRequest([IO.File]::ReadAllBytes($RequestPath),[ref]$root,[ref]$installed,[ref]$channel,[ref]$envelope,[ref]$old)
$root=[IO.Path]::GetFullPath($root)
$expected=[IO.Path]::GetFullPath((Join-Path $root 'BepInEx/framework-updates/request.xml'))
if([IO.Path]::GetFullPath($RequestPath) -ne $expected){throw 'Unexpected update request location'}
$log=Join-Path $root 'BepInEx/framework-updates/helper.log'
$updaterHome=Join-Path $root 'BepInEx/framework-updates'
$pending=Join-Path $updaterHome 'pending'
$cancel=Join-Path $updaterHome 'cancel'
[PncFramework.Update.Policy]::NoLinks($updaterHome)
function Show-Result([string]$message,[string]$kind='success'){
    if($Quiet){Write-State $message;return}
    try { & (Join-Path $PSScriptRoot 'Show-ParchmentNotification.ps1') -GameRoot $root -Message $message -Kind $kind }
    catch {
        Write-State ('Native notification typography failed; using legible serif fallback: '+$_.Exception.Message)
        try { & (Join-Path $PSScriptRoot 'Show-ParchmentNotification.ps1') -GameRoot $root -Message $message -Kind $kind -UseFallbackFont }
        catch { Write-State ('Notification renderer failed; install/rollback result is unchanged: '+$_.Exception.Message) }
    }
}
function Write-State([string]$message){ ('{0:u} {1}' -f [DateTime]::UtcNow,$message) | Add-Content -LiteralPath $log }
$gameRunning={
    foreach($process in [Diagnostics.Process]::GetProcessesByName('Post Nut Calamity')){
        try { if([IO.Path]::GetDirectoryName($process.MainModule.FileName) -eq $root){return $true} }
        catch { return $true } # Do not install when the process cannot be identified safely.
    }
    return $false
}
try {
    [IO.File]::WriteAllText((Join-Path $updaterHome 'ready'),'ready')
    Write-State 'Waiting for Testing/game to close. No plugin files have been changed.'
    $deadline=[DateTime]::UtcNow.AddMinutes(30)
    while((& $gameRunning)){
        if(Test-Path -LiteralPath $cancel){Write-State 'Update cancelled before installation';exit 0}
        if([DateTime]::UtcNow -gt $deadline){throw 'Timed out waiting for game exit; nothing installed'}
        Start-Sleep -Milliseconds 500
    }
    if($Rollback){
        [PncFramework.Update.Policy]::Rollback($root,[Func[bool]]$gameRunning)
        Write-State 'Rollback complete. Settings retained. You may restart the game.'
        Show-Result 'The previous framework version has been restored. Settings were retained. You may restart the game.' 'rollback'
    } else {
        $trust=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Trust.xml'))
        $release=[PncFramework.Update.Policy]::Verify($envelope,$trust,$channel,[DateTime]::UtcNow)
        if(-not [PncFramework.Update.Policy]::IsNewer($release,$installed)){throw 'Update is not newer than the requested baseline'}
        [PncFramework.Update.Policy]::ValidateGame($root)
        [PncFramework.Update.Policy]::VerifyInstalled($root,$trust,$channel,$installed) | Out-Null
        [PncFramework.Update.Policy]::CheckUnchanged($root,$old)
        if(Test-Path -LiteralPath $cancel){Write-State 'Update cancelled before installation';exit 0}
        $stage=[IO.File]::ReadAllText($pending)
        if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($stage)) -ne [IO.Path]::GetFullPath($updaterHome) -or -not [IO.Path]::GetFileName($stage).StartsWith('payload-')){throw 'Unsafe staged payload location'}
        [PncFramework.Update.Policy]::NoLinks($stage)
        foreach($file in $release.Files){[PncFramework.Update.Policy]::ValidateAssembly((Join-Path $stage $file.Name),$file)}
        [PncFramework.Update.Policy]::Install($root,$release,$stage,$old,[Func[bool]]$gameRunning,$null,$envelope)
        Write-State ('Installed release '+$release.Id+'. Settings retained. You may restart the game.')
        Show-Result ('PNC Framework update '+$release.Id+' is installed. You may restart the game. Your settings and other mods were retained.')
    }
} catch {
    Write-State ('FAILED: '+$_.Exception.Message)
    Show-Result ('The update could not finish: '+$_.Exception.Message+'. Your rollback backup has been retained. Please check the updater log before restarting.') 'error'
    exit 1
} finally {
    if(Test-Path -LiteralPath $pending){Remove-Item -LiteralPath $pending}
}

