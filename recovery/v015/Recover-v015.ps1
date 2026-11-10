param(
    [string]$GameExe
)
$ErrorActionPreference = 'Stop'
try {
    Write-Host 'PNC Framework v0.1.5 signed rollback recovery' -ForegroundColor Cyan
    Write-Host 'This uses the game-owned trusted rollback; it does NOT delete scripts or saves.'
    if([string]::IsNullOrWhiteSpace($GameExe)) {
        Add-Type -AssemblyName System.Windows.Forms
        $picker = New-Object System.Windows.Forms.OpenFileDialog
        $picker.Title = 'Select your actual Post Nut Calamity.exe (not a desktop shortcut)'
        $picker.Filter = 'Post Nut Calamity|Post Nut Calamity.exe'
        $picker.CheckFileExists = $true
        if($picker.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) {
            Write-Host 'Cancelled. No files were changed.'
            exit 0
        }
        $GameExe = $picker.FileName
    }
    $exe=[IO.Path]::GetFullPath($GameExe)
    if([IO.Path]::GetFileName($exe) -ne 'Post Nut Calamity.exe' -or !(Test-Path -LiteralPath $exe)) {
        throw 'Choose the actual Post Nut Calamity.exe file.'
    }
    $root=[IO.Path]::GetDirectoryName($exe)
    $rootItem=Get-Item -LiteralPath $root
    if(($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Linked directories are not safe update targets. Select the real game folder.'
    }
    if([Diagnostics.Process]::GetProcessesByName('Post Nut Calamity').Length -gt 0) {
        throw 'Close ALL running Post Nut Calamity windows before recovery.'
    }
    $up=Join-Path $root 'BepInEx\framework-updates'
    $trusted=Join-Path $root 'BepInEx\framework-updater'
    $policy=Join-Path $trusted 'UpdatePolicy.cs'
    $trust=Join-Path $trusted 'Trust.xml'
    $expected=@{
        'UpdatePolicy.cs'='F956375A7DA5402315C89D3DCAAFBCA452CFAB6B0FCB25A8481CDAD4EF873D71'
        'Trust.xml'='5B4779511E1D1277018B381A28EA5AE2CB023703DBA1F78313C7E26B800E239F'
    }
    foreach($p in $expected.Keys) {
        $full=Join-Path $trusted $p
        if(!(Test-Path -LiteralPath $full)) {throw "Trusted updater file missing: $p"}
        $hash=(Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash
        if($hash -ne $expected[$p]) {throw "Trusted updater component differs from official release: $p"}
    }
    Add-Type -Path $policy -ReferencedAssemblies @('System.Xml.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll')
    [PncFramework.Update.Policy]::ValidateGame($root)
    $publicKey=[IO.File]::ReadAllText($trust)
    [void][PncFramework.Update.Policy]::VerifyInstalled($root,$publicKey,'stable','0.1.5')
    $txFile=Join-Path $up 'transaction.xml'
    if(!(Test-Path -LiteralPath $txFile)) {throw 'Rollback journal missing; nothing was changed.'}
    [xml]$tx=Get-Content -LiteralPath $txFile
    if($tx.transaction.release -ne '0.1.5' -or $tx.transaction.state -ne 'complete') {
        throw 'Not a completed v0.1.5 update; recovery is not needed or cannot proceed.'
    }
    if(Test-Path (Join-Path $up 'pending')) {
        throw 'An update is still pending. Complete or cancel it before recovery.'
    }
    Write-Host 'Verified signed v0.1.5 receipt and original rollback journal.' -ForegroundColor Green
    Write-Host 'Restoring official v0.1.4 updater state...'
    $gameRunning=[Func[bool]]{[Diagnostics.Process]::GetProcessesByName('Post Nut Calamity').Length -gt 0}
    [PncFramework.Update.Policy]::Rollback($root,$gameRunning)
    [void][PncFramework.Update.Policy]::VerifyInstalled($root,$publicKey,'stable','0.1.4')
    Write-Host ''
    Write-Host 'SUCCESS: Signed rollback completed. Your gallery and saves were not removed.' -ForegroundColor Green
    Write-Host 'Now open the game from its real folder. Use the golden arrow > Check Again to install v0.1.6 or newer.'
    Write-Host 'Close the separate EDI desktop app if the game needs to merge previously missing scripts.'
    exit 0
} catch {
    Write-Host ''
    Write-Host ('RECOVERY STOPPED: '+$_.Exception.Message) -ForegroundColor Red
    Write-Host 'No manual DLL replacement or signed receipt editing was attempted.'
    exit 1
}