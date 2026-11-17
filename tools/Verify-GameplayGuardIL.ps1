param(
    [Parameter(Mandatory=$true)][string]$BuiltDll,
    [Parameter(Mandatory=$true)][string]$GameDir
)
$ErrorActionPreference='Stop'
if(!(Test-Path -LiteralPath $BuiltDll)){throw 'BuiltDll not found'}
if(!(Test-Path -LiteralPath $GameDir)){throw 'GameDir not found'}
Add-Type -Path (Join-Path $GameDir 'BepInEx\core\Mono.Cecil.dll')
$a=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($BuiltDll)
try {
 function Method([string]$type,[string]$name) {
  $t=$a.MainModule.Types|Where-Object FullName -eq $type
  if(!$t){throw "MISSING_TYPE=$type"}
  $m=@($t.Methods|Where-Object Name -eq $name)
  if($m.Count -ne 1){throw "MISSING_OR_DUPLICATE_METHOD=$type::$name"}
  return $m[0]
 }
 $awake=Method 'PncCustomEnemies.CustomEnemyPlugin' 'Awake'
 $awakeCalls=@($awake.Body.Instructions|Where-Object{$null -ne $_.Operand}|ForEach-Object{$_.Operand.ToString()})
 if(!($awakeCalls|Where-Object{$_ -match 'GameplayGuards::Install'})){throw 'Actual compiled plugin never starts public gameplay guards'}
 $install=Method 'PncCustomEnemies.GameplayGuards' 'Install'
 $strings=@($install.Body.Instructions|Where-Object{$_.OpCode.Name -eq 'ldstr'}|ForEach-Object{$_.Operand})
 if('femboy_witch' -notin $strings -or 'joker_wall' -notin $strings){throw 'Actual compiled package IDs incorrect'}
 if('femboy-witch' -in $strings -or 'joker-wall' -in $strings){throw 'Compiled assembly still uses erroneous hyphen package IDs'}
 $guardCalls=@($install.Body.Instructions|Where-Object{$null -ne $_.Operand}|ForEach-Object{$_.Operand.ToString()})
 foreach($needle in @('PackageAssemblies::IsLoaded','PackageLifecycle::Active','SingleWitchSpawnGuard','JokerWallSupportGuard::Install','JokerDetectionRangeGuard::Install','JokerGameOverVisualTick')){
 if(!($guardCalls|Where-Object{$_ -match [regex]::Escape($needle)})){throw "Compiled guard wiring missing: $needle"}
 }
 $tick=Method 'PncCustomEnemies.GameplayGuards' 'LateTick'
 $tickStrings=@($tick.Body.Instructions|Where-Object{$_.OpCode.Name -eq 'ldstr'}|ForEach-Object{$_.Operand})
 if('joker_wall' -notin $tickStrings){throw 'Compiled Joker Game Over tick uses wrong package ID'}
 $prefix=Method 'PncCustomEnemies.JokerWallSupportGuard' 'TryFindWallPrefix'
 $pars=@($prefix.Parameters)
 if(($pars|ForEach-Object Name) -join ',' -ne '__0,__1,__2,__3,__result' -or $prefix.ReturnType.FullName -ne 'System.Boolean' -or $pars[3].ParameterType.FullName -ne 'UnityEngine.RaycastHit&'){
 throw 'Harmony wall selector prefix method contract mismatch'
 }
 $wallType=$a.MainModule.Types|Where-Object FullName -eq 'PncCustomEnemies.JokerWallSupportGuard'
 $physics=@($wallType.Methods|Where-Object HasBody|ForEach-Object{$_.Body.Instructions}|Where-Object {$_.Operand -and $_.Operand.ToString() -match 'Physics::'}|ForEach-Object {$_.Operand.ToString()})
 if(!($physics|Where-Object{$_ -match 'Physics::RaycastNonAlloc'})){throw 'RaycastNonAlloc missing from Joker compiled wall code'}
 if(!($physics|Where-Object{$_ -match 'Physics::OverlapBoxNonAlloc'})){throw 'OverlapBoxNonAlloc missing from Joker compiled wall code'}
 if($physics|Where-Object{$_ -match 'Physics::RaycastAll|Physics::OverlapBox\('}){throw 'Allocating Joker physics query slipped into release'}
 $witch=Method 'PncCustomEnemies.SingleWitchSpawnGuard' 'CountLiveSpawnedWitches'
 $wCalls=@($witch.Body.Instructions|Where-Object{$_.Operand}|ForEach-Object{$_.Operand.ToString()})
 if(!($wCalls|Where-Object{$_ -match 'HashSet.*IntersectWith'})){throw 'Compiled Witch lifetime tracking missing'}
 if(!($wCalls|Where-Object{$_ -match 'FindObjectsByType'})){throw 'Compiled Witch real-instance scan missing'}
 $range=Method 'PncCustomEnemies.JokerDetectionRangeGuard' 'PreserveOriginalForceCurve'
 if(!$range.HasBody){throw 'Compiled Joker original force-curve transpiler missing'}
 'PASS ACTUAL COMPILED DLL: correct runtime IDs; no hyphen names'
 'PASS ACTUAL COMPILED DLL: loader installs Witch and Joker safeguards'
 'PASS ACTUAL COMPILED DLL: Joker game-over overlay is activated for joker_wall'
 'PASS ACTUAL COMPILED DLL: Harmony wall selector signature and NonAlloc physics intact'
 'PASS ACTUAL COMPILED DLL: Witch persistence until killed and Joker force-preserving transpiler included'
}finally{$a.Dispose()}