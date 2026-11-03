using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.Mono;
using BepInEx.Unity.Mono.Configuration;
using UnityEngine;

namespace PncEdi;

/// <summary>
/// Every setting the mod exposes, and the one method that binds them.
///
/// Split out of Plugin.cs, which was 1846 lines with 150 of these declarations and a 200-line
/// `Bind` run wedged into the middle of `Awake`. It is the same class - `partial` - so every
/// `Plugin.CfgX` call site across the other 63 files is untouched, and `cfgaudit.py` (which
/// greps the whole `PncEdi/` directory for `.Bind&lt;T&gt;("Section", "Key"`) still sees all of it.
///
/// The section name in each `Bind` call is what the written config file groups by, so a setting
/// declared here under the wrong section lands in the wrong `[block]` of com.edi.pnc.cfg and the
/// user's existing value stops being read. `cfgaudit.py` is the check for that.
/// </summary>
public partial class Plugin
{
	internal static ConfigEntry<string> CfgEdiUrl;
	internal static ConfigEntry<bool> CfgDebug;
	internal static ConfigEntry<string> CfgFillerGallery;
	internal static ConfigEntry<string> CfgFillerDamageGalleryMap;
	internal static ConfigEntry<string> CfgFillerCumGalleryMap;
	internal static ConfigEntry<string> CfgFillerIntensityPriority;
	internal static ConfigEntry<float> CfgFillerIntensityHysteresis;

	internal static ConfigEntry<int> CfgMasterIntensity;

	internal static ConfigEntry<string> CfgRowIntensityScale;
	internal static ConfigEntry<bool> CfgFillerInMenus;
	internal static ConfigEntry<bool> CfgFillerWhilePaused;
	internal static ConfigEntry<bool> CfgEnableSerpentHypnosis;
	internal static ConfigEntry<float> CfgSerpentHypnosisViewGrace;
	internal static ConfigEntry<string> CfgSerpentHypnosisGallery;
	internal static ConfigEntry<int> CfgSerpentHypnosisIntensityFar;
	internal static ConfigEntry<int> CfgSerpentHypnosisIntensityNear;
	internal static ConfigEntry<int> CfgSerpentHypnosisIntensityStep;
	internal static ConfigEntry<float> CfgSerpentHypnosisIntensityInterval;
	internal static ConfigEntry<float> CfgSerpentHypnosisIntensityRelease;
	internal static ConfigEntry<bool> CfgEnableChaserStomp;
	internal static ConfigEntry<float> CfgChaserStompRange;
	internal static ConfigEntry<int> CfgChaserStompIntensityFar;
	internal static ConfigEntry<int> CfgChaserStompIntensityNear;
	internal static ConfigEntry<float> CfgChaserStompIntensityNearDistance;
	internal static ConfigEntry<int> CfgChaserStompIntensityStep;
	internal static ConfigEntry<float> CfgChaserStompIntensityInterval;
	internal static ConfigEntry<float> CfgChaserStompGrace;
	internal static ConfigEntry<string> CfgChaserStompDragonGallery;
	internal static ConfigEntry<string> CfgChaserStompWendigoGallery;
	internal static ConfigEntry<bool> CfgEnableAmbient;
	internal static ConfigEntry<float> CfgAmbientRange;
	internal static ConfigEntry<string> CfgAmbientPatterns;
	internal static ConfigEntry<string> CfgAmbientPrefix;
	internal static ConfigEntry<float> CfgAmbientScanInterval;
	internal static ConfigEntry<bool> CfgAmbientDiag;
	internal static ConfigEntry<float> CfgAmbientDiagRange;
	internal static ConfigEntry<float> CfgAmbientHearingMultiplier;
	internal static ConfigEntry<bool> CfgEnableImp;
	internal static ConfigEntry<string> CfgImpPrefix;
	internal static ConfigEntry<string> CfgGrapplePrefixMap;
	internal static ConfigEntry<float> CfgImpGrappleLoopDelaySeconds;
	internal static ConfigEntry<float> CfgGrappleTierDropHoldSeconds;
	internal static ConfigEntry<bool> CfgImpGrappleReinforcement;
	internal static ConfigEntry<float> CfgImpGrappleReinforcementInterval;
	internal static ConfigEntry<float> CfgImpGrappleReinforcementRamp;
	internal static ConfigEntry<int> CfgImpGrappleReinforcementCeiling;
	internal static ConfigEntry<bool> CfgGoonShroomGrappleReinforcement;
	internal static ConfigEntry<float> CfgGoonShroomGrappleReinforcementInterval;
	internal static ConfigEntry<float> CfgGoonShroomGrappleReinforcementRamp;
	internal static ConfigEntry<int> CfgGoonShroomGrappleReinforcementCeiling;
	internal static ConfigEntry<float> CfgNunGrabPreferenceMultiplier;
	internal static ConfigEntry<float> CfgGargoyleGrabRate;
	internal static ConfigEntry<float> CfgGargoyleShootRate;
	internal static ConfigEntry<float> CfgGargoyleSpinRangeScale;
	internal static ConfigEntry<float> CfgGargoyleSpinCooldownScale;
	internal static ConfigEntry<float> CfgPlantashaGrabRate;
	internal static ConfigEntry<float> CfgPlantashaShootRate;
	internal static ConfigEntry<float> CfgEnemyProjectileSpeedMultiplier;
	internal static ConfigEntry<bool> CfgEnableInteractive;
	internal static ConfigEntry<string> CfgEnemyNameRemap;
	internal static ConfigEntry<string> CfgGrabVariantSuffixes;
	internal static ConfigEntry<string> CfgGalleryAliases;
	internal static ConfigEntry<string> CfgInGameAliases;
	internal static ConfigEntry<bool> CfgUnlockAllGallery;
	internal static ConfigEntry<string> CfgDioramaGalleryMap;
	internal static ConfigEntry<string> CfgPeekGalleryMap;
	internal static ConfigEntry<string> CfgPeekClipMap;
	internal static ConfigEntry<bool> CfgEnableFreecam;
	internal static ConfigEntry<KeyboardShortcut> CfgKeyFreecam;
	internal static ConfigEntry<bool> CfgEnableDebugEnemySpawn;
	internal static ConfigEntry<KeyboardShortcut> CfgKeySpawnZombie;
	internal static ConfigEntry<KeyboardShortcut> CfgKeySpawnPlantasha;
	internal static ConfigEntry<KeyboardShortcut> CfgKeySpawnGargoyle;
	internal static ConfigEntry<KeyboardShortcut> CfgKeySpawnGooper;
	internal static ConfigEntry<KeyboardShortcut> CfgKeySpawnMimic;
	internal static ConfigEntry<KeyboardShortcut> CfgKeySpawnNun;
	internal static ConfigEntry<KeyboardShortcut> CfgKeySpawnImp;
	internal static ConfigEntry<KeyboardShortcut> CfgKeySpawnChaserBoss;
	internal static ConfigEntry<KeyboardShortcut> CfgKeySpawnBlindedBeast;
	internal static ConfigEntry<KeyboardShortcut> CfgKeySpawnGoonShroom;
	internal static ConfigEntry<KeyboardShortcut> CfgKeySpawnSerpent;
	internal static ConfigEntry<KeyboardShortcut> CfgKeySpawnShufflePool;
	internal static ConfigEntry<string> CfgSpawnZombieNameHint;
	internal static ConfigEntry<string> CfgSpawnPlantashaNameHint;
	internal static ConfigEntry<string> CfgSpawnGargoyleNameHint;
	internal static ConfigEntry<string> CfgSpawnGooperNameHint;
	internal static ConfigEntry<string> CfgSpawnMimicNameHint;
	internal static ConfigEntry<string> CfgSpawnNunNameHint;
	internal static ConfigEntry<string> CfgSpawnImpNameHint;
	internal static ConfigEntry<string> CfgSpawnGoonShroomNameHint;
	internal static ConfigEntry<string> CfgSpawnBlindedBeastNameHint;
	internal static ConfigEntry<string> CfgSpawnChaserBossNameHint;
	internal static ConfigEntry<string> CfgSpawnSerpentNameHint;
	internal static ConfigEntry<float> CfgSpawnEnemyDistance;
	internal static ConfigEntry<bool> CfgPauseOnFocusLoss;
	internal static ConfigEntry<GameplayProfile> CfgGameplayProfile;
	internal static ConfigEntry<HandyIntensitySource> CfgHandyIntensitySource;
	internal static ConfigEntry<bool> CfgEnableGameplayTweaks;
	internal static ConfigEntry<bool> CfgGodMode;
	internal static ConfigEntry<bool> CfgEnableHeatLocks;
	internal static ConfigEntry<float> CfgHeatLockAutoHealRate;
	internal static ConfigEntry<bool> CfgClearOverheatAtLockFloor;
	internal static ConfigEntry<bool> CfgDisableCumDamage;
	internal static ConfigEntry<bool> CfgGrappleDeathSequence;
	internal static ConfigEntry<float> CfgGrappleDeathImpInterval;
	internal static ConfigEntry<string> CfgGrappleDeathText;
	internal static ConfigEntry<string> CfgHeatLockScaleSource;
	internal static ConfigEntry<string> CfgHeatPotionLockRemoval;
	internal static ConfigEntry<float> CfgAmbientReleaseLookSeconds;
	internal static ConfigEntry<float> CfgAmbientReleaseDecayMultiplier;
	internal static ConfigEntry<bool> CfgAmbientPlaybackRequiresPresence;
	internal static ConfigEntry<float> CfgAmbientPlaybackLingerSeconds;
	internal static ConfigEntry<bool> CfgAmbientReleaseUseUnlockTriggers;
	internal static ConfigEntry<float> CfgAmbientReleaseTriggerPadding;
	internal static ConfigEntry<float> CfgAmbientPlaybackTriggerPadding;
	internal static ConfigEntry<float> CfgAmbientReleaseLookAngle;
	internal static ConfigEntry<bool> CfgReviveKeepsSceneGallery;
	internal static ConfigEntry<bool> CfgAmbientReleaseClearsAllLocks;
	internal static ConfigEntry<bool> CfgFullLockHoldsCum;
	internal static ConfigEntry<bool> CfgHeatLockOncePerGrabSource;
	internal static ConfigEntry<bool> CfgShowHeatBarDuringGrab;
	internal static ConfigEntry<bool> CfgCumCooldownEndsAtLockFloor;
	internal static ConfigEntry<float> CfgPostCumGraceSeconds;
	internal static ConfigEntry<string> CfgAmbientNearbyIcon;
	internal static ConfigEntry<float> CfgKeyholeReleaseWatchSeconds;
	internal static ConfigEntry<float> CfgAmbientPeekHoleRadius;
	internal static ConfigEntry<int> CfgHeatLockUnitsPerLock;
	internal static ConfigEntry<bool> CfgMimicGrabRespectsImmunity;
	internal static ConfigEntry<float> CfgMimicStrandedRearmSeconds;
	internal static ConfigEntry<bool> CfgMimicReactivateInstantly;
	internal static ConfigEntry<float> CfgAiUnstickSeconds;
	internal static ConfigEntry<bool> CfgEnemyGrabRespectsImmunity;
	internal static ConfigEntry<bool> CfgDebugHotkeysIgnoreHeldKeys;
	internal static ConfigEntry<KeyboardShortcut> CfgKeyRemoveHeat;
	internal static ConfigEntry<KeyboardShortcut> CfgKeyAddHeat;
	internal static ConfigEntry<int> CfgPlayerMaxHealth;
	internal static ConfigEntry<float> CfgEnemyHeatGainRate;
	internal static ConfigEntry<string> CfgClassHeatMultipliers;
	internal static ConfigEntry<bool> CfgConvertBlockedDamageToHeat;
	internal static ConfigEntry<float> CfgSpawnCountMultiplier;
	internal static ConfigEntry<float> CfgSpawnCountMultiplierAtFullLock;
	internal static ConfigEntry<float> CfgSpawnScatterRadius;
	internal static ConfigEntry<bool> CfgEnemySpawnShuffle;
	internal static ConfigEntry<string> CfgEnemySpawnShufflePool;
	internal static ConfigEntry<string> CfgEnemySpawnMode;
	internal static ConfigEntry<string> CfgGrapplerSpawnKeys;
	internal static ConfigEntry<float> CfgGrapplerSpawnWeight;
	internal static ConfigEntry<float> CfgImpSpawnWeightMultiplier;
	internal static ConfigEntry<string> CfgGrabGameOverGrabEnemyKeys;
	internal static ConfigEntry<KeyboardShortcut> CfgKeyEndGrab;
	internal static ConfigEntry<KeyboardShortcut> CfgKeyEndGrabDebug;
	internal static ConfigEntry<float> CfgEndGrabDelaySeconds;
	internal static ConfigEntry<float> CfgEndGrabDelaySecondsChaserBoss;
	internal static ConfigEntry<float> CfgEndGrabDelaySecondsMiniboss;
	internal static ConfigEntry<string> CfgMinibossEnemyKeys;
	internal static ConfigEntry<string> CfgServiceSceneKeys;
	internal static ConfigEntry<int> CfgHeatLockMinibossLocks;
	internal static ConfigEntry<float> CfgEndGrabImmunitySeconds;
	internal static ConfigEntry<float> CfgEndGrabEnemyCooldownSeconds;
	internal static ConfigEntry<bool> CfgHideNearbyEnemiesOnScene;

	internal static ConfigEntry<string> CfgGrabScreenAudioFill;
	internal static ConfigEntry<float> CfgHideNearbyEnemyRadius;
	internal static ConfigEntry<bool> CfgBlockAttacksDuringGrabStruggle;
	internal static ConfigEntry<bool> CfgKeepEnemiesAfterGrab;


	internal static ConfigEntry<float> CfgKeepEnemiesAfterGrabGraceSeconds;
	internal static ConfigEntry<string> CfgGrabReviveHealth;
	internal static ConfigEntry<float> CfgEnemyReactivationDelaySeconds;
	internal static ConfigEntry<bool> CfgEnemyInactiveAutofix;
	internal static ConfigEntry<float> CfgEnemyInactiveAutofixInterval;
	internal static ConfigEntry<float> CfgEnemyInactiveAutofixRadius;

	private void BindConfig()
	{
		CfgEdiUrl = Config.Bind<string>("EDI", "Url", "http://127.0.0.1:5000", "Base URL for the EDI service.");
		CfgDebug = Config.Bind<bool>("EDI", "Debug", true, "Verbose logs.");
		CfgFillerGallery = Config.Bind<string>("EDI", "FillerGallery", "filler", "Gallery to play when idle.");
		CfgFillerDamageGalleryMap = Config.Bind<string>("EDI", "FillerDamageGalleryMap", "25=filler_damage_25;50=filler_damage_50;75=filler_damage_75", "Optional semicolon-separated damage intensity galleries. Format: minimumDamagePercent=gallery; e.g. 25=filler_damage_25;50=filler_damage_50;75=filler_damage_75. Damage percent is based on the biggest recent hit compared to max health.");
		CfgFillerCumGalleryMap = Config.Bind<string>("EDI", "FillerCumGalleryMap", "25=filler_cum_25;50=filler_cum_50;75=filler_cum_75", "Optional semicolon-separated heat intensity galleries. Format: minimumHeatPercent=gallery; e.g. 25=filler_cum_25;50=filler_cum_50;75=filler_cum_75. Heat percent uses the player heat meter, so values are 1-100%.");
		CfgFillerIntensityPriority = Config.Bind<string>("EDI", "FillerIntensityPriority", "Higher", "Which filler wins when the player is both aroused and hurt. Higher = whichever percentage is larger (default). Cum = arousal always wins, damage only below its lowest bucket (pre-2.0.9 behaviour). Damage = the reverse.");
		CfgFillerIntensityHysteresis = Config.Bind<float>("EDI", "FillerIntensityHysteresis", 0.05f, "How far a percentage has to fall back below a bucket's own threshold before the filler drops out of it, as a fraction (0.05 = 5 percentage points). A bucket is entered at its threshold and left only past this margin, so heat or health sitting on a boundary cannot flip the device between two scripts frame after frame. 0 restores the bare threshold test.");
		CfgFillerInMenus = Config.Bind<bool>("EDI", "FillerInMenus", true, "Keep the filler running in the main menu and on the game-over screen instead of stopping the device. The plain FillerGallery row is used rather than the damage/heat ladder, because those percentages belong to a run that has ended. Set false to have menus fall silent, which is what the mod did before 3.0.0. Losing window focus still pauses.");
		CfgFillerWhilePaused = Config.Bind<bool>("EDI", "FillerWhilePaused", false, "Play the base filler behind the pause menu instead of pausing the device with the game - whatever was playing when the menu opened, filler or a real gallery row (a grab, an interact scene), is saved and put back at the same point in its loop when the menu closes. Off by default. Losing window focus pauses regardless of this setting.");
		CfgEnableSerpentHypnosis = Config.Bind<bool>("EDI", "SerpentHypnosis", true, "Play a script while the Black Serpent's hypnosis drags the player's camera onto itself, chosen by how close it is. Overrides the damage/heat filler for as long as the hypnosis loop runs; the grab that ends it plays Serpent_Loop as usual.");
		CfgSerpentHypnosisViewGrace = Config.Bind<float>("EDI", "SerpentHypnosisViewGrace", 1.5f, "Seconds the approach keeps playing - the row and its intensity both - after the serpent stops pulling the camera. Vanilla answers hypnosisInView per frame and it flickers, a step to the side or the serpent's own walk cycle, and dropping to the filler on the first false frame is what made a tier last 1.16 s in the 2026-08-24 run. A pull that stays lost still gives the device back; 0 disables the grace. Measured on unscaled time.");
		CfgSerpentHypnosisGallery = Config.Bind<string>("EDI", "SerpentHypnosisGallery", "Serpent_Hypnosis", "The row the serpent's approach loops while the mod scales the device's stroke range with the distance. It has to be authored at the LOUDEST the approach will ever be, because intensity only ever scales down from what the script asks for: this row peaks at 90, and playing it at 44% is the 40 units the old far tier played. It is anchored at position 0 at every seam, so scaling changes how far the device travels and not where it sits. Empty disables the approach without disabling anything else.");
		CfgMasterIntensity = Config.Bind<int>("EDI", "MasterIntensity", 100, "Scales every intensity the mod sends to Edi, as a percentage. 100 leaves the scripts as authored. Lower it if the device is too strong overall - this moves the top of the device's stroke range in place, without re-dispatching or retiming anything, so it softens a scene rather than slowing it down. It composes with RowIntensityScale and with whatever a scene is already asking for (the serpent's approach ramp, for instance), so turning this down never makes a lowered scene louder. Note that Edi only moves the range's top: Min stays where the device is configured, so what this scales is amplitude anchored at the bottom of the stroke.");
		CfgRowIntensityScale = Config.Bind<string>("EDI", "RowIntensityScale", "", "Per-row intensity, for the one or two scripts that are too strong rather than all of them: `row=percent;row=percent`, e.g. `imp_3=60;imp_3_Gallery=60`. Names are gallery rows as they appear in Definitions.csv, matched case-insensitively. A row with no entry plays at 100 and is affected only by MasterIntensity. Prefer this to editing a funscript: the script stays the measured one and only the device's range moves. If a whole device class is too fast rather than too strong, the answer is the variant folder instead (handy2, handy1) - that limits speed, which is a different complaint.");
		CfgSerpentHypnosisIntensityFar = Config.Bind<int>("EDI", "SerpentHypnosisIntensityFar", 44, "Device intensity (0-100) at the far edge of the approach band, where the serpent starts hypnotising. 44 of the row's 90-unit peak is 40 units of travel, which is what the old far tier played, so the band starts where the ladder did rather than somewhere new. Lower it to spend more of the band on the ramp.");
		CfgSerpentHypnosisIntensityNear = Config.Bind<int>("EDI", "SerpentHypnosisIntensityNear", 100, "Device intensity (0-100) at grabRange, where vanilla lands the grab and Serpent_Loop takes over. 100 is the row as authored.");
		CfgSerpentHypnosisIntensityStep = Config.Bind<int>("EDI", "SerpentHypnosisIntensityStep", 3, "Smallest intensity change worth a POST. Distance moves every frame and the device cannot feel a 1% change, so this is what stops a walk down the band from becoming sixty requests a second.");
		CfgSerpentHypnosisIntensityInterval = Config.Bind<float>("EDI", "SerpentHypnosisIntensityInterval", 0.15f, "Minimum seconds between intensity POSTs. Edi debounces its own range writes by 100 ms (DeviceBase.rangeTimer), so anything faster than that is discarded before it reaches the device anyway. Measured on unscaled time.");
		CfgEnableChaserStomp = Config.Bind<bool>("EDI", "ChaserStomp", true, "Play a real above-baseline row - Dragon_Stomp or Wendigo_Stomp - in place of the filler while a chaser boss (dragon or wendigo) is audible and within ChaserStompRange, phase-locked to its own footfall. Unlike a pure Intensity squeeze this changes which row is playing, because Edi's Intensity endpoint can only ever cap a row's travel, never raise it - so this is the only way the device gets louder than the ordinary filler as a chaser closes in. Stands down for the serpent's hypnosis and for a real scene (a grab, an interact). Off restores the plain filler everywhere and gives the intensity channel back.");
		CfgChaserStompRange = Config.Bind<float>("EDI", "ChaserStompRange", 25f, "Metres: the far edge of the band, where the stomp row and its intensity start. 0 means read the chaser's own looping AudioSource maxDistance instead, which is the honest figure but is only trustworthy if the prefab set it - the game adds that source at runtime where it is missing, and Unity's default is a non-spatial one that claims 500 m, which would put a whole floor at the far value. The log line prints both, so a run says which this should have been. The near edge is not configurable: it is the chaser's own grabRange, where its grab scene takes the device over.");
		CfgChaserStompIntensityFar = Config.Bind<int>("EDI", "ChaserStompIntensityFar", 50, "Device intensity (0-100), capping the stomp row's own travel, at the far edge of the band - the moment a chaser boss becomes part of this. Everything outside the band plays the plain filler at 100. Assigned directly, not ramped in - see ChaserStomp.cs for why a distance-driven value does not need one.");
		CfgChaserStompIntensityNear = Config.Bind<int>("EDI", "ChaserStompIntensityNear", 100, "Device intensity (0-100) at ChaserStompIntensityNearDistance and closer, all the way to the chaser's grabRange where its grab lands and plays its own scene. 100 is the stomp row as authored - its peak, unsqueezed - so an approach reads at full impact before the grab, not only at the instant of it.");
		CfgChaserStompIntensityNearDistance = Config.Bind<float>("EDI", "ChaserStompIntensityNearDistance", 6f, "Metres from the chaser at which intensity reaches ChaserStompIntensityNear and holds there for anything closer. Distinct from grabRange itself (clamped to be at least grabRange): without this, full impact was only ever reached the instant the grab was about to land, so most of an approach read closer to ChaserStompIntensityFar than to 100 - basically touching was the only way to feel the top of the band. A few metres out now reads as 'about to grab' instead.");
		CfgChaserStompIntensityStep = Config.Bind<int>("EDI", "ChaserStompIntensityStep", 3, "Smallest intensity change worth a POST. Distance moves every frame and the device cannot feel a 1% change. Arriving back at 100 is always sent whatever this says, because a range left at 97 is a range nothing would ever restore.");
		CfgChaserStompIntensityInterval = Config.Bind<float>("EDI", "ChaserStompIntensityInterval", 0.15f, "Minimum seconds between intensity POSTs. Edi debounces its own range writes by 100 ms (DeviceBase.rangeTimer), so anything faster than that is discarded before it reaches the device anyway. Measured on unscaled time.");
		CfgChaserStompGrace = Config.Bind<float>("EDI", "ChaserStompGrace", 2.0f, "Seconds the stomp row - and its intensity target - keep holding after the gate (audible and in range) goes false, before falling back to the filler at 100%. The gate is answered per frame off live game state and it can drop for reasons that have nothing to do with the approach ending: a step behind cover, the walk loop restarting, or a real grab attempt, which silences the gate for exactly `grabAttemptDuration` (1.5s, measured off the decompiled DragonEnemyAI/ProximityDragonEnemyAI) win or miss. This is set above that measured 1.5s with margin deliberately - equal to it would decide by coin flip, on every single grab attempt, whether the approach survives. 0 disables the grace. Measured on unscaled time.");
		CfgChaserStompDragonGallery = Config.Bind<string>("EDI", "ChaserStompDragonGallery", "Dragon_Stomp", "The row ChaserStomp plays for a dragon-family chaser. Which creature a chaser is comes off its GameObject's own name (there is no separate WendigoEnemyAI class - a wendigo is a reskinned dragon prefab), not its C# type. This row's declared length in Definitions.csv is read back as the beat period for phase-locking, so a retimed row retimes the lock with it.");
		CfgChaserStompWendigoGallery = Config.Bind<string>("EDI", "ChaserStompWendigoGallery", "Wendigo_Stomp", "The row ChaserStomp plays for a wendigo-family chaser. Which creature a chaser is comes off its GameObject's own name (there is no separate WendigoEnemyAI class - a wendigo is a reskinned dragon prefab), not its C# type. This row's declared length in Definitions.csv is read back as the beat period for phase-locking, so a retimed row retimes the lock with it.");
		CfgSerpentHypnosisIntensityRelease = Config.Bind<float>("EDI", "SerpentHypnosisIntensityRelease", 0.75f, "Seconds the device keeps the approach's reduced range after the ladder hands the row back, before it is restored to full. The row hand-back is instant because something has to be playing; the range is delayed because an approach that resumes inside this window should not have cost a full-amplitude stroke in between. The 2026-08-25 run has the case: the view grace expired and the approach resumed 91 ms later, and without this the device went to full range and back to 84% inside a tenth of a second. A scene change restores it immediately whatever this says. Measured on unscaled time.");
		CfgEnableAmbient = Config.Bind<bool>("Ambient", "Enabled", true, "Trigger EDI when the player can HEAR an ambient h-scene's looping audio.");
		CfgAmbientRange = Config.Bind<float>("Ambient", "FallbackRange", 8f, "Distance (m) used when an AudioSource has no 3D falloff (spatialBlend=0 or maxDistance unset).");
		CfgAmbientHearingMultiplier = Config.Bind<float>("Ambient", "HearingRangeMultiplier", 1f, "Multiplier on each AudioSource's own maxDistance. 1.0 = exactly when the game says you can hear it. Increase to extend reach (e.g. 1.5).");
		CfgAmbientPatterns = Config.Bind<string>("Ambient", "Patterns", "imp gangbang|imp_gangbang|imp gangbang sound|imp gangbang 1,nun chair fuck|nun chair anal|nun_chair,nun wall chain head|nun_wall_chain_head|nun wall chain,mimic wall fuck|mimic_wall_fuck|mimic wall,gargoyle ledge fuck|gargoyle ride wall ledge|gargoyle wall ride|gargoyle_ledge_fuck,gooper bed blowjob|gooper blowjob|gooper_bed_blowjob,plantasha blowjob|plantasha blowjob sound|plantasha_blowjob,zombie bench fuck|zombie fuck|zombie fuck sound|zombie blowjob hole scene|zombiebjpeep,wendigo hole|wendigo hole scene|wendigo sex,imp gangbang 2|imp_gangbang_2,dragon squat ride,wendigo squat ride,goonshroom gangbang,nun watersports,plant gangbang,serpent wall blowjob|Serpent wall blowjob", "Comma-separated in-world diorama ambient patterns (9 scenes). Each entry can contain pipe-separated aliases â€” ANY alias matching the AudioClip name fires this scene's EDI gallery. The FIRST alias is the canonical name used to build the gallery slug ('ambient_<first-alias-slugified>'). Longest alias wins on collisions. Peek-tab replays use separate peek_* scripts via the gallery menu hook, not this list.");
		CfgAmbientPrefix = Config.Bind<string>("Ambient", "GalleryPrefix", "ambient_", "Prefix prepended to the matched pattern.");
		CfgAmbientScanInterval = Config.Bind<float>("Ambient", "ScanIntervalSeconds", 3f, "How often to rescan the scene for matching looping audio sources.");
		CfgAmbientDiag = Config.Bind<bool>("Ambient", "DiagnosticMode", false, "When true, every 2s logs ALL playing AudioSources + SpriteRenderers within DiagnosticRange of the player. Walk up to an unrecognized scene, copy a unique clip name fragment into Patterns above.");
		CfgAmbientDiagRange = Config.Bind<float>("Ambient", "DiagnosticRange", 15f, "Distance (m) used by DiagnosticMode dumps.");
		CfgEnableImp = Config.Bind<bool>("Imp", "Enabled", true, "Trigger EDI when imps are clinging.");
		CfgImpPrefix = Config.Bind<string>("Imp", "GalleryPrefix", "imp_", "Cling gallery = prefix + grapple count (imp_1 â€¦ imp_3). Trio grab uses imp_grab_* via GalleryAliases.");
		CfgGrapplePrefixMap = Config.Bind<string>("Imp", "GrapplePrefixMap", "imp=imp_;goonshroom=goonshroom_", "Semicolon-separated `enemy_key=gallery_prefix` for cling scenes, which are named prefix + the number attached. Game 0.3.1 added the goonshroom as a second grappler; with the single GalleryPrefix above, a goonshroom grapple played imp_1/2/3 - a wrong scene, which nothing announces. An enemy key that is not listed here dispatches nothing and logs it, rather than borrowing another creature's scripts. Leave a family out to silence it deliberately.");
		CfgGrappleTierDropHoldSeconds = Config.Bind<float>("Imp", "GrappleTierDropHoldSeconds", 0.35f, "Seconds a lower cling count has to hold before the device drops to its script. Shaking one grappler off used to leave the device on the higher tier until the whole grapple ended, because the dispatch only ever escalated. The hold is here because the count is read every frame and dips for a frame or two around a shake-off and around a replacement arriving; without it the device would step down and straight back up. 0 drops on the first frame the count is lower; a large value restores the old escalate-only behaviour.");
		CfgImpGrappleLoopDelaySeconds = Config.Bind<float>("Imp", "GrappleLoopDelaySeconds", 0f, "Seconds after an imp cling tier starts/escalates before switching to imp_grab_loop while the grapple remains active. 0 (default) disables the switch, which is what vanilla does: GrappleScreenobject holds oneEnemyAnimation/twoEnemyAnimation/threeEnemyAnimation and a matching audio loop for as long as that many imps are attached, and has no generic grab loop to fall back to. imp_1/2/3 already loop, so switching only replaced the tier script with an unrelated one 1.2s in.");
		CfgImpGrappleReinforcement = Config.Bind<bool>("Imp", "GrappleReinforcement", true, "While 1-2 imps cling during grapple (not H-scene), spawn another imp every GrappleReinforcementInterval seconds. Stops at 3 imps.");
		CfgImpGrappleReinforcementInterval = Config.Bind<float>("Imp", "GrappleReinforcementInterval", 5f, "Seconds between extra imp spawns during imp grapple (only with 1 or 2 imps attached).");
		CfgImpGrappleReinforcementRamp = Config.Bind<float>("Imp", "GrappleReinforcementRamp", 1f, "Multiplier applied to GrappleReinforcementInterval for each imp already clinging past the first. 1 (default) is the flat drumbeat imps have always had: 5s, then another 5s. Below 1 the pile closes in faster as it grows; above 1 it eases off. Clamped to 0.1-4, and the resulting wait never drops below 0.5s.");
		CfgImpGrappleReinforcementCeiling = Config.Bind<int>("Imp", "GrappleReinforcementCeiling", 3, "Stop calling imps in at this many attached. Vanilla GrappleScreenobject.maxGrappleCount (3 on the shipped prefab) is a hard cap above this - StartGrapple refuses past it, the grapple UI animator has no fourth state and the cling scenes are named prefix + count - so a higher value only ever lowers to it.");
		CfgGoonShroomGrappleReinforcement = Config.Bind<bool>("GoonShroom", "GrappleReinforcement", true, "While goonshrooms cling during grapple (not H-scene), call more of them in. Game 0.3.1 made the goonshroom the second grappler; reinforcement used to be imp-only, so a goonshroom grapple stayed at whatever walked into you.");
		CfgGoonShroomGrappleReinforcementInterval = Config.Bind<float>("GoonShroom", "GrappleReinforcementInterval", 8f, "Seconds before the second goonshroom arrives. Deliberately slower than the imps' 5s: with GrappleReinforcementRamp below 1 the goonshroom escalation is slow to start and then closes fast, rather than being an imp swarm with a different prefab.");
		CfgGoonShroomGrappleReinforcementRamp = Config.Bind<float>("GoonShroom", "GrappleReinforcementRamp", 0.5f, "Multiplier applied to GrappleReinforcementInterval for each goonshroom already clinging past the first. 0.5 (default) halves the wait each time: 8s to the second, 4s to the third. Set to 1 for the imps' flat pacing. Clamped to 0.1-4, and the resulting wait never drops below 0.5s.");
		CfgGoonShroomGrappleReinforcementCeiling = Config.Bind<int>("GoonShroom", "GrappleReinforcementCeiling", 3, "Stop calling goonshrooms in at this many attached. Vanilla maxGrappleCount caps this the same way it caps the imps' - see Imp/GrappleReinforcementCeiling. Lower it to 2 to leave the third slot to whatever walks in.");
		CfgGrappleDeathSequence = Config.Bind<bool>("Imp", "GrappleDeathSequence", true, "Dying while something is grappling you plays out as that creature's trio scene instead of a deferred game over: health stays at 0, you can no longer shake the grapplers off, and more of them are called in until the scene takes over. The game over is presented when that scene ends. Applies to every clinging family (imps and goonshrooms both carry a trio grab scene); the setting stays under [Imp] so an existing config still answers to it.");
		CfgGrappleDeathImpInterval = Config.Bind<float>("Imp", "GrappleDeathImpInterval", 1f, "Seconds between imp spawns during the grapple death sequence. Overrides GrappleReinforcementInterval, and applies even when GrappleReinforcement is off.");
		CfgGrappleDeathText = Config.Bind<string>("Imp", "GrappleDeathText", "Overwhelmed!", "Grapple UI text during the grapple death sequence, replacing \"Shake mouse to escape!\" while the struggle is disabled. Empty leaves the vanilla text.");
		CfgEnableInteractive = Config.Bind<bool>("Interactive", "Enabled", true, "Hook interactive H-scene triggers (CameraSwapTrigger: Baphomet glory hole, Gravy hole, etc).");
		CfgEnableFreecam = Config.Bind<bool>("Tools", "EnableFreecam", false, "Enable the noclip + teleport hotkeys for finding and recording ambient scenes: ToggleFreecamKey (F1) flies the camera, and the heat keys below add and remove horniness. Off by default because these fire on keys an unsuspecting player presses; turn it on here and they work immediately, no restart.");
		CfgKeyFreecam = Config.Bind<KeyboardShortcut>("Tools", "ToggleFreecamKey", new KeyboardShortcut((KeyCode)282, Array.Empty<KeyCode>()), "Toggle noclip flight (Space=up, Ctrl/Shift=down, WASD=move). F1 again to exit â€” works even while holding Ctrl/Shift/Alt for movement.");
		CfgEnableDebugEnemySpawn = Config.Bind<bool>("Tools", "EnableDebugEnemySpawn", false, "Allow the hotkeys below to spawn enemies in front of the player - the digit row, M for the mimic, and Period for a random one - for filling the gallery or reaching a scene on purpose. Off by default so that a digit pressed in ordinary play does nothing; turn it on here and they work immediately, no restart.");
		CfgKeySpawnZombie = Config.Bind<KeyboardShortcut>("Tools", "SpawnZombieKey", new KeyboardShortcut((KeyCode)49, Array.Empty<KeyCode>()), "Spawn a zombie in front of the player (debug). Cycles through all matching prefab variants (e.g. normal + ALT1).");
		CfgKeySpawnPlantasha = Config.Bind<KeyboardShortcut>("Tools", "SpawnPlantashaKey", new KeyboardShortcut((KeyCode)50, Array.Empty<KeyCode>()), "Spawn a plantasha in front of the player (debug). Cycles prefab variants when multiple match.");
		CfgKeySpawnGargoyle = Config.Bind<KeyboardShortcut>("Tools", "SpawnGargoyleKey", new KeyboardShortcut((KeyCode)51, Array.Empty<KeyCode>()), "Spawn a gargoyle in front of the player (debug).");
		CfgKeySpawnGooper = Config.Bind<KeyboardShortcut>("Tools", "SpawnGooperKey", new KeyboardShortcut((KeyCode)52, Array.Empty<KeyCode>()), "Spawn a gooper in front of the player (debug).");
		CfgKeySpawnMimic = Config.Bind<KeyboardShortcut>("Tools", "SpawnMimicKey", new KeyboardShortcut((KeyCode)109, Array.Empty<KeyCode>()), "Spawn a mimic in front of the player (debug). On M rather than a digit: the digit row is the ten walking enemies in encounter order, and the mimic is furniture that waits for you rather than something that walks up, so it reads better off the row than squeezed into it.");
		CfgKeySpawnNun = Config.Bind<KeyboardShortcut>("Tools", "SpawnNunKey", new KeyboardShortcut((KeyCode)53, Array.Empty<KeyCode>()), "Spawn a nun in front of the player (debug). Cycles hood / alt prefab variants.");
		CfgKeySpawnImp = Config.Bind<KeyboardShortcut>("Tools", "SpawnImpKey", new KeyboardShortcut((KeyCode)55, Array.Empty<KeyCode>()), "Spawn an imp in front of the player (debug).");
		CfgKeySpawnChaserBoss = Config.Bind<KeyboardShortcut>("Tools", "SpawnChaserBossKey", new KeyboardShortcut((KeyCode)48, Array.Empty<KeyCode>()), "Spawn a chaser boss in front of the player (debug). The dragon and the wendigo share this key and cycle like any other prefab variant - press again for the other one; the log names which. They are one key because they are one mechanic: both are chaser bosses, both are refused by the spawn shuffle, and both take EndGrabDelaySecondsChaserBoss and a full heat lock-out rather than the ordinary grab treatment.");
		CfgKeySpawnBlindedBeast = Config.Bind<KeyboardShortcut>("Tools", "SpawnBlindedBeastKey", new KeyboardShortcut((KeyCode)57, Array.Empty<KeyCode>()), "Spawn a Blinded Beast in front of the player (debug). No spawner's own enemyData table lists the Blinded Beast, so an ordinary room can never roll one; only a Floor 2 arena spawn group can. Its EnemyData asset is not loaded until something asks for it, which is why the resolver loads the Resources folder before searching.");
		CfgKeySpawnGoonShroom = Config.Bind<KeyboardShortcut>("Tools", "SpawnGoonShroomKey", new KeyboardShortcut((KeyCode)56, Array.Empty<KeyCode>()), "Spawn a goonshroom in front of the player (debug). The prefab hint already existed for GrappleReinforcement; this binds a key to it so a cling can be tested without finding a nest. Sits next to the imp as the other half of the clinging family.");
		CfgKeySpawnSerpent = Config.Bind<KeyboardShortcut>("Tools", "SpawnSerpentKey", new KeyboardShortcut((KeyCode)54, Array.Empty<KeyCode>()), "Spawn a Black Serpent in front of the player (debug). Key 6, between the ordinary enemies and the two grapplers, because that is where it sits in the row's own logic: the digit row runs 1-5 ordinary, 6 serpent, 7-8 clinging, 9-0 miniboss and boss. It was the only walkable enemy with no key at all - the serpent is in eleven vanilla spawner tables and the Gooper&Serpent arena group, so it can be met in play, but nothing could ask for one, which left SerpentHypnosis the one system in the mod that could not be tested on demand.");
		CfgKeySpawnShufflePool = Config.Bind<KeyboardShortcut>("Tools", "SpawnShufflePoolKey", new KeyboardShortcut((KeyCode)46, Array.Empty<KeyCode>()), "Spawn a random enemy from the shuffle pool in front of the player (debug). Logs pool contents and picked type.");
		CfgSpawnEnemyDistance = Config.Bind<float>("Tools", "SpawnEnemyDistance", 10f, "Distance (m) in front of the player for debug enemy spawns.");
		CfgSpawnZombieNameHint = Config.Bind<string>("Tools", "SpawnZombieNameHint", "zombie", "Prefab name substring for debug zombie spawn. Pipe-separated hints match any variant (e.g. zombie|alt). Each press cycles through all matching prefabs.");
		CfgSpawnPlantashaNameHint = Config.Bind<string>("Tools", "SpawnPlantashaNameHint", "plantasha", "Prefab name substring for debug plantasha spawn.");
		CfgSpawnGargoyleNameHint = Config.Bind<string>("Tools", "SpawnGargoyleNameHint", "Gargoyle|Gargoyle Alt|gargoyle", "Gargoyle enemy prefabs (Gargoyle / Gargoyle Alt). Same hint used for the debug key and the shuffle pool.");
		CfgSpawnGooperNameHint = Config.Bind<string>("Tools", "SpawnGooperNameHint", "gooper", "Prefab name substring for debug gooper spawn.");
		CfgSpawnMimicNameHint = Config.Bind<string>("Tools", "SpawnMimicNameHint", "mimic", "Prefab name substring for the debug mimic spawn. MimicEnemy IS the chest mimic: it disguises itself with chestSprite, draws an outline while you are in interactionRange, and springs its grab when you press Interact. The two prefabs, Mimic and Weapons Mimic, are the common-chest and weapon-chest disguises and cycle on the one key. The `Mimic wall fuck` objects are the D8 diorama, a static scene with no MimicEnemy on it.");
		CfgSpawnNunNameHint = Config.Bind<string>("Tools", "SpawnNunNameHint", "hood|nun", "Prefab name substring for debug nun spawn. Pipe-separated hints match normal + alt variants.");
		CfgSpawnChaserBossNameHint = Config.Bind<string>("Tools", "SpawnChaserBossNameHint", "dragon|wendigo", "Prefab name substrings for the debug chaser-boss spawn. Pipe-separated, so both bosses answer to the one key and each press cycles to the next matching prefab.");
		CfgSpawnSerpentNameHint = Config.Bind<string>("Tools", "SpawnSerpentNameHint", "black serpent|blackserpent|serpent", "Black Serpent enemy prefab. Matched before the bare 'serpent' so a peek-scene or diorama prop named for the serpent cannot win the lookup ahead of the enemy.");
		CfgSpawnImpNameHint = Config.Bind<string>("Tools", "SpawnImpNameHint", "imp", "Prefab name substring for debug imp spawn.");
		CfgSpawnGoonShroomNameHint = Config.Bind<string>("Tools", "SpawnGoonShroomNameHint", "goonshroom", "Prefab name substring for goonshroom spawns. Used by GoonShroom/GrappleReinforcement, which is what spawns them in play, and by SpawnGoonShroomKey.");
		CfgSpawnBlindedBeastNameHint = Config.Bind<string>("Tools", "SpawnBlindedBeastNameHint", "blindedbeast|blinded beast|blinded", "Prefab name substring for debug Blinded Beast spawn. Pipe-separated hints match any variant. The miniboss transforms into its second stage itself, so one prefab covers both stages and both grab screens.");
		DebugEnemySpawn.Configure(new DebugEnemySpawn.SpawnBinding[11]
		{
			new DebugEnemySpawn.SpawnBinding
			{
				Key = CfgKeySpawnZombie,
				Hint = CfgSpawnZombieNameHint,
				Label = "zombie"
			},
			new DebugEnemySpawn.SpawnBinding
			{
				Key = CfgKeySpawnPlantasha,
				Hint = CfgSpawnPlantashaNameHint,
				Label = "plantasha"
			},
			new DebugEnemySpawn.SpawnBinding
			{
				Key = CfgKeySpawnGargoyle,
				Hint = CfgSpawnGargoyleNameHint,
				Label = "gargoyle"
			},
			new DebugEnemySpawn.SpawnBinding
			{
				Key = CfgKeySpawnGooper,
				Hint = CfgSpawnGooperNameHint,
				Label = "gooper"
			},
			new DebugEnemySpawn.SpawnBinding
			{
				Key = CfgKeySpawnMimic,
				Hint = CfgSpawnMimicNameHint,
				Label = "mimic"
			},
			new DebugEnemySpawn.SpawnBinding
			{
				Key = CfgKeySpawnNun,
				Hint = CfgSpawnNunNameHint,
				Label = "nun"
			},
			new DebugEnemySpawn.SpawnBinding
			{
				Key = CfgKeySpawnImp,
				Hint = CfgSpawnImpNameHint,
				Label = "imp"
			},
			new DebugEnemySpawn.SpawnBinding
			{
				Key = CfgKeySpawnChaserBoss,
				Hint = CfgSpawnChaserBossNameHint,
				Label = "chaser_boss"
			},
			new DebugEnemySpawn.SpawnBinding
			{
				Key = CfgKeySpawnBlindedBeast,
				Hint = CfgSpawnBlindedBeastNameHint,
				Label = "blinded_beast"
			},
			new DebugEnemySpawn.SpawnBinding
			{
				Key = CfgKeySpawnGoonShroom,
				Hint = CfgSpawnGoonShroomNameHint,
				Label = "goonshroom"
			},
			new DebugEnemySpawn.SpawnBinding
			{
				Key = CfgKeySpawnSerpent,
				Hint = CfgSpawnSerpentNameHint,
				Label = "serpent"
			}
		});
		CfgHandyIntensitySource = Config.Bind<HandyIntensitySource>("EDI", "HandyIntensitySource", HandyIntensitySource.Automatic, "Which figure FillerCumGalleryMap buckets into filler_cum_25/50/75. Automatic uses heat under Vanilla and GodMode and the stronger of heat or lock pressure under PressureAndRelease. Heat is the game's heat bar only; ReleasePressure is lock progress only; Highest is whichever is currently stronger. Heat alone reads poorly once locks hold the heat floor up, because it then reports how many locks are held rather than what is happening.");
		CfgPauseOnFocusLoss = Config.Bind<bool>("EDI", "PauseOnFocusLoss", true, "Send Edi/Pause when the game loses focus (alt-tab / clicked outside / minimized) and Edi/Resume on regain.");
		CfgGameplayProfile = Config.Bind<GameplayProfile>("Gameplay", "Profile", GameplayProfile.Vanilla, "Select one gameplay profile. Fresh installations start in Vanilla. PressureAndRelease uses pressure/heat rules; GodMode refills HP and blocks damage; Overkeks is the separate community overhaul; Custom uses individual gameplay settings. Existing saved profile selections are retained. Switch profiles in the mod manager.");
		CfgEnableGameplayTweaks = Config.Bind<bool>("Gameplay", "Enabled", true, "Player survivability tweaks (god mode, enemy heat gain rate, spawn count multiplier).");
		CfgGodMode = Config.Bind<bool>("Gameplay", "GodMode", true, "No damage, no death, no grab heat/dragon kill, no game over. Blocked hits can still add heat (see ConvertBlockedDamageToHeat).");
		CfgEnableHeatLocks = Config.Bind<bool>("Gameplay", "EnableHeatLocks", true, "Replace god-mode survivability with heat locks. Locks clamp minimum heat until released by a one-time keyhole or ambient scene. See HeatLockScaleSource for how many locks there are.");
		CfgDisableCumDamage = Config.Bind<bool>("Gameplay", "DisableCumDamage", true, "Cumming during a grab or grapple scene no longer costs HP (vanilla GrabScreen deals maxHeatDamage plus a percentage of max HP, dropping invulnerability to do it). The animation, heat block and cooldown are unchanged.");
		CfgClearOverheatAtLockFloor = Config.Bind<bool>("Gameplay", "ClearOverheatAtLockFloor", true, "Overheating still disables attacks and dashes, but clears once heat cools back to the current lock floor instead of vanilla's exactly-0, which a lock floor makes unreachable. With no locks held the floor is 0, i.e. vanilla behaviour.");
		CfgHeatLockScaleSource = Config.Bind<string>("Gameplay", "HeatLockScaleSource", "Heat", "Which stat sets the horny limit (total number of locks). Heat = the cum meter / heat capacity (default). Health = base class max HP, the pre-2.0.9 behaviour.");
		CfgHeatPotionLockRemoval = Config.Bind<string>("Gameplay", "HeatPotionLockRemoval", "small heat potion=2;medium heat potion=5;large heat potion=10", "Horny locks each heat potion removes, along with the heat those locks were holding, instead of vanilla's flat heat reduction. Matched case-insensitively against the item name, longest key wins. Empty restores the vanilla flat cooling. Only applies while EnableHeatLocks is on.");
		CfgAmbientReleaseLookSeconds = Config.Bind<float>("Gameplay", "AmbientReleaseLookSeconds", 3f, "Seconds the player must keep a diorama in view, with line of sight, before its one-time horny release fires. A bar under the Horny readout shows the progress. 0 disables the requirement, restoring the old fire-on-earshot behaviour.");
		CfgAmbientReleaseDecayMultiplier = Config.Bind<float>("Gameplay", "AmbientReleaseDecayMultiplier", 1f, "How fast the diorama look meter drains while looking away, relative to how fast it fills. 1 = same speed, 0 = never drains, 2 = drains twice as fast. It drains rather than resetting, so a glance away costs progress instead of the attempt.");
		CfgAmbientPlaybackRequiresPresence = Config.Bind<bool>("Gameplay", "AmbientPlaybackRequiresPresence", true, "Diorama scripts only play while the player is at the diorama - inside its gallery-unlock box, widened by AmbientPlaybackTriggerPadding - instead of merely within earshot of it. false restores the earshot-only behaviour, where a diorama heard through a wall drives the device.");
		CfgAmbientPlaybackLingerSeconds = Config.Bind<float>("Gameplay", "AmbientPlaybackLingerSeconds", 0.25f, "Seconds a diorama keeps playing after the player steps out of its unlock box. Debounces the box boundary, where the overlap test can flip frame to frame and bounce the device to filler and back. Only used when AmbientPlaybackRequiresPresence is on.");
		CfgAmbientReleaseUseUnlockTriggers = Config.Bind<bool>("Gameplay", "AmbientReleaseUseUnlockTriggers", true, "Use the game's own GalleryUnlockTrigger box - the volume that unlocks the diorama's gallery entry when you walk into it - to decide the player is at a diorama, instead of raycasting for line of sight. Hand-placed per diorama by the level designer, so it needs no camera or ray. false falls back to the raycast test.");
		CfgAmbientReleaseTriggerPadding = Config.Bind<float>("Gameplay", "AmbientReleaseTriggerPadding", 1.5f, "Metres of slack around the unlock box for the look meter, since some of the boxes are only a metre across. Raise it if the meter will not start while you are plainly standing at the diorama; the log line 'Xm outside box' says how far short you were.");
		CfgAmbientPlaybackTriggerPadding = Config.Bind<float>("Gameplay", "AmbientPlaybackTriggerPadding", 4f, "Metres of slack around the unlock box for diorama script playback - deliberately looser than the release, so the script does not cut out as you shift your feet. Only used when AmbientPlaybackRequiresPresence is on.");
		CfgAmbientReleaseLookAngle = Config.Bind<float>("Gameplay", "AmbientReleaseLookAngle", 75f, "How far off-centre the diorama may be, in degrees, and still count as being looked at while inside its unlock box. 180 accepts any direction. Ignored within 1.5m, where the direction is meaningless.");
		CfgReviveKeepsSceneGallery = Config.Bind<bool>("Gameplay", "ReviveKeepsSceneGallery", true, "When the game revives the player into a grab scene - which is how dying in a grab plays out as that scene - keep playing the scene's script instead of dropping to damage filler. Vanilla Revive() fires a couple of milliseconds after the scene's gallery is dispatched, so without this the filler stomps it and the whole death scene runs on filler_damage_*. false restores that.");
		CfgAmbientReleaseClearsAllLocks = Config.Bind<bool>("Gameplay", "AmbientReleaseClearsAllLocks", true, "A diorama's one-time release clears every horny lock, the same payout a peephole gives, instead of removing a single one. false restores the single-lock behaviour.");
		CfgHeatLockOncePerGrabSource = Config.Bind<bool>("Gameplay", "HeatLockOncePerGrabSource", false, "true means a grab only adds a horny lock the first time that enemy catches you AT THAT SPOT - the pre-2.0.10 behaviour, and a bug. The guard keyed on the source's position rounded to 0.1m, so a walking enemy earned a lock every time it caught you somewhere new while a stationary one (a mimic chest) earned exactly one per run, and a walking enemy that caught you twice in the same doorway was refused the second. Left as false: every grab locks. Peepholes and dioramas keep their own one-per-source guard, which is what stops those being farmed by walking back and forth.");
		CfgFullLockHoldsCum = Config.Bind<bool>("Gameplay", "FullLockHoldsCum", true, "At full horny lock, a grab screen keeps replaying its cum animation instead of dropping to the grabbed loop between cums. The lock floor is one unit below max heat there, so the bar refills instantly and vanilla would otherwise flick to the loop for a frame or two several times a second - visibly stuttery, and it makes the device jump between two scripts. false restores the flicker.");
		CfgShowHeatBarDuringGrab = Config.Bind<bool>("Gameplay", "ShowHeatBarDuringGrab", false, "Keep the player heat bar on screen during grab scenes. Hidden by default because it clutters the scene, but it is the only live read of heat while grabbed - useful for checking whether the cum cooldown is moving heat or the horny lock floor is pinning it at the top.");
		CfgPostCumGraceSeconds = Config.Bind<float>("Gameplay", "PostCumGraceSeconds", 2f, "Seconds of grabbed loop guaranteed after a cum ends, before heat starts building again. Below full lock the lock floor can sit one or two units under max, so vanilla refills the bar almost instantly and the next cum triggers before the loop has played - the screen and the device flick between two scenes. This holds the grab screen's own heatIncreaseBlocked for a moment instead. 0 disables it. Does nothing at full lock, where FullLockHoldsCum keeps the cum on screen instead.");

		CfgCumCooldownEndsAtLockFloor = Config.Bind<bool>("Gameplay", "CumCooldownEndsAtLockFloor", true, "A cum in a grab scene ends once heat has cooled to the horny lock floor, instead of vanilla's exactly-0. Vanilla gates the end of the cum animation on heat reaching 0 (HandleHeatBuildup sets heatFullyCooled, which OnMaxHeatAnimationComplete requires), and a lock floor makes 0 unreachable - so without this the cum animation repeats for the whole grab. Same fix as ClearOverheatAtLockFloor.");
		CfgAmbientNearbyIcon = Config.Bind<string>("Gameplay", "AmbientNearbyIcon", "♦", "Marker shown under the Horny readout when an unspent diorama release is within reach but not being looked at. Drawn in Unity's builtin Arial, so stick to glyphs that font has - emoji will not render. Empty shows nothing.");
		CfgKeyholeReleaseWatchSeconds = Config.Bind<float>("Gameplay", "KeyholeReleaseWatchSeconds", 10f, "Seconds a peephole scene must be watched before its one-time horny release pays out. A fill bar under the Horny readout shows the progress, and the locks clear the moment it completes. Leaving early costs the progress but not the release - peeking again restarts it. 0 pays out the instant you peek, with no bar.");
		CfgAmbientPeekHoleRadius = Config.Bind<float>("Gameplay", "AmbientPeekHoleRadius", 1.5f, "Metres around a looping audio source to look for a peephole trigger. Sources with one that close are treated as peek scenes, not dioramas: no proximity playback and no look-timer release, since peeks are driven by interacting with the hole.");
		CfgHeatLockUnitsPerLock = Config.Bind<int>("Gameplay", "HeatLockUnitsPerLock", 20, "Points of the scaling stat per lock; the horny limit is that stat divided by this, rounded up. Higher means fewer locks to fill. At the default 20, and with ClassHeatMultipliers doubling capacity, the classes get: Knight 5, Rogue 8, Dent Head 10, Mage 13, Ranger 13. At 10 (the pre-2.0.11 value) those were 10/15/20/25/25, which is a lot of locks on the high-capacity classes. Never goes below 1 lock however high this is set.");
		CfgHeatLockAutoHealRate = Config.Bind<float>("Gameplay", "HeatLockAutoHealRate", 1f, "Auto-heal in HUNDREDTHS of HP per second at 0 heat, while HeatLocks are enabled - ApplyHeatScaledAutoHeal multiplies this by 0.01, so 120 is 1.2 HP/s and the coded default of 1 is 0.01 HP/s (one HP per 100 seconds, effectively off). Healing scales down quadratically with heat and stops at max heat. Releases ship 120 via release.py SHIPPED; the coded default is left at 1 only because changing it would silently alter every existing user config on next launch.");
		CfgKeyRemoveHeat = Config.Bind<KeyboardShortcut>("Gameplay", "RemoveHeatKey", new KeyboardShortcut((KeyCode)269, Array.Empty<KeyCode>()), "Remove 1 heat. Heat cannot go below 0.");
		CfgKeyAddHeat = Config.Bind<KeyboardShortcut>("Gameplay", "AddHeatKey", new KeyboardShortcut((KeyCode)268, Array.Empty<KeyCode>()), "Add 1 heat.");
		CfgKeepEnemiesAfterGrab = Config.Bind<bool>("Gameplay", "KeepEnemiesAfterGrab", true, "Do not despawn/kill the enemy when a grab (H-scene) starts or ends. Only applies to enemies the game still destroys: 0.3.1 prefabs with hideInsteadOfDestroyOnGrab are handled by the game itself and this setting does not touch them.");
		CfgMimicGrabRespectsImmunity = Config.Bind<bool>("Gameplay", "MimicGrabRespectsGrabImmunity", true, "Refuse a mimic interaction while the player is in post-grab immunity, instead of letting it burn the mimic's one-shot flag on a grab the game will decline. MimicEnemy.TriggerMimicGrab checks only IsGrabbed, while GrabScreen.StartGrab also checks CanBeGrabbed - so interacting within ~1s of escaping a scene used to leave the chest standing and permanently uninteractable. false restores that.");
		CfgMimicStrandedRearmSeconds = Config.Bind<float>("Gameplay", "MimicStrandedRearmSeconds", 3f, "Seconds a mimic may sit with its one-shot flag set while no scene is running before it is re-armed anyway. Backstop for any route that burns the flag without producing a scene; the normal scene-end re-arm is much faster, so this only rescues genuinely stuck chests. 0 disables it. Needs KeepEnemiesAfterGrab.");
		CfgMimicReactivateInstantly = Config.Bind<bool>("Gameplay", "MimicReactivateInstantly", true, "Re-arm a mimic the moment its scene ends, skipping EnemyReactivationDelaySeconds. That delay exists to stop an enemy resuming its AI and instantly re-grabbing you; a mimic has no AI and interacting with it is voluntary, so it only made a chest you walked back to sit inert. false puts mimics back on the shared enemy delay.");
		CfgAiUnstickSeconds = Config.Bind<float>("Gameplay", "AiUnstickSeconds", 0.5f, "Seconds an enemy may sit in a coroutine-driven state with no coroutine running before it is returned to Idle. SpinningEnemyAI (Grabbing/Shooting) and ChargingEnemyAI (PreparingCharge/Charging) have no case for those states in UpdateStateMachine, so recovery depends entirely on the sequence coroutine finishing - and the transition sets the state before deciding whether to start one. The other four AI classes handle every state they can enter and are not patched. 0 disables the watchdog.");
		CfgEnemyGrabRespectsImmunity = Config.Bind<bool>("Gameplay", "EnemyGrabRespectsGrabImmunity", true, "Stop the DRAGONS committing to a grab the game is about to refuse. GrabScreen.StartGrab declines while the player is in post-grab immunity or already grabbed. EnemyAI, SpinningEnemyAI and ProjectileEnemyAI all check for that and cancel the swing themselves, so they are left alone; DragonEnemyAI and ProximityDragonEnemyAI do not - they burn hasTriggeredDragonGrab, stop their coroutine and call RemoveEnemyAfterGrab anyway, removing the dragon from the level with no scene played. false restores vanilla.");
		CfgDebugHotkeysIgnoreHeldKeys = Config.Bind<bool>("Tools", "DebugHotkeysIgnoreHeldKeys", true, "Let the debug hotkeys (spawns, heat, dumps) fire while another key is held. BepInEx's KeyboardShortcut.IsDown additionally requires that NO other key is down, so a spawn key does nothing while you are walking - which is most of the time you want it. Explicitly configured modifiers are still required. false restores BepInEx behaviour.");
		CfgKeepEnemiesAfterGrabGraceSeconds = Config.Bind<float>("Gameplay", "KeepEnemiesAfterGrabGraceSeconds", 5f, "Seconds after grab end to keep blocking destroy/death on that enemy.");
		CfgGrabReviveHealth = Config.Bind<string>("Gameplay", "GrabReviveHealth", "Snapshot", "Health an enemy killed during an H-scene comes back with. Snapshot = what it had when the scene started (default). A number = that percent of max, e.g. 50 for the pre-2.0.9 behaviour. 0 = never revive, leaving it down but still present. Vanilla destroys the enemy outright after a grab, so for that set KeepEnemiesAfterGrab=false instead.");
		CfgEnemyReactivationDelaySeconds = Config.Bind<float>("Gameplay", "EnemyReactivationDelaySeconds", 5f, "Seconds after a scene ends before hidden/released enemies regain AI and can act again. They become visible immediately when the scene ends. A player weapon swing (animation/PerformAttack) or weapon heat gain wakes waiting enemies immediately.");
		CfgEnemyInactiveAutofix = Config.Bind<bool>("Gameplay", "EnemyInactiveAutofix", true, "Every EnemyInactiveAutofixInterval seconds, reactivate visible-but-stuck enemies near the player. Skips enemies still in the post-scene reactivation delay.");
		CfgEnemyInactiveAutofixInterval = Config.Bind<float>("Gameplay", "EnemyInactiveAutofixInterval", 20f, "Seconds between inactive-enemy autofix scans.");
		CfgEnemyInactiveAutofixRadius = Config.Bind<float>("Gameplay", "EnemyInactiveAutofixRadius", 40f, "Radius (m) around the player for inactive-enemy autofix scans.");
		CfgPlayerMaxHealth = Config.Bind<int>("Gameplay", "PlayerMaxHealth", 999999, "Shown max HP when GodMode is on (bar stays full while invulnerable).");
		CfgEnemyHeatGainRate = Config.Bind<float>("Gameplay", "EnemyHeatGainRate", 0.2f, "Multiplier for heat gained during enemy grapple and grab scenes (0.2 = 20% of vanilla rate). Weapon heat is unchanged.");
		CfgClassHeatMultipliers = Config.Bind<string>("Gameplay", "ClassHeatMultipliers", "Knight=2;Rogue=2;Mage=2;Ranger=2;Dent Head=2", "Per-class max-heat multipliers after class stats apply. Exact class name = factor (e.g. Mage=3;Knight=2). Unlisted classes use 1 (vanilla). Shown in class selection UI.");
		CfgConvertBlockedDamageToHeat = Config.Bind<bool>("Gameplay", "ConvertBlockedDamageToHeat", true, "With GodMode, enemy hits that would deal damage add heat instead (same amount as the blocked damage).");
		CfgSpawnCountMultiplier = Config.Bind<float>("Gameplay", "SpawnCountMultiplier", 1f, "Enemies per spawn wave at ZERO horny locks, as a multiple of what the room spawns in vanilla. 1 (default) is vanilla. SpawnCountMultiplierAtFullLock is the same figure at full lock and the game interpolates between the two on lock progress, so this is the floor of the ramp rather than a flat multiplier - it was 2 until it was noticed that an unlocked room was spawning double before the run had started. Also relaxes min distance to player, which is divided by whatever the ramp currently reads, so more spawn points qualify; at 1 that leaves vanilla's distance alone.");
		CfgSpawnCountMultiplierAtFullLock = Config.Bind<float>("Gameplay", "SpawnCountMultiplierAtFullLock", 2.5f, "Enemies per spawn wave at FULL horny lock, as a multiple of what the room spawns in vanilla. SpawnCountMultiplier is the same figure at zero locks, and the game interpolates between the two on lock progress - so with the defaults a room goes from vanilla to 2.5x as the locks fill. How much each lock adds therefore depends on how many locks the character has (locks/total, and the total scales with max heat): a character with 13 locks steps up in thirteenths, one with 5 in fifths. Clamped to never fall below SpawnCountMultiplier.");
		CfgSpawnScatterRadius = Config.Bind<float>("Gameplay", "SpawnScatterRadius", 2f, "Metres to scatter the extra enemies added by SpawnCountMultiplier around their spawn point, so they do not spawn inside each other. Each candidate is floor-snapped and rejected if it is occupied or behind a wall; if none is found the original point is reused.");
		CfgEnemySpawnShuffle = Config.Bind<bool>("Gameplay", "EnemySpawnShuffle", true, "Master switch for the mod changing how normal spawners choose an enemy. What it does is set by EnemySpawnMode; false patches nothing and the game spawns exactly as shipped.");
		CfgEnemySpawnShufflePool = Config.Bind<string>("Gameplay", "EnemySpawnShufflePool", "imp;gargoyle;gooper;nun;zombie;plantasha;goonshroom", "Semicolon-separated enemy keys the shuffle is allowed to spawn. The shuffle *replaces* the spawner's own choice, so a walkable enemy left off this list never appears in a run at all - which is what happened to the goonshroom, added by game 0.3.1 while this list was still the six pre-0.3.1 types. Each key needs a Tools/Spawn<Key>NameHint to find its prefab; a key with no hint is skipped and logged. Chaser bosses (dragon, wendigo) are refused here whatever this says - they have their own mechanic. Empty restores vanilla spawning.");
		CfgEnemySpawnMode = Config.Bind<string>("Gameplay", "EnemySpawnMode", "grappler-bias", "How EnemySpawnShuffle changes spawning. 'grappler-bias' (default) keeps the per-room enemy table the game authored and only raises the odds of the enemies named in GrapplerSpawnKeys, by GrapplerSpawnWeight; a room whose table has no grappler is left exactly as vanilla rolls it. 'pool' is the older behaviour: every spawner in the game rolls EnemySpawnShufflePool instead of its own table, which makes every room the same mix and discards the goonshroom nests and single-enemy rooms the scenes actually contain. 'vanilla' patches nothing. The debug SpawnShufflePoolKey uses EnemySpawnShufflePool in every mode.");
		CfgGrapplerSpawnKeys = Config.Bind<string>("Gameplay", "GrapplerSpawnKeys", "Imp;GoonShroom", "Semicolon-separated EnemyData asset names weighted up by grappler-bias mode. These are the game's own names as they appear in a spawner's table (Imp, GoonShroom, Nun, Gooper, Gargoyle, Zombie Enemy, PlantEnemy, Black Serpent, Blinded Beast), NOT the mod's enemy keys. Default is the clinging family, which is what has cling scripts behind it.");
		CfgGrapplerSpawnWeight = Config.Bind<float>("Gameplay", "GrapplerSpawnWeight", 1.5f, "Weight multiplier applied to each GrapplerSpawnKeys entry in a spawner's own table. 1 is vanilla odds. 1.5 (default) makes a grappler half again as likely as any other single entry in the same room - deliberately gentle, because it lands on top of a room the spawn multiplier has already inflated and on top of grapple reinforcement, which spawns up to two more grapplers once a grapple starts. Note the table may already repeat a name to weight it - the game does this - and each occurrence is weighted separately, so a table listing Imp twice at x2 rolls imp four times as often as a name listed once.");
		CfgImpSpawnWeightMultiplier = Config.Bind<float>("Gameplay", "ImpSpawnWeightMultiplier", 2f, "Relative spawn weight for imps when EnemySpawnShuffle is on (2 = twice as likely as each other normal enemy type).");
		CfgGrabGameOverGrabEnemyKeys = Config.Bind<string>("Gameplay", "GrabGameOverGrabEnemyKeys", "wendigo", "Grab enemies that normally trigger game over. With GodMode, game over is replaced by EndGrab (or Q key). Dragon handled separately.");
		CfgKeyEndGrab = Config.Bind<KeyboardShortcut>("Gameplay", "EndGrabKey", new KeyboardShortcut((KeyCode)113, Array.Empty<KeyCode>()), "End the current grab H-scene, grapple, or camera-swap interact scene (after EndGrabDelaySeconds). Default: Q.");
		CfgKeyEndGrabDebug = Config.Bind<KeyboardShortcut>("Gameplay", "EndGrabDebugKey", new KeyboardShortcut((KeyCode)270, Array.Empty<KeyCode>()), "Instantly end the current H-scene (debug). Default: numpad +. Shift+= also works.");
		CfgEndGrabDelaySeconds = Config.Bind<float>("Gameplay", "EndGrabDelaySeconds", 20f, "Seconds before EndGrabKey (Q) can leave an H-scene. Imp mouse-shake escape is unaffected. 0 = immediate.");
		CfgEndGrabDelaySecondsChaserBoss = Config.Bind<float>("Gameplay", "EndGrabDelaySecondsChaserBoss", 40f, "Q escape delay for dragon and wendigo grabs (and matching interact scenes). Other enemies use EndGrabDelaySeconds.");
		CfgServiceSceneKeys = Config.Bind<string>("Gameplay", "ServiceSceneKeys", "service;gravy;minothaur;minotaur", "Semicolon-separated name fragments that mark an interact scene as a *service* rather than a scene that charges you: a service adds no horny lock and pays out a release instead, the same one a peephole gives, once per source. Matched case-insensitively as a substring against the trigger name, its gallery id and its clip names. Gravy is here because she heals in the unmodded game, so her scene lowering the meter is what the game already implies; the literal `service` was the only fragment this test used before and matches nothing the game ships. How much a release pays is AmbientReleaseClearsAllLocks. Empty turns the exemption off and every interact scene charges.");
		CfgMinibossEnemyKeys = Config.Bind<string>("Gameplay", "MinibossEnemyKeys", "blinded_beast", "Semicolon-separated enemy keys treated as minibosses: their grab costs HeatLockMinibossLocks horny locks instead of one and holds you for EndGrabDelaySecondsMiniboss instead of EndGrabDelaySeconds. Keys are the ones Naming/EnemyRemap produces, so `blinded_beast`, not the prefab name. Chaser bosses (dragon, wendigo) are not listed here - they already have their own heavier treatment, a full lock-out and EndGrabDelaySecondsChaserBoss. Empty makes every enemy ordinary again.");
		CfgEndGrabDelaySecondsMiniboss = Config.Bind<float>("Gameplay", "EndGrabDelaySecondsMiniboss", 30f, "Q escape delay for a MinibossEnemyKeys grab (and its matching interact scene). Sits between the ordinary EndGrabDelaySeconds and the chaser bosses' EndGrabDelaySecondsChaserBoss, which is the point of the tier.");
		CfgHeatLockMinibossLocks = Config.Bind<int>("Gameplay", "HeatLockMinibossLocks", 2, "Horny locks a MinibossEnemyKeys grab adds. An ordinary grab adds one; a chaser boss goes straight to the limit. Clamped to at least 1, and it can never take the total past the horny limit - the last lock still just fills the meter.");
		CfgEndGrabImmunitySeconds = Config.Bind<float>("Gameplay", "EndGrabImmunitySeconds", 4f, "Seconds the player cannot be grabbed again after a grab ends (Q key or normal EndGrab).");
		CfgEndGrabEnemyCooldownSeconds = Config.Bind<float>("Gameplay", "EndGrabEnemyCooldownSeconds", 0f, "Extra grab cooldown forced onto the enemy when a grab ends while it is still alive, as a floor under the enemy's own grabCooldown. 0 leaves vanilla's timing alone, which is the default: the mod protects what is on screen rather than blocking the re-grab (§122). Raise it to hold an enemy off for longer than the game would.");
		CfgNunGrabPreferenceMultiplier = Config.Bind<float>("Gameplay", "NunGrabPreferenceMultiplier", 1.45f, "Ghoul/nun (EnemyAI) grab tuning. Higher = shorter grab cooldown, longer melee cooldown, and in grab range they wait for grab instead of punching. 1 = vanilla. Default 1.45 is clearly weaker than old 2.5.");
		CfgGargoyleGrabRate = Config.Bind<float>("Gameplay", "GargoyleGrabRate", 1.28f, "Gargoyle (SpinningEnemyAI) grab attempt rate. 1.28 = clearly more grab tries. 1 = vanilla.");
		CfgGargoyleShootRate = Config.Bind<float>("Gameplay", "GargoyleShootRate", 1.35f, "Gargoyle ranged shot rate. 1.35 = clearly more shots. 1 = vanilla.");
		CfgGargoyleSpinRangeScale = Config.Bind<float>("Gameplay", "GargoyleSpinRangeScale", 0.8f, "Gargoyle spin engagement radius scale (<1 = smaller spin zone, more ranged play). 0.8 = noticeable shift. 1 = vanilla.");
		CfgGargoyleSpinCooldownScale = Config.Bind<float>("Gameplay", "GargoyleSpinCooldownScale", 1.18f, "Gargoyle delay between spins (>1 = less spinning). 1 = vanilla.");
		CfgPlantashaGrabRate = Config.Bind<float>("Gameplay", "PlantashaGrabRate", 1.25f, "Plantasha melee grab attempt rate (dash/H-scene, no grab projectile). 1 = vanilla.");
		CfgPlantashaShootRate = Config.Bind<float>("Gameplay", "PlantashaShootRate", 1.32f, "Plantasha ranged shot rate (damage-only projectile). 1 = vanilla.");
		CfgEnemyProjectileSpeedMultiplier = Config.Bind<float>("Gameplay", "EnemyProjectileSpeedMultiplier", 1.28f, "Speed multiplier for enemy ranged/grab projectiles. 1.28 = clearly faster bolts, still dodgeable. 1 = vanilla.");
		CfgHideNearbyEnemiesOnScene = Config.Bind<bool>("Gameplay", "HideNearbyEnemiesOnScene", true, "Hide nearby enemies during grab / interact H-scenes. The triggering enemy is always hidden too.");
		CfgGrabScreenAudioFill = Config.Bind<string>("Gameplay", "GrabScreenAudioFill", "GoonShroom_GrabscreenStart=Goonshroom gangbang sex;GoonShroom_GrabscreenCum=Goonshroom Gangbang cum;Imp_Grab_Loop=Imp Gangbang grab sound;Imp_Grab_Cum=Imp Gangbang grab CUM sound", "Semicolon-separated `grab_screen_animator_state=audio_clip_name`, used ONLY when the grabbed enemy authored no grabScreenAnimations of its own. Game 0.3.1 ships the goonshroom and all five imps with that array empty and all three GrabScreen instances with null grabStartSound/grabLoopSound, so those two families' grab screens are silent; the clips themselves exist in the build and are simply unreferenced. A clip name that is not loaded when the grab starts is logged and skipped, which leaves vanilla's silence. Empty disables the fill entirely.");
		CfgHideNearbyEnemyRadius = Config.Bind<float>("Gameplay", "HideNearbyEnemyRadius", 30f, "Radius (m) around the player/trigger to hide enemies during H-scenes.");
		CfgBlockAttacksDuringGrabStruggle = Config.Bind<bool>("Gameplay", "BlockAttacksDuringGrabStruggle", true, "During grab H-scenes block weapon attacks; only the heat bar cooldown struggle remains (no sword damage to escape).");
		CfgGrabVariantSuffixes = Config.Bind<string>("Naming", "GrabVariantSuffixes", "BlindedBeastTransformedGrabScreen=_t", "Semicolon-separated `controller_substring=suffix`. When the grab-screen animator is running a matching controller, the suffix is appended to the resolved gallery name. This exists for enemies with two grab screens whose animator states are named identically: game 0.3.1's Blinded Beast is a two-stage miniboss, and both BlindedBeastGrabScreen and BlindedBeastTransformedGrabScreen declare states called exactly Loop and Cum, so both stages would otherwise resolve to blinded_beast_loop despite their clips being different lengths. Longest matching key wins. Empty disables it.");
		CfgEnemyNameRemap = Config.Bind<string>("Naming", "EnemyRemap", "Hood_Enemy NoTape=nun;Hood_Enemy=nun;Mimic=mimic;Gooper=gooper;Gargoyle=gargoyle;Dragon=dragon;Baphomet=baphomet;Baph=baphomet;Gravy=gravy;Minotaur=gravy;minotaur=gravy;Minothaur=gravy;minothaur=gravy;Wendigo=wendigo;Imp_Enemy=imp;GoonShroom_Enemy=goonshroom;Goon Shroom=goonshroom;GoonShroom=goonshroom;Black Serpent Enemy=serpent;BlackSerpent=serpent;Black Serpent=serpent;Blinded Beast=blinded_beast;BlindedBeast=blinded_beast;Plantasha_Enemy=plantasha;Plantasha=plantasha;Zombie_Enemy=zombie;Zombie=zombie", "Semicolon-separated `prefab_or_galleryId=key` map. Case-insensitive substring match. Longer keys first.");
		CfgGalleryAliases = Config.Bind<string>("Naming", "GalleryAliases", "", "Overrides for the built-in animator-state to gallery table (see GalleryTable.cs in the source). Empty by default: the table ships in the DLL so updates reach you. Anything listed here is applied on top and wins, and survives a plugin update. Same syntax as before - semicolon-separated <natural_name>=<alias>, target '-' means skip the state entirely, and a target may end with '?seek=<ms>'.");
		CfgInGameAliases = Config.Bind<string>("Naming", "InGameAliases", "", "Overrides for the built-in live-gameplay alias table (see GalleryTable.cs). Checked before the shared table when a hook fires from live gameplay; the gallery viewer ignores it. Empty by default - the table ships in the DLL.");
		CfgUnlockAllGallery = Config.Bind<bool>("Gallery", "UnlockAll", true, "Treat every gallery entry (enemies, peek, diorama) as unlocked in the menu.");
		CfgDioramaGalleryMap = Config.Bind<string>("Gallery", "DioramaAmbientMap", "D1=ambient_nun_chair_fuck;D2=ambient_gooper_bed_blowjob;D3=ambient_zombie_bench_fuck;D4=ambient_gargoyle_ledge_fuck;D5=ambient_imp_gangbang;D6=ambient_imp_gangbang_2;D7=ambient_nun_wall_chain_head;D8=ambient_mimic_wall_fuck;D9=ambient_plantasha_blowjob;GargoyleD4=ambient_gargoyle_ledge_fuck;GooperBedD2=ambient_gooper_bed_blowjob;ImpGangbang=ambient_imp_gangbang;ImpGangbangD6=ambient_imp_gangbang_2;MImicWallFuckD8=ambient_mimic_wall_fuck;NunChair=ambient_nun_chair_fuck;NunChairD1=ambient_nun_chair_fuck;NunWallBJ=ambient_nun_wall_chain_head;NunWallBJD7=ambient_nun_wall_chain_head;PlantBJ=ambient_plantasha_blowjob;PlantBJD9=ambient_plantasha_blowjob;ZombieBench=ambient_zombie_bench_fuck;ZombieBenchD3=ambient_zombie_bench_fuck;WendigoPeek=ambient_wendigo_hole;D10=ambient_dragon_squat_ride;D11=ambient_wendigo_squat_ride;D12=ambient_goonshroom_gangbang;D13=ambient_nun_watersports;D14=ambient_plant_gangbang;D15=ambient_serpent_wall_blowjob;DragonD10=ambient_dragon_squat_ride;WendigoD11=ambient_wendigo_squat_ride;GoonShroomD12=ambient_goonshroom_gangbang;NunWatersportsD13=ambient_nun_watersports;PlantBJD14=ambient_plant_gangbang;SerpentD15=ambient_serpent_wall_blowjob", "Maps DioramaGalleryUI galleryID values (PNC uses D1..D9) to ambient_* EDI scripts. Built-in D-slot fallback applies even if this list is empty.");
		CfgPeekGalleryMap = Config.Bind<string>("Gallery", "PeekGalleryMap", "ImpThreeWayGalleryData=peek_imp_three_way;ImpThreeWayGallery=peek_imp_three_way;Nun&MimicGalleryData=peek_nun_mimic;Mimic&NunGallery=peek_nun_mimic;PlantBJGalleryData=peek_plant_bj;PlantBJGallery=peek_plant_bj;WendigoRideGalleryData=peek_wendigo_ride;WendigoRidingGallery=peek_wendigo_ride;NunsThreewayGalleryData=peek_nuns_threeway;NunsDuoGallery=peek_nuns_threeway;GooperGloryHoleGalleryEntry=peek_gooper_pillory;GooperPilloaryGallery=peek_gooper_pillory;ZombieBJpeekscene=peek_zombie_bj;ZombieBJGallery=peek_zombie_bj;GravyBathingGallery=peek_gravy_bath;Gargoyle FuckfestGallery=peek_gargoyle_fuck_fest;GargoyleFuckFestGallery=peek_gargoyle_fuck_fest;Serpent Prison Style=peek_serpent_prison;Serpent Prison Style Gallery=peek_serpent_prison;BlindedBeastRide=peek_werewolf_ride;Blinded Beast Ride Gallery=peek_werewolf_ride;ImpThreeWay=peek_imp_three_way;ImpThree=peek_imp_three_way;Imp_Grab_Three=peek_imp_three_way;ImpGrabThree=peek_imp_three_way;ImpThreway=peek_imp_three_way;ZombieBJPeekScene=peek_zombie_bj;ZombieBJ=peek_zombie_bj;PlantBJ=peek_plant_bj;WendigoRide=peek_wendigo_ride;WendigoPeek=peek_wendigo_ride;NunsThreeWay=peek_nuns_threeway;NunsThree=peek_nuns_threeway;NunsThreeway=peek_nuns_threeway;GooperPillory=peek_gooper_pillory;NunMimic=peek_nun_mimic;NunAndMimic=peek_nun_mimic;GargoyleFuckFest=peek_gargoyle_fuck_fest;GargoyleFuck=peek_gargoyle_fuck_fest;GravyBath=peek_gravy_bath;GravyBathing=peek_gravy_bath;SerpentPrisonStyle=peek_serpent_prison;SerpentPrison=peek_serpent_prison;Werewolf=peek_werewolf_ride;WerewolfRide=peek_werewolf_ride;BlindedBeastRide=peek_werewolf_ride;Blinded Beast Ride=peek_werewolf_ride", "Maps a peek EnemyGalleryEntry to its peek_* EDI script. Keys are matched exactly against, in order: the entry asset's own name, the name of the peephole animator controller it plays, its enemyName, and finally its enemyID. Asset and controller names are what 0.3.1 kept stable; the displayed enemyName is a joke title there and the enemyID is only P1..P11.");
		CfgPeekClipMap = Config.Bind<string>("Gallery", "PeekClipMap", "Peephole_Nun&Mimic=peek_nun_mimic;Peephole_Imps=peek_imp_three_way;Peephole_Nuns=peek_nuns_threeway;Peephole_Wendigo=peek_wendigo_ride;Peephole_GooperBJ=peek_gooper_pillory;Peephole_Plantasha=peek_plant_bj;Peephole_Planatasha=peek_plant_bj;Peephole_Zombie=peek_zombie_bj;GargoylePeep=peek_gargoyle_fuck_fest;GravyPeep=peek_gravy_bath;Peephole_Serpent=peek_serpent_prison;Peephole_Werewolf=peek_werewolf_ride;Blinded Beast Ride=peek_werewolf_ride", "Maps a peephole's animation clip name to its peek script, matched as a substring of the slugged clip name, longest key first. This is checked before the alias table because every peek trigger in the game is named 'P1', 'P2', 'P3'... - the clip is the only thing that says which peephole it is, and the alias table's per-enemy substring matcher would otherwise claim it for that enemy's grab scene. Note the game misspells one Plantasha clip as 'Planatasha', hence both spellings.");
	}
}
