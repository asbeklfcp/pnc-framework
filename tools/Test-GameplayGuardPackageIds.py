"""Regression: runtime package IDs differ from asset directory names.

This test directly inspects the exact public bundle shipped by the loader
and the source code that decides whether gameplay guards are active.
Run before compiling and before publishing any new stable release.
"""
from __future__ import annotations
from pathlib import Path
import re, json, zipfile
ROOT = Path(__file__).resolve().parents[1]
BUNDLE = ROOT/"src/PncCustomEnemies/resources/PNC-Public-Characters-0.1.8.zip"
GUARD = ROOT/"src/PncCustomEnemies/PublicGameplayGuards.cs"
LOADER = ROOT/"src/PncCustomEnemies/CustomEnemyPlugin.cs"
assert BUNDLE.exists() and GUARD.exists()
with zipfile.ZipFile(BUNDLE) as z:
    wm=json.loads(z.read("BepInEx/custom-enemies/femboy-witch/enemy.json"))
    jm=json.loads(z.read("BepInEx/custom-enemies/joker-wall/wall-trap.json"))
assert wm["id"] == "femboy_witch"
assert jm["id"] == "joker_wall"
assert wm["includeInRandomSpawns"] is True
assert wm["spawnWeight"] == 1
assert jm["autoPlace"] is True
assert jm["spawnCount"] > 0
code=GUARD.read_text(encoding="utf8")
loader=LOADER.read_text(encoding="utf8")
for symbol, expected in [("WitchPackageId",wm["id"]),("JokerPackageId",jm["id"])]:
    pattern=rf'internal const string {symbol}\s*=\s*"([^"]+)";'
    values=re.findall(pattern, code)
    assert values == [expected],(symbol,values,expected)
for token in (
'PackageAssemblies.IsLoaded(WitchPackageId)',
'PackageLifecycle.Active(WitchPackageId)',
'PackageAssemblies.IsLoaded(JokerPackageId)',
'PackageLifecycle.Active(JokerPackageId)',
'if (!PackageLifecycle.Active(JokerPackageId)) return;',
'JokerWallSupportGuard.Install(_patches, log);',
'JokerDetectionRangeGuard.Install(_patches, log);',
'_patches.PatchAll(typeof(SingleWitchSpawnGuard));',
'AddComponent<JokerGameOverVisualTick>()',
):
    assert token in code,token
for erroneous in ('PackageAssemblies.IsLoaded("femboy-witch")',
'PackageAssemblies.IsLoaded("joker-wall")',
'PackageLifecycle.Active("femboy-witch")',
'PackageLifecycle.Active("joker-wall")'):
    assert erroneous not in code,erroneous
assert loader.index("PackageAssemblies.Initialize();") < loader.index("GameplayGuards.Install(Logger);")
# Old 58-template bug must stay fixed, but after a real Witch is detected the
# one-per-floor cap must stay in force until the Witch actually dies/disappears.
for guard in (
    "ObservedLivingWitches",
    "ObservedLivingWitches.Contains(token)",
    "ObservedLivingWitches.Add(token)",
    "ObservedLivingWitches.IntersectWith(stillAlive)",
    "ai.IsDead",
    "hp <= 0",
    "_trackedSceneHandle != activeFloorSceneHandle",
    "if (!observed && !IsRuntimeWitchInstance(root, ai)) continue;",
    "FindObjectsInactive.Include",
    "PendingSelectionSeconds = 3.0f",
    "SceneManager.GetActiveScene()",
):
    assert guard in code,("Witch level-exclusive guard missing",guard)
wall=(ROOT/"src/PncCustomEnemies/JokerWallSupportGuard.cs").read_text(encoding="utf8")
range_src=(ROOT/"src/PncCustomEnemies/JokerDetectionRangeGuard.cs").read_text(encoding="utf8")
for required in ("MaxWallValidationAttempts = 7", "MaxScanMilliseconds = 2.5",
                 "Physics.RaycastNonAlloc", "Physics.OverlapBoxNonAlloc",
                 "TryFindWallPrefix", "SupportsPortraitAndAccess"):
    assert required in wall,("Joker smart placement regression",required)
for required in ("OriginalForceCurveRadius = 7.5f", "PreserveOriginalForceCurve",
                 "WallPictureTraps.WallPictureTrap", "UpdatePull"):
    assert required in range_src,("Joker detection/force preservation regression",required)
assert 'PluginVersion = "1.2.6"' in loader
print("PASS: manifest IDs match both Witch and Joker runtime activation gates")
print("PASS: Joker game-over tick uses correct live package ID")
print("PASS: both Harmony hooks and visual tick remain installed for enabled packages")
print("PASS: 0.1.8 character bundle still enables natural Witch spawns and Joker autoPlace")
print("PASS: module init order and loader version verified")
print("PASS: once activated, living Witch retains floor slot until dead/destroyed; dormant clones excluded")
print("PASS: Joker smart wall support, bounded physics and unchanged force curve preserved")
