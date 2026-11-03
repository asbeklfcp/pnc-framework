# Build a child-only environment. Never change the game, user or machine environment.
function New-PncRestartStartInfo {
    param([Parameter(Mandatory=$true)][string]$GameRoot)
    $resolved=[IO.Path]::GetFullPath($GameRoot)
    $exe=Join-Path $resolved 'Post Nut Calamity.exe'
    if(-not [IO.File]::Exists($exe)){throw 'Testing game executable was not found'}
    $start=[Diagnostics.ProcessStartInfo]::new()
    $start.FileName=$exe;$start.WorkingDirectory=$resolved;$start.UseShellExecute=$false
    # Doorstop sets these process-local guards during its first bootstrap. A restarted
    # child must bootstrap afresh, rather than inherit the previous process's state.
    $start.EnvironmentVariables.Remove('DOORSTOP_INITIALIZED')
    $start.EnvironmentVariables.Remove('DOORSTOP_DISABLE')
    return $start
}
