using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.Mono;
using BepInEx.Unity.Mono.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace PncEdi;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public partial class Plugin : BaseUnityPlugin
{
	public const string PluginGuid = "com.edi.pnc";
	public const string PluginName = "Post Nut Calamity EDI Integration";
	public const string PluginVersion = "3.0.25";
	public static bool FrameworkReady { get; private set; }
	internal static ManualLogSource Log;
	private static readonly object MissingDefinitionLogLock = new object();
	private static readonly HashSet<string> MissingDefinitionLogged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
	internal static Plugin Instance;

	internal static readonly HttpClient Http = new HttpClient
	{
		Timeout = TimeSpan.FromSeconds(2.0)
	};


	internal static bool EdiPausedByFocus;
	internal static bool ShouldBeStopped;
	internal static string LastSent;
	internal static float LastSentTime;

	// The bare row name behind LastSent and how long one of its loops is, kept so a ladder
	// switch can work out where in the cycle the outgoing row had got to. Both are cleared by
	// the pause/resume/stop markers, which are not rows and have no cycle.
	private static string _lastSentRow;
	private static int _lastSentLoopMs;
	internal static bool PlayerDead;
	private static float _lastDamagePercent;
	private static float _lastCumPercent;
	private static string _lastFillerGallery;
	private static bool _damageLeads;
	private static bool _fillerPlaybackActive;

	// What the device was playing right before a pause menu swapped it out for the base row, so
	// closing the menu can put it back where it was rather than wherever the ladder would land
	// fresh. This covers the filler and a real scene row alike (a grab, an interact scene) -
	// null/-1 means there was nothing worth restoring. _fillerSavedWasFiller records which of the
	// two it was, so the restore can pass SendPlay the right `filler` flag rather than always
	// claiming the row back as filler.
	private static string _fillerSavedGallery;
	private static int _fillerSavedPhaseMs = -1;
	private static bool _fillerSavedWasFiller;

	// Whether the scene on screen is a menu, by the same name test OnSceneChanged has always
	// used. Cached rather than re-read, because GoFiller is reached from a dozen places and the
	// answer only changes when the scene does.
	private static bool _inMenuScene;

	// True while what the device is playing is the filler rather than a scene. PauseHooks needs
	// it to tell the one playback FillerWhilePaused may leave running from every other.
	internal static bool FillerPlaybackActive => _fillerPlaybackActive;

	// True while the device is playing an actual gallery scene rather than filler, nothing, or
	// a paused/stopped state. SendPlay records which of the two it last sent.
	internal static bool IsGalleryPlaybackActive => !_fillerPlaybackActive && !ShouldBeStopped && !string.IsNullOrEmpty(LastSent) && LastSent[0] != '_';

	// The two gates every gameplay patch reads. They were `CfgEnableGameplayTweaks.Value` and
	// `CfgGodMode.Value` read directly at sixty-odd call sites; they now go through
	// GameplayProfiles, so a profile can answer for all of them at once and no call site has to
	// know that profiles exist. Under the default Custom profile they resolve to exactly the two
	// config values they replaced.
	internal static bool GameplayTweaksEnabled => OverkeksMode.Exiting || GameplayProfiles.TweaksEnabled;
	internal static bool GodModeEnabled => OverkeksMode.Exiting || GameplayProfiles.GodModeEnabled;

	// What the profile was last time we looked, so a SettingChanged can say what it changed from.
	private static GameplayProfile _lastGameplayProfile;

	private static float _heldDamageBucket;
	private static float _heldCumBucket;
	private static readonly string[] CurrentHeatMemberNames = new string[5] { "Heat", "CurrentHeat", "currentHeat", "heat", "heatAmount" };
	private int _lastStateHash;
	private int _lastInteractStateHash;
	private int _impTier;
	private bool _impWasGrappling;
	private bool _impLoopPlaying;
	private float _impLoopAt;
	private float _impTierDropAt;
	private bool _interactWasActive;
	private float _ambientScanTimer;
	private float _ambientDiagTimer;

	private void Awake()
	{
		Instance = this;
		Log = base.Logger;
		if (!FrameworkCompatibility.Verify(Log)) { enabled = false; return; }
		BindConfig();
		OverkeksConfig.Initialize(Config);
        BossTestTools.Initialize(Config);
		CommunityCombatRuntime.Initialize(Config);
		OverkeksFeatureRuntime.Initialize(Config);
		OverkeksEnemyRuntime.Initialize(Config);
		FreeCam.InitializeBindings(Config);
		PncFramework.Controls.ControlService.RegisterConfig(Config);
		// Profiles are switchable while the game runs - that is the point of the mod manager - so
		// the mod has to hear about it. Every gameplay patch stays installed under every profile
		// and gates on GameplayProfiles instead, which is what makes a live switch safe; what the
		// switch still has to undo is the stats the outgoing profile wrote onto the player.
		_lastGameplayProfile = CfgGameplayProfile.Value;
		CfgGameplayProfile.SettingChanged += OnGameplayProfileChanged;
		NameRemap.Reload();
		ClassHeatMultipliers.Reload();
		GalleryAliases.Reload();
		DioramaGalleryMap.Reload();
		PeekGalleryMap.Reload();
		SceneManager.activeSceneChanged += OnSceneChanged;
		// Awake runs inside whatever scene the game booted into and before the first
		// activeSceneChanged, so the menu flag has to start from the active scene rather than
		// from a callback that has not fired yet - otherwise the GoFiller below reads the boot
		// menu as gameplay.
		_inMenuScene = IsMenuSceneName(SceneManager.GetActiveScene().name);
		ApplyPatches();
		// Custom-enemy packages load in PncCustomEnemies, which declares a BepInDependency on this
		// plugin and therefore has its Awake run after this one - after the alias and gallery
		// tables it registers rows into, which is the order that used to be spelled out here.
		GoFiller();
		FrameworkReady = true;
	}

	private void Update()
	{
        TickGuarded("capture-audio-pause",CaptureAudioPause.Tick);
        TickGuarded("enemy-audio-pause",EnemyGameplayAudioPause.Tick);
        TickGuarded("capture-boss-hud",CaptureBossHud.Tick);
        TickGuarded("chaser-hit-layer",ChaserHitLayerLease.Tick);
		TickProfileDiagnostics();
		GameplayProfiles.TickNamedGodModeHealth();
		OverkeksMode.Tick();
		CommunityCombatRuntime.Tick();
		OverkeksFeatureRuntime.Tick();
		OverkeksEnemyRuntime.Tick();
		// Before anything reads a key: BepInEx resolves its input backend on first access and
		// caches the result, and on 0.3.1 that happens before the keyboard device exists, which
		// leaves every hotkey â€” ours and every configured KeyboardShortcut â€” reading false for the
		// whole run. SafeInput re-runs the probe once the keyboard is there. No-op after that.
		SafeInput.EnsureBackend();
		// The serpent's range hand-back is counted here rather than in the ladder: the case it
		// exists for is a grab scene, which is exactly when the ladder is not being asked anything.
		SerpentHypnosis.TickIntensityRelease();
		// And the chaser stomp is ticked here for the same reason and one more: a grab scene has
		// to take the range back on the frame it starts, and the filler refresh the row-selection
		// half of it would otherwise live in stops being called at exactly that moment. It runs
		// after the serpent so that a serpent release this frame is already visible to it.
		ChaserStomp.Tick();
		// MasterIntensity and RowIntensityScale are edited live from the mod manager, and a
		// softening that only takes hold at the next scene change reads as not working. Polling
		// them is a comparison of two ints and a string reference per frame; ApplyIntensity sends
		// nothing unless the derived figure actually moved.
		int masterNow = Mathf.Clamp(CfgMasterIntensity?.Value ?? 100, 0, 100);
		string rowScaleNow = CfgRowIntensityScale?.Value ?? string.Empty;
		if (masterNow != _lastMasterIntensity
			|| !string.Equals(rowScaleNow, _rowScaleSource, StringComparison.Ordinal))
		{
			_lastMasterIntensity = masterNow;
			ApplyIntensity("settings changed");
		}
		bool galleryBlocks = GalleryHooks.BlocksInGameEdiTracking();
		if (galleryBlocks)
		{
			_lastStateHash = 0;
			_lastInteractStateHash = 0;
			_fillerPlaybackActive = false;
		}
		else if (GrabHooks.IsGrabAnimatorReadyForEdi())
		{
			Animator currentAnimator = GrabHooks.CurrentAnimator;
			AnimatorStateInfo currentAnimatorStateInfo = currentAnimator.GetCurrentAnimatorStateInfo(0);
			if (currentAnimatorStateInfo.shortNameHash != _lastStateHash)
			{
				_lastStateHash = currentAnimatorStateInfo.shortNameHash;
				string stepName = GrabHooks.ResolveGrabStepName(currentAnimator, currentAnimatorStateInfo);
				if (!string.IsNullOrEmpty(stepName))
				{
					string enemyKey = GrabHooks.CurrentEnemyKey ?? "enemy";
					string galleryName = GrabHooks.BuildGalleryName(enemyKey, stepName);
					DBG("STEP", enemyKey + "/" + stepName + " -> " + galleryName);
					SendPlay(galleryName);
				}
			}
		}
		else if (!galleryBlocks && (_lastStateHash != 0 || GrabHooks.CurrentAnimator != null))
		{
			GrabScreen grabScreen = GrabScreen.Instance;
			if (!(grabScreen != null) || !grabScreen.IsGrabbed)
			{
				DBG("GRAB-LOST", "key=" + GrabHooks.CurrentEnemyKey + " -> filler");
				GrabHooks.CurrentAnimator = null;
				GrabHooks.CurrentEnemyKey = null;
				GrabHooks.CurrentAnims = null;
				GrabHooks.CurrentRawPrefab = null;
				_lastStateHash = 0;
				GoFiller();
			}
		}
		if (!galleryBlocks && CfgEnableInteractive.Value && CameraSwapHooks.Active && CameraSwapHooks.IsCinematicLive() && CameraSwapHooks.CurrentAnimator != null)
		{
			Animator currentAnimator2 = CameraSwapHooks.CurrentAnimator;
			if (currentAnimator2.gameObject.activeInHierarchy && currentAnimator2.runtimeAnimatorController != null && currentAnimator2.isActiveAndEnabled)
			{
				AnimatorStateInfo currentAnimatorStateInfo2 = currentAnimator2.GetCurrentAnimatorStateInfo(0);
				if (currentAnimatorStateInfo2.shortNameHash != _lastInteractStateHash && currentAnimatorStateInfo2.length > 0f)
				{
					_lastInteractStateHash = currentAnimatorStateInfo2.shortNameHash;
					string clipName = null;
					AnimatorClipInfo[] currentAnimatorClipInfo = currentAnimator2.GetCurrentAnimatorClipInfo(0);
					if (currentAnimatorClipInfo != null && currentAnimatorClipInfo.Length != 0 && currentAnimatorClipInfo[0].clip != null)
					{
						clipName = currentAnimatorClipInfo[0].clip.name;
					}
					if (string.IsNullOrEmpty(clipName))
					{
						clipName = $"state_{currentAnimatorStateInfo2.shortNameHash:x}";
					}
					string interactKey = CameraSwapHooks.CurrentKey ?? "interact";
					string currentGalleryId = CameraSwapHooks.GetCurrentGalleryId();
					string peekGallery = PeekGalleryMap.Resolve(currentGalleryId, CameraSwapHooks.CurrentRawName, clipName);
					if (PeekGalleryMap.IsPeekScript(peekGallery))
					{
						GalleryHooks.InGamePeekScene = true;
						HeatLockSystem.QueueReleaseFromPeekScene(CameraSwapHooks.CurrentTrigger, currentGalleryId, CameraSwapHooks.CurrentRawName, clipName);
						DBG("PEEK-STEP", currentGalleryId + "/" + clipName + " -> " + peekGallery);
						SendPlay(peekGallery);
					}
					else
					{
						GalleryHooks.InGamePeekScene = false;
						string galleryName = GrabHooks.BuildGalleryName(interactKey, clipName);
						DBG("INTERACT-STEP", interactKey + "/" + clipName + " -> " + galleryName);
						SendPlay(galleryName);
					}
				}
			}
			_interactWasActive = true;
		}
		else if (!galleryBlocks && (_interactWasActive || CameraSwapHooks.Active))
		{
			_interactWasActive = false;
			_lastInteractStateHash = 0;
			CameraSwapHooks.ResetState();
			GoFiller();
		}
		if (!galleryBlocks && CfgEnableImp.Value)
		{
			GrappleScreenobject grappleScreenobject = GrappleScreenobject.Instance;
			if (grappleScreenobject != null && grappleScreenobject.IsGrappling)
			{
				// The list and the counter can disagree for a frame during teardown; the larger
				// of the two is the one that matches what is on screen.
				int clingingTier = GrappleEnemies.ClingingTier(grappleScreenobject);
				int grappleCount = Mathf.Clamp((clingingTier > 0) ? clingingTier : grappleScreenobject.GrappleCount, 1, 9);
				int tier = Mathf.Clamp(grappleCount, 1, 3);
				// Whose cling scripts to play. Game 0.3.1 made goonshrooms grapple too, and this
				// used to be a single hardcoded prefix, so their grapple played imp_1/2/3 (Â§66).
				string prefix = ResolveGrapplePrefix(grappleScreenobject);
				if (prefix == null)
				{
					// Unknown grappler: dispatch nothing rather than another creature's scene.
					// A gap is inert and says so; a wrong scene is silent and plausible.
					if (!_impWasGrappling)
					{
						_impWasGrappling = true;
						DBG("GRAPPLE", "no prefix for family '"
							+ (GrappleEnemies.ResolveGrappleFamily(grappleScreenobject) ?? "<unreadable>")
							+ "' - add it to GrapplePrefixMap; playing nothing");
					}
				}
				else if (!_impWasGrappling)
				{
					_impWasGrappling = true;
					_impTier = tier;
					_impTierDropAt = 0f;
					_impLoopPlaying = false;
					_impLoopAt = Time.time + Mathf.Max(0f, CfgImpGrappleLoopDelaySeconds.Value);
					string tierScript = prefix + tier;
					DBG("IMP", $"start tier={tier} count={grappleCount} -> {tierScript}");
					SendPlay(tierScript);
				}
				else if (tier > _impTier)
				{
					_impTier = tier;
					_impTierDropAt = 0f;
					_impLoopPlaying = false;
					_impLoopAt = Time.time + Mathf.Max(0f, CfgImpGrappleLoopDelaySeconds.Value);
					string tierScript = prefix + tier;
					DBG("IMP", $"escalate tier={tier} count={grappleCount} -> {tierScript}");
					SendPlay(tierScript);
				}
				// Shaking one off has to come back down. This branch did not exist: the tier
				// only ever climbed, so a three-imp grapple shaken back to one kept playing
				// imp_3 until the grapple ended entirely - the device saying more is happening
				// than is. The hold below is why it is not simply the mirror of the escalate
				// branch: the count is read per frame off the attached list, and it dips for a
				// frame or two while a shaken-off grappler is released and again while its
				// replacement attaches, so an immediate drop would step down and back up.
				else if (tier < _impTier)
				{
					float hold = Mathf.Max(0f, CfgGrappleTierDropHoldSeconds.Value);
					if (_impTierDropAt <= 0f)
					{
						_impTierDropAt = Time.time + hold;
					}
					if (Time.time >= _impTierDropAt)
					{
						_impTier = tier;
						_impTierDropAt = 0f;
						_impLoopPlaying = false;
						_impLoopAt = Time.time + Mathf.Max(0f, CfgImpGrappleLoopDelaySeconds.Value);
						string tierScript = prefix + tier;
						DBG("IMP", $"drop tier={tier} count={grappleCount} -> {tierScript}");
						SendPlay(tierScript);
					}
				}
				else if (tier == _impTier && _impTierDropAt > 0f)
				{
					// Back to the tier already playing before the hold expired: nothing to send.
					_impTierDropAt = 0f;
				}
				else if (!_impLoopPlaying && CfgImpGrappleLoopDelaySeconds.Value > 0f && Time.time >= _impLoopAt)
				{
					_impLoopPlaying = true;
					// Same prefix as the tier scenes, so this follows the grappler too:
					// imp_ -> imp_grab_loop, goonshroom_ -> goonshroom_grab_loop.
					string loop = prefix + "grab_loop";
					DBG("IMP", $"loop tier={_impTier} count={grappleCount} -> {loop}");
					SendPlay(loop);
				}
			}
			else if (_impWasGrappling)
			{
				_impWasGrappling = false;
				_impTier = 0;
				_impTierDropAt = 0f;
				_impLoopPlaying = false;
				_impLoopAt = 0f;
				if (GrabScreen.Instance == null || !GrabScreen.Instance.IsGrabbed)
				{
					DBG("IMP", "ended -> filler");
					GoFiller();
				}
				else
				{
					DBG("IMP", "ended -> grab screen active, no filler");
				}
			}
		}
		if (CfgEnableAmbient.Value)
		{
			_ambientScanTimer -= Time.deltaTime;
			if (_ambientScanTimer <= 0f)
			{
				_ambientScanTimer = Mathf.Max(0.5f, CfgAmbientScanInterval.Value);
				AmbientProximity.Rescan();
			}
			AmbientProximity.Tick();
			AmbientReleaseGaze.Tick();
		}
		RefreshFillerForCurrentHeat();
		if (GameplayTweaksEnabled)
		{
			// These seven are independent of each other, but a throw in any one of them used to
			// abort the rest for the whole run â€” and invisibly, because a MonoBehaviour exception
			// goes to the Unity log, which BepInEx does not mirror unless WriteUnityLog = true.
			// That is exactly how 0.3.1's legacy-input change killed both escape paths from
			// HeatLockSystem.HandleHotkeys, three calls upstream of them. One throwing tick is now
			// one dead tick, named once in our own log.
			TickGuarded("heatlock-keys", HeatLockSystem.HandleHotkeys);
			TickGuarded("heatlock", HeatLockSystem.Tick);
			TickGuarded("escape-gate", SceneEscapeGate.Tick);
			TickGuarded("escape-keys", SceneEscapeGate.HandleEscapeKeys);
			TickGuarded("grapple-death", GrappleDeathSequence.Tick);
			TickGuarded("grapple-reinforce", GrappleReinforcement.Tick);
			TickGuarded("enemy-autofix", EnemyInactiveAutofix.Tick);
		}
		if (CfgEnableDebugEnemySpawn.Value)
		{
			DebugEnemySpawn.TickHotkeys();
            TickGuarded("boss-test-tools",BossTestTools.Tick);
			if (Hotkeys.IsDown(CfgKeySpawnShufflePool))
			{
				EnemySpawnShuffle.SpawnRandomFromPoolDebug();
			}
		}
		if (CfgEnableFreecam.Value)
		{
			if (FreeCam.WasTogglePressed(CfgKeyFreecam.Value))
			{
				FreeCam.Toggle();
			}
			FreeCam.Tick();
		}
		if (CfgAmbientDiag.Value)
		{
			_ambientDiagTimer -= Time.deltaTime;
			if (_ambientDiagTimer <= 0f)
			{
				_ambientDiagTimer = 2f;
				AmbientProximity.DiagnosticDump(CfgAmbientDiagRange.Value);
			}
		}
	}

	// The gallery prefix for whoever is currently clinging, or null if this family has none.
	//
	// Falls back to CfgImpPrefix when the grappler cannot be read at all: imps were the only
	// grappler before game 0.3.1, so that is the behaviour every verified log was produced
	// against, and an unreadable list should not silently stop the device.
	private static string ResolveGrapplePrefix(GrappleScreenobject grapple)
	{
		string family = GrappleEnemies.ResolveGrappleFamily(grapple);
		if (string.IsNullOrEmpty(family))
		{
			return CfgImpPrefix.Value;
		}
		return GrapplePrefixForFamily(family);
	}

	// The cling prefix for one enemy key, or null if that family has none.
	//
	// GrapplePrefixMap is the mod's list of families that can cling: a key is in it exactly when
	// there are cling scenes named for it. That makes it the honest answer to "is this a
	// grappler" as well - see GrappleEnemies.IsGrapplerEnemy, which asks it rather than
	// carrying a second list that could disagree with this one.
	internal static string GrapplePrefixForFamily(string family)
	{
		if (string.IsNullOrEmpty(family))
		{
			return null;
		}
		string map = CfgGrapplePrefixMap?.Value ?? "";
		string[] entries = map.Split(';');
		for (int i = 0; i < entries.Length; i++)
		{
			string entry = entries[i].Trim();
			int eq = entry.IndexOf('=');
			if (eq <= 0)
			{
				continue;
			}
			if (string.Equals(entry.Substring(0, eq).Trim(), family, StringComparison.OrdinalIgnoreCase))
			{
				return entry.Substring(eq + 1).Trim();
			}
		}
		return null;
	}

	// One failing per-frame tick must not take the rest of Update with it, and must not be silent
	// either. Logged once per site â€” a throw here repeats every frame, so logging every time would
	// bury the log and the first line is the one that matters.
	private static readonly System.Collections.Generic.HashSet<string> _tickFaultsSeen = new System.Collections.Generic.HashSet<string>();

	private static void TickGuarded(string site, Action tick)
	{
		try
		{
			tick();
		}
		catch (Exception ex)
		{
			if (_tickFaultsSeen.Add(site))
			{
				ManualLogSource log = Log;
				if (log != null)
				{
					log.LogError((object)("[TICK] " + site + " threw and is dead for this run: " + ex));
				}
			}
		}
	}

	internal static void DBG(string evt, string detail)
	{
		if (CfgDebug != null && !CfgDebug.Value)
		{
			return;
		}
		Log?.LogInfo($"[{DateTime.Now:HH:mm:ss.fff}] [{evt}] {detail}");
	}

	private static readonly Dictionary<string, string> _lastRepeatedDetail = new Dictionary<string, string>();
	private static readonly Dictionary<string, int> _repeatCount = new Dictionary<string, int>();

	/// <summary>
	/// <see cref="DBG"/> for a line that a per-frame path would otherwise print thousands of times.
	/// The first occurrence logs; identical repeats are counted, and the count is flushed as one
	/// line when the message changes or <see cref="DBGRepeatEnd"/> is called. A refusal that holds
	/// for four seconds is one fact, and the 2026-08-27 run spent 21% of its log restating two of
	/// them (Â§138) - in a file whose whole value is that a person reads it start to finish.
	/// </summary>
	internal static void DBGRepeat(string evt, string key, string detail)
	{
		if (_lastRepeatedDetail.TryGetValue(key, out string previous) && previous == detail)
		{
			_repeatCount[key] = (_repeatCount.TryGetValue(key, out int n) ? n : 0) + 1;
			return;
		}
		DBGRepeatEnd(evt, key);
		_lastRepeatedDetail[key] = detail;
		DBG(evt, detail);
	}

	/// <summary>Flush the suppressed count for a key, and forget it. Safe to call when there is none.</summary>
	internal static void DBGRepeatEnd(string evt, string key)
	{
		if (_repeatCount.TryGetValue(key, out int suppressed) && suppressed > 0)
		{
			DBG(evt, $"...the line above repeated {suppressed} more time(s)");
		}
		_repeatCount.Remove(key);
		_lastRepeatedDetail.Remove(key);
	}

	internal static void LogMissingDefinition(string galleryName, string resolvedName)
	{
		if (string.IsNullOrEmpty(resolvedName))
		{
			return;
		}
		lock (MissingDefinitionLogLock)
		{
			if (!MissingDefinitionLogged.Add(resolvedName))
			{
				return;
			}
			try
			{
				string path = Path.Combine(Paths.BepInExRootPath, "PncEdi-missing-definitions.log");
				string contents = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {galleryName} -> {resolvedName} (no such row in the gallery registry; definitions: {GalleryRegistry.Source}){Environment.NewLine}";
				File.AppendAllText(path, contents);
			}
			catch (Exception ex)
			{
				ManualLogSource log = Log;
				if (log != null)
				{
					log.LogWarning((object)("Failed to write missing EDI definition log: " + ex.Message));
				}
			}
		}
	}

	// Where in its own loop the row currently playing has got to, or -1 when that cannot be
	// answered - nothing playing, a pause/stop marker, or a row Definitions.csv gives no length.
	//
	// Time.realtimeSinceStartup rather than Time.time: this is wall-clock phase on a device that
	// keeps running while the game is paused, and it is the same clock LastSentTime is stamped
	// from.
	private static int CurrentLoopPhaseMs()
	{
		if (string.IsNullOrEmpty(_lastSentRow) || _lastSentLoopMs <= 0)
		{
			return -1;
		}
		int elapsedMs = (int)((Time.realtimeSinceStartup - LastSentTime) * 1000f);
		if (elapsedMs < 0)
		{
			return -1;
		}
		return elapsedMs % _lastSentLoopMs;
	}

	// `preservePhase` is for switching between rows of one ladder - the filler intensity set, or
	// the serpent's hypnosis tiers. Those rows are built on a shared time grid with a shared
	// anchor (code/ladders.py), so starting the incoming row at the phase the outgoing one had
	// reached continues the same gesture at a new amplitude instead of snapping back to the top.
	//
	// Edi supports exactly this: `POST Play/{name}?seek=<ms>` takes a [FromQuery] long, and its
	// own loop restart passes `elapsed % duration` back through the same parameter - so a seek
	// biases the first pass only and the row loops from 0 afterwards, which is what we want.
	// Asking for it is free when it cannot be honoured: an unknown length or a mismatched cycle
	// falls back to a plain Play, i.e. the behaviour that was there before.
	//
	// `animNormalizedTime` / `animClipSeconds` are the other use of the same seek: starting a
	// scene at the phase its *animation* is already at. Every grab screen in the game starts its
	// animator at 0, so this is inert for all of them but one - the Black Serpent's, which is
	// already mid-loop when the grab begins (`[GRAB-ANIM] ... t=335.68` in the 2026-08-22 log,
	// against t=0.00 for all eleven other controllers). Without it that scene's script starts at
	// 0 against an animation two-thirds through its cycle, which is the desync that was reported.
	internal static void SendPlay(string galleryName, bool loop = true, bool inGame = true, bool filler = false, bool preservePhase = false, float animNormalizedTime = -1f, float animClipSeconds = 0f, int seekOverrideMs = -1)
	{
		if (string.IsNullOrEmpty(galleryName))
		{
			return;
		}
		_fillerPlaybackActive = false;
		string alias = GalleryAliases.Resolve(galleryName, inGame);
		if (string.IsNullOrEmpty(alias) || alias == "-" || alias.Equals("__SKIP__", StringComparison.OrdinalIgnoreCase))
		{
			LogMissingDefinition(galleryName, alias);
			DBG("EDI-SKIP", galleryName + " (alias=skip)");
			return;
		}
		string row = alias;
		int seek = 0;
		int queryStart = alias.IndexOf('?');
		if (queryStart > 0)
		{
			row = alias.Substring(0, queryStart);
			string[] queryParts = alias.Substring(queryStart + 1).Split('&');
			foreach (string part in queryParts)
			{
				int eq = part.IndexOf('=');
				if (eq > 0)
				{
					string key = part.Substring(0, eq).Trim();
					string value = part.Substring(eq + 1).Trim();
					if (key.Equals("seek", StringComparison.OrdinalIgnoreCase))
					{
						int.TryParse(value, out seek);
					}
				}
			}
		}
		if (!GalleryRegistry.IsKnown(row))
		{
			LogMissingDefinition(galleryName, row);
			DBG("EDI-SKIP", galleryName + " -> " + row + " (no such row in the gallery registry; definitions: " + GalleryRegistry.Source + ")");
			return;
		}
		_fillerPlaybackActive = filler;
		if (!(alias == LastSent) || !(Time.realtimeSinceStartup - LastSentTime < 0.25f))
		{
			int loopMs = GalleryRegistry.LoopMs(row);
			// Align to the animation's own phase. `animNormalizedTime` counts whole clip cycles,
			// so its fractional part is the phase within one cycle - but a script is not always
			// one cycle long (a grab loop is two, by this set's convention), so the phase has to
			// be taken across the script rather than across the clip. `cycles` is how many clip
			// lengths the row spans; anything that does not come out near a whole number means
			// the script and the clip are not describing the same loop and the seek is dropped
			// rather than guessed - the old behaviour, and the safe way to be wrong.
			if (seek <= 0 && animNormalizedTime >= 0f && animClipSeconds > 0.001f && loopMs > 0)
			{
				float clipMs = animClipSeconds * 1000f;
				float cyclesF = (float)loopMs / clipMs;
				int cycles = Mathf.RoundToInt(cyclesF);
				if (cycles >= 1 && Mathf.Abs(cyclesF - cycles) <= 0.1f)
				{
					// Position within the script, in clip cycles, then in milliseconds.
					float withinScript = Mathf.Repeat(animNormalizedTime, cycles);
					int seekMs = Mathf.Clamp((int)(withinScript * clipMs), 0, loopMs - 1);
					if (seekMs > 0)
					{
						seek = seekMs;
						DBG("EDI-PHASE", $"{row} aligned to animation t={animNormalizedTime:0.00} ({cycles} cycle(s) of {clipMs:0}ms) -> seek {seekMs}ms");
					}
				}
			}
			// A caller-supplied seek - the pause-menu filler swap restoring its own saved row - is
			// as authored a decision as the alias's own `?seek=` and outranks preservePhase's guess,
			// which is comparing against the wrong outgoing row here anyway (the base filler, not
			// the row being restored).
			if (seek <= 0 && seekOverrideMs > 0)
			{
				seek = seekOverrideMs;
			}
			// An explicit `?seek=` in the alias is an authored decision about where that scene
			// starts and outranks a phase carried over from the row before it.
			if (preservePhase && seek <= 0)
			{
				int phaseMs = CurrentLoopPhaseMs();
				// Same cycle length is the whole precondition: the two rows only share a phase
				// if they share a period. Crossing between ladders, or into one from a scene,
				// starts at the top - both ends anchor at 0, so that is the old behaviour and
				// no worse than it was.
				if (phaseMs > 0 && loopMs > 0 && loopMs == _lastSentLoopMs)
				{
					seek = phaseMs;
				}
			}
			LastSent = alias;
			LastSentTime = Time.realtimeSinceStartup;
			_lastSentRow = row;
			_lastSentLoopMs = loopMs;
			ShouldBeStopped = false;
			string url = CfgEdiUrl.Value + "/Edi/Play/" + Uri.EscapeDataString(row);
			if (seek > 0)
			{
				url += $"?seek={seek}";
			}
			FireAndForget(url, "Play " + row);
			SerpentHypnosis.NoteRowPlayed(row);
			// The row changed, so the per-row scale may have too - and a row that carries one is
			// usually a row nothing else asks about intensity for. NoteRowPlayed goes first
			// because it owns the scene-level restore this then scales.
			ApplyIntensity("row " + row);
			string logName = ((seek > 0) ? $"{row}@{seek}ms" : row);
			if (!string.Equals(alias, galleryName, StringComparison.OrdinalIgnoreCase))
			{
				DBG("EDI", "Play " + logName + "  (alias of " + galleryName + ")");
			}
			else
			{
				DBG("EDI", "Play " + logName);
			}
		}
	}

	// The device's own amplitude knob, as distinct from which script is playing.
	//
	// `POST /Edi/Intensity/{max}` scales `device.Max` between the range configured for the
	// device and does **not** re-dispatch the gallery: DevicePlayer.Intensity sets the property,
	// DeviceBase debounces it 100 ms and calls applyRange(), and HandyV3Device.applyRange sends
	// SetStroke(Min, Max). Measured against a Handy 2 Pro on 2026-08-25 with the patched Edi
	// this project ships (code/intensitybench.py): `v2/slide` max followed 100/70/40/20 exactly,
	// and the playback phase stayed within **22 ms** of prediction across six changes, one of
	// them across a loop seam. So this is amplitude without a restart, which is the one thing
	// SendPlay cannot do.
	//
	// It is **global to the channel**, not a property of a row, so whatever turns it down owns
	// turning it back up. Everything else in the mod assumes 100.
	private static int _lastIntensitySent = 100;

	// What the *scene* asked for, before the player's own scaling. The two are tracked separately
	// because they answer to different things: a scene lowers the range for a reason of its own
	// (the serpent's approach) and raises it again when that reason ends, while MasterIntensity and
	// RowIntensityScale are a standing preference that must survive every one of those changes. So
	// a scene sets this, the player's settings scale it, and either side moving re-derives the
	// number the device is actually told.
	private static int _requestedIntensity = 100;

	/// <summary>
	/// What the scene last asked for, before the player's own scaling. Read by anything that has
	/// to notice the channel moving under it: two scene-level systems share this one number, so
	/// comparing against what *you* last sent leaves you believing a figure the device no longer
	/// holds.
	/// </summary>
	internal static int RequestedIntensity => _requestedIntensity;

	private static int _lastMasterIntensity = 100;

	private static string _rowScaleSource;

	private static readonly Dictionary<string, int> _rowScale =
		new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

	/// <summary>Ask the device for an amplitude on the scene's behalf, before player scaling.</summary>
	internal static void SendIntensity(int max, string why)
	{
		_requestedIntensity = Mathf.Clamp(max, 0, 100);
		ApplyIntensity(why);
	}

	/// <summary>Hand the device its full range back. Safe to call when nothing lowered it.</summary>
	internal static void ResetIntensity(string why)
	{
		SendIntensity(100, why);
	}

	// Re-derive what the device should be holding and send it if that has moved. Called by
	// SendIntensity, by SendPlay once a new row is live (the per-row scale is a property of the
	// row, so it changes under a scene that never touched intensity itself), and by Update when
	// either setting is edited - the mod manager writes config live, and a softening the player
	// cannot hear until the next scene change reads as not working.
	private static void ApplyIntensity(string why)
	{
		int scaled = ScaleIntensity(_requestedIntensity);
		if (scaled == _lastIntensitySent)
		{
			return;
		}
		_lastIntensitySent = scaled;
		FireAndForget(CfgEdiUrl.Value + "/Edi/Intensity/" + scaled, "Intensity " + scaled);
		if (scaled != _requestedIntensity)
		{
			DBG("EDI-INTENSITY", scaled + "% (" + why + "; scene asked " + _requestedIntensity
				+ "%, master " + Mathf.Clamp(CfgMasterIntensity?.Value ?? 100, 0, 100) + "%"
				+ (RowScaleFor(_lastSentRow) < 100 ? ", row " + RowScaleFor(_lastSentRow) + "%" : "")
				+ ")");
		}
		else
		{
			DBG("EDI-INTENSITY", scaled + "% (" + why + ")");
		}
	}

	// Percentages multiply: the scene's own figure, the player's global scale, and the row's. Each
	// is a fraction of the one before it, so a serpent approach at 44% under a master of 50% is
	// 22% and not 50% - turning the master down can never make a scene louder than it asked to be.
	private static int ScaleIntensity(int requested)
	{
		int master = Mathf.Clamp(CfgMasterIntensity?.Value ?? 100, 0, 100);
		int row = RowScaleFor(_lastSentRow);
		return Mathf.Clamp(Mathf.RoundToInt(requested * (master / 100f) * (row / 100f)), 0, 100);
	}

	private static int RowScaleFor(string row)
	{
		string map = CfgRowIntensityScale?.Value ?? string.Empty;
		if (!string.Equals(map, _rowScaleSource, StringComparison.Ordinal))
		{
			_rowScaleSource = map;
			_rowScale.Clear();
			string[] entries = map.Split(';');
			for (int i = 0; i < entries.Length; i++)
			{
				string entry = entries[i].Trim();
				int eq = entry.IndexOf('=');
				if (eq > 0 && eq < entry.Length - 1
					&& int.TryParse(entry.Substring(eq + 1).Trim(), out var percent))
				{
					_rowScale[entry.Substring(0, eq).Trim()] = Mathf.Clamp(percent, 0, 100);
				}
			}
			if (_rowScale.Count > 0)
			{
				var summary = new System.Text.StringBuilder();
				foreach (var kv in _rowScale)
				{
					if (summary.Length > 0)
					{
						summary.Append(", ");
					}
					summary.Append(kv.Key).Append('=').Append(kv.Value).Append('%');
				}
				DBG("EDI-INTENSITY", "row scales: " + summary);
			}
		}
		if (!string.IsNullOrEmpty(row) && _rowScale.TryGetValue(row, out var scale))
		{
			return scale;
		}
		return 100;
	}

	internal static void NotePlayerDamage(PlayerStats playerStats, int damage)
	{
		if (damage > 0)
		{
			UpdateCumPercent(playerStats);
			// Called from the TakeDamage *prefix*, so health has not dropped yet - project
			// the hit so the filler reacts on the same frame rather than one later.
			UpdateDamagePercent(playerStats, damage);
			RefreshFillerForCurrentHeat();
		}
	}

	internal static void NotePlayerCum(PlayerStats playerStats)
	{
		UpdateCumPercent(playerStats);
		RefreshFillerForCurrentHeat();
	}

	private static string GetFillerGallery(out float animNormalizedTime, out float animClipSeconds)
	{
		animNormalizedTime = -1f;
		animClipSeconds = 0f;
		RefreshPlayerHeatSnapshot();
		// The serpent's camera grab outranks both meters while it runs. It is a thing happening
		// *to* the player rather than a state they are in, and it ends either in a grab - which
		// dispatches its own scene over the top of this - or in the serpent breaking off, after
		// which the meters answer again.
		string gallery = SerpentHypnosis.CurrentGallery();
		// A chaser boss close enough to stomp outranks the meters the same way, and for the same
		// reason - it too is a thing happening to the player rather than a read on their state.
		// It carries its own phase (an audio clock, not an Animator's), which is why this is the
		// one provider in the chain that hands anything back through the out params.
		if (string.IsNullOrEmpty(gallery))
		{
			gallery = ChaserStomp.CurrentGallery(out animNormalizedTime, out animClipSeconds);
		}
		if (string.IsNullOrEmpty(gallery))
		{
			gallery = ResolveFillerIntensity();
		}
		if (string.IsNullOrEmpty(gallery))
		{
			gallery = CfgFillerGallery.Value;
		}
		if (!string.Equals(_lastFillerGallery, gallery, StringComparison.OrdinalIgnoreCase))
		{
			DBG("FILLER", $"intensity heat={_lastCumPercent:P0} damage={_lastDamagePercent:P0} -> {gallery}");
			_lastFillerGallery = gallery;
		}
		return gallery;
	}

	// Arousal used to win outright, with damage as the fallback for "heat below the lowest
	// bucket". Heat locks broke that: the lock floor holds heat permanently above 0, so
	// from a few locks on the cum map always matched and filler_damage_* became
	// unreachable no matter how hurt the player was. Default is now "whichever reads
	// higher", with the old precedence still available.
	private static string ResolveFillerIntensity()
	{
		string priority = CfgFillerIntensityPriority?.Value?.Trim();
		bool damageLeads = ((priority != null && priority.Equals("Damage", StringComparison.OrdinalIgnoreCase)) || (!(priority != null && priority.Equals("Cum", StringComparison.OrdinalIgnoreCase)) && DamageLeadsWithHysteresis()));
		string gallery = (damageLeads ? ResolveIntensityGallery(CfgFillerDamageGalleryMap?.Value, _lastDamagePercent, ref _heldDamageBucket) : ResolveIntensityGallery(CfgFillerCumGalleryMap?.Value, _lastCumPercent, ref _heldCumBucket));
		if (!string.IsNullOrEmpty(gallery))
		{
			return gallery;
		}
		// The leading signal sat below its lowest bucket - let the other one answer.
		if (!damageLeads)
		{
			return ResolveIntensityGallery(CfgFillerDamageGalleryMap?.Value, _lastDamagePercent, ref _heldDamageBucket);
		}
		return ResolveIntensityGallery(CfgFillerCumGalleryMap?.Value, _lastCumPercent, ref _heldCumBucket);
	}

	// Which meter is currently in front, under the same hysteresis the buckets already had.
	//
	// `Higher` used to be a bare `_lastDamagePercent > _lastCumPercent`, read every frame. Both
	// meters move continuously - heat drains, health refills - so whenever they were close the
	// device alternated between the two families several times a second. The 2026-08-22 log
	// carries it plainly: 39 filler switches less than a second apart, among them
	// `filler_damage_25 -> filler_cum_25` four times and the reverse four times, and the same
	// pair at tier 50. That is the "still jerky at times" note, and it is not a tier change at
	// all - the two families sit at the same amplitude and differ in their *floor*, so a flip
	// changes how deep every trough goes without changing anything the player did.
	//
	// This is exactly the failure `ResolveIntensityGallery` below was given hysteresis for, one
	// level up and missed at the time, so it reuses the same setting rather than adding one.
	private static bool DamageLeadsWithHysteresis()
	{
		float margin = Mathf.Clamp(CfgFillerIntensityHysteresis?.Value ?? 0f, 0f, 0.5f);
		// The leader keeps the lead until the other is clear of it by the margin.
		if (_damageLeads)
		{
			if (_lastCumPercent > _lastDamagePercent + margin)
			{
				_damageLeads = false;
			}
		}
		else if (_lastDamagePercent > _lastCumPercent + margin)
		{
			_damageLeads = true;
		}
		return _damageLeads;
	}

	// `held` is the threshold of the bucket this map is currently sitting in, or 0 for none. A
	// bucket is entered at its own threshold and left only once the percentage falls a clear
	// margin below it: heat drains and health refills continuously, and this runs every frame,
	// so a bare `percent >= threshold` puts the device between two scripts several times a
	// second for as long as the meter rests on a boundary. SendPlay's 0.25 s repeat suppression
	// is no help there - it only covers the *same* name, and a flap alternates two.
	private static string ResolveIntensityGallery(string map, float percent, ref float held)
	{
		if (string.IsNullOrWhiteSpace(map) || percent <= 0f)
		{
			held = 0f;
			return null;
		}
		string bestGallery = null;
		string heldGallery = null;
		float bestThreshold = 0f;
		float hysteresis = Mathf.Clamp(CfgFillerIntensityHysteresis?.Value ?? 0f, 0f, 0.5f);
		string[] entries = map.Split(';');
		for (int i = 0; i < entries.Length; i++)
		{
			string entry = entries[i].Trim();
			int eq = entry.IndexOf('=');
			if (eq > 0 && eq < entry.Length - 1 && float.TryParse(entry.Substring(0, eq).Trim(), out var threshold))
			{
				threshold = ((threshold >= 1f) ? (threshold / 100f) : threshold);
				if (percent >= threshold && threshold >= bestThreshold)
				{
					bestThreshold = threshold;
					bestGallery = entry.Substring(eq + 1).Trim();
				}
				// Remember the held bucket's own name in the same pass, so leaving it is a
				// decision about one number rather than a second walk of the map.
				if (held > 0f && Mathf.Abs(threshold - held) < 0.0001f)
				{
					heldGallery = entry.Substring(eq + 1).Trim();
				}
			}
		}
		if (held > bestThreshold && heldGallery != null && percent >= held - hysteresis)
		{
			return heldGallery;
		}
		held = bestThreshold;
		return bestGallery;
	}

	private static void RefreshPlayerHeatSnapshot()
	{
		PlayerStats playerStats = Object.FindAnyObjectByType<PlayerStats>();
		if (playerStats != null)
		{
			UpdateCumPercent(playerStats);
			UpdateDamagePercent(playerStats);
			return;
		}
		// No PlayerStats means we're in a menu or the gallery. Without this the
		// percentages keep their last in-game values and filler resumes at e.g.
		// filler_cum_75 from the previous run.
		_lastCumPercent = 0f;
		_lastDamagePercent = 0f;
		_heldDamageBucket = 0f;
		_heldCumBucket = 0f;
		_damageLeads = false;
	}

	private static void OnGameplayProfileChanged(object sender, EventArgs args)
	{
		GameplayProfile previous = _lastGameplayProfile;
		_lastGameplayProfile = CfgGameplayProfile.Value;
		GameplayProfiles.OnProfileChanged(previous, _lastGameplayProfile);
	}

	private static void UpdateCumPercent(PlayerStats playerStats)
	{
		if (!(playerStats == null))
		{
			float maxHeat = Mathf.Max(1f, playerStats.MaxHeat);
			if (TryGetFloatMember(playerStats, CurrentHeatMemberNames, out var value))
			{
				// Heat is only the default answer. Once locks hold the heat floor up, heat reports
				// how many locks are held rather than what is happening, so HandyIntensitySource
				// can take lock progress instead - or whichever is stronger. See GameplayProfiles.
				_lastCumPercent = GameplayProfiles.ResolveHandyIntensity(
					Mathf.Clamp01(Mathf.Max(0f, value) / maxHeat));
			}
		}
	}

	// How hurt the player is *right now*, not how big the last hit was. The old
	// high-water mark only ever ratcheted up in-game (its single reset is the
	// no-PlayerStats branch above, i.e. menus and the gallery), so once damaged the run
	// was stuck on filler_damage_* no matter how much health came back.
	private static void UpdateDamagePercent(PlayerStats playerStats, int pendingDamage = 0)
	{
		if (!(playerStats == null))
		{
			float maxHealth = Mathf.Max(1f, GetPlayerMaxHealth(playerStats));
			float health = Mathf.Clamp((float)(playerStats.CurrentHealth - Mathf.Max(0, pendingDamage)), 0f, maxHealth);
			_lastDamagePercent = Mathf.Clamp01(1f - health / maxHealth);
		}
	}

	// Runs every frame. It used to short-circuit on "neither percentage moved" and only then
	// resolve a gallery, which cost a scene search either way and, worse, could not see a change
	// that came from anywhere but the two meters - the serpent's hypnosis tier moves with
	// distance and would have been invisible here. Resolving unconditionally and comparing the
	// *name* is both cheaper (one PlayerStats lookup instead of two on a change) and complete.
	private static void RefreshFillerForCurrentHeat()
	{
		if (!CanRefreshFillerForHeat())
		{
			return;
		}
		string lastFillerGallery = _lastFillerGallery;
		string fillerGallery = GetFillerGallery(out float animNormalizedTime, out float animClipSeconds);
		if (!string.Equals(lastFillerGallery, fillerGallery, StringComparison.OrdinalIgnoreCase))
		{
			// Same ladder, shared grid, shared anchor: carry the phase over rather than
			// restarting the device at the top of the new row. If ChaserStomp handed back an
			// audio phase instead, SendPlay tries that first and preservePhase is the fallback
			// it never reaches.
			SendPlay(fillerGallery, loop: true, inGame: true, filler: true, preservePhase: true,
				animNormalizedTime: animNormalizedTime, animClipSeconds: animClipSeconds);
		}
	}

	// Swaps whatever the device is playing out for the plain base row when the pause menu opens -
	// the filler (a ladder rung, chaser stomp) or a real gallery row (a grab, an interact scene)
	// alike - saving what it was and where in its loop so ResumeFillerFromMenu can put it back.
	// Called only from the branch that would otherwise have left the interrupted row running
	// unchanged, which read wrong to the ear regardless of which kind of row it was.
	internal static void PauseFillerForMenu()
	{
		if (_fillerPlaybackActive)
		{
			_fillerSavedGallery = _lastFillerGallery;
			_fillerSavedWasFiller = true;
		}
		else if (IsGalleryPlaybackActive)
		{
			_fillerSavedGallery = _lastSentRow;
			_fillerSavedWasFiller = false;
		}
		else
		{
			_fillerSavedGallery = null;
		}
		_fillerSavedPhaseMs = CurrentLoopPhaseMs();
		SendPlay(CfgFillerGallery.Value, loop: true, inGame: true, filler: true);
		DBG("PAUSE", _fillerSavedGallery != null
			? $"pause menu opened -> base filler ({CfgFillerGallery.Value}), saved {_fillerSavedGallery} at {_fillerSavedPhaseMs}ms"
			: $"pause menu opened -> base filler ({CfgFillerGallery.Value}), nothing to save");
	}

	// The other half of PauseFillerForMenu: reissue the saved row at the phase it had reached
	// when the menu opened, rather than let CanRefreshFillerForHeat's next tick pick whatever the
	// ladder reads as *now*, which may have drifted while the base filler was standing in. Passes
	// back the same `filler` flag the row was saved under, so a resumed scene row is not
	// mistaken for filler by IsGalleryPlaybackActive and everything gated on it.
	internal static void ResumeFillerFromMenu()
	{
		if (!string.IsNullOrEmpty(_fillerSavedGallery))
		{
			DBG("PAUSE", $"pause menu closed -> restore {_fillerSavedGallery} at {_fillerSavedPhaseMs}ms");
			SendPlay(_fillerSavedGallery, loop: true, inGame: true, filler: _fillerSavedWasFiller, seekOverrideMs: _fillerSavedPhaseMs);
		}
		ClearSavedFillerForMenu();
	}

	// Scene changes while paused (quitting to the main menu) leave the game floor the saved row
	// belonged to, so a later ResumeGame that never fires must not be able to replay it into
	// whatever comes next. PauseHooks.ResetForNewScene calls this on every scene change made
	// while GamePaused was true, whether or not this session ever ran PauseFillerForMenu.
	internal static void ClearSavedFillerForMenu()
	{
		_fillerSavedGallery = null;
		_fillerSavedPhaseMs = -1;
		_fillerSavedWasFiller = false;
	}

	private static bool CanRefreshFillerForHeat()
	{
		// A charm circle owns the channel while the player is inside it (see AmbientProximity), so
		// the filler must not push its own row on top when heat moves.
		// The menu filler is a fixed row on purpose (GoMenuFiller), so nothing may push a
		// ladder rung over it from percentages the ended run left behind. The pause menu's own
		// base-filler swap is the same idea for the same reason: PauseFillerForMenu already put
		// the row this frame should show, and letting the ladder recompute over it during a pause
		// is exactly the "keeps drifting while paused" bug the swap exists to fix.
		if (!_fillerPlaybackActive || _inMenuScene || PlayerDead || GalleryHooks.BlocksInGameEdiTracking() || CustomEnemyBridge.EdiChannelHeld || PauseHooks.GamePaused)
		{
			return false;
		}
		GrabScreen grabScreen = GrabScreen.Instance;
		if (grabScreen != null && grabScreen.IsGrabbed)
		{
			return false;
		}
		GrappleScreenobject grappleScreenobject = GrappleScreenobject.Instance;
		if (grappleScreenobject != null && grappleScreenobject.IsGrappling)
		{
			return false;
		}
		return !GrabHooks.IsGrabAnimatorReadyForEdi() && !CameraSwapHooks.Active;
	}

	private static int GetPlayerMaxHealth(PlayerStats playerStats)
	{
		if (playerStats == null)
		{
			return CfgPlayerMaxHealth?.Value ?? 1;
		}
		if (TryGetFloatMember(playerStats, new string[4] { "MaxHealth", "maxHealth", "maximumHealth", "baseMaxHealth" }, out var value))
		{
			return Mathf.RoundToInt(value);
		}
		return CfgPlayerMaxHealth?.Value ?? 1;
	}

	private static bool TryGetFloatMember(object instance, string[] names, out float value)
	{
		Type type = instance.GetType();
		for (int i = 0; i < names.Length; i++)
		{
			PropertyInfo property = type.GetProperty(names[i], BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (property != null && TryConvertFloat(property.GetValue(instance, null), out value))
			{
				return true;
			}
			FieldInfo field = type.GetField(names[i], BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (field != null && TryConvertFloat(field.GetValue(instance), out value))
			{
				return true;
			}
		}
		value = 0f;
		return false;
	}

	private static bool TryConvertFloat(object raw, out float value)
	{
		if (raw is int asInt)
		{
			value = asInt;
			return true;
		}
		if (raw is float asFloat)
		{
			value = asFloat;
			return true;
		}
		value = 0f;
		return false;
	}

	// Leaving a gallery scene has to *stop* the device. GoFiller instead starts the filler
	// looping, which in a menu - where there is no gameplay to fill between - just keeps it
	// moving forever. Same defect fixed for scene changes in section 8.4, missed here.
	// In-game gallery/peek views still fall through to filler, which is correct there.
	internal static void EndGalleryPlayback(string logTag)
	{
		if (Object.FindAnyObjectByType<PlayerStats>() == null)
		{
			DBG(logTag, "-> no gameplay");
			GoMenuFiller();
			return;
		}
		DBG(logTag, "-> filler");
		GoFiller();
	}

	private static bool IsMenuSceneName(string sceneName)
	{
		return !string.IsNullOrEmpty(sceneName) && sceneName.IndexOf("menu", StringComparison.OrdinalIgnoreCase) >= 0;
	}

	// What the filler plays outside a run. Deliberately the plain FillerGallery row and not
	// ResolveFillerIntensity: the damage and heat percentages behind the ladder are whatever the
	// run that just ended left in them, so a menu would otherwise open on filler_cum_75 because
	// the last thing that happened was a death.
	private static void GoMenuFiller()
	{
		if (!CfgFillerInMenus.Value)
		{
			_fillerPlaybackActive = false;
			SendStop();
			return;
		}
		SendPlay(CfgFillerGallery.Value, loop: true, inGame: true, filler: true);
	}

	internal static void GoFiller()
	{
		// A menu has no gameplay to fill between, so every route into the filler has to ask
		// first - not just the scene change that spots the menu. GoFiller is reached from a lost
		// grab, a scene ending, a reset and the hotkey, and in a menu all of those mean the same
		// thing.
		if (_inMenuScene)
		{
			GoMenuFiller();
			return;
		}
		// A charm circle plays its own row for as long as the player stands in it, and GoFiller is
		// reached from a dozen places that mean "nothing else is happening" - a lost grab, a scene
		// ending, a scene change. None of those are true inside the circle, so bail before any of
		// them can take the channel back.
		if (CustomEnemyBridge.EdiChannelHeld)
		{
			return;
		}
		// No preservePhase here: GoFiller is the entry *into* filler from somewhere else - a scene
		// ending or a reset - and there is no ladder phase to carry. A chaser stomp still carries
		// its own audio phase, though: a scene can end right next to a stomping dragon, and the row
		// should not restart the beat from 0 when it does.
		string fillerGallery = GetFillerGallery(out float animNormalizedTime, out float animClipSeconds);
		SendPlay(fillerGallery, loop: true, inGame: true, filler: true,
			animNormalizedTime: animNormalizedTime, animClipSeconds: animClipSeconds);
	}

	internal void ResetGrabTracking()
	{
		GrabHooks.ClearLiveGrabState();
		_lastStateHash = 0;
		ResetImpGrappleTrackingOnly();
		GoFiller();
	}

	internal void ResetImpGrappleTrackingOnly()
	{
		_impWasGrappling = false;
		_impTier = 0;
		_impTierDropAt = 0f;
		_impLoopPlaying = false;
		_impLoopAt = 0f;
	}

	internal void ResetGrabStepHash()
	{
		_lastStateHash = 0;
	}

	internal bool HasGrabStepPlayback()
	{
		return _lastStateHash != 0;
	}

	internal void MarkGrabStepHash(int hash)
	{
		_lastStateHash = hash;
	}

	internal static void SendPause()
	{
		FireAndForget(CfgEdiUrl.Value + "/Edi/Pause?untilResume=true", "Pause");
		LastSent = "__paused__";
		LastSentTime = Time.realtimeSinceStartup;
		_lastSentRow = null;
		_lastSentLoopMs = 0;
		DBG("EDI", "Pause?untilResume=true");
	}

	internal static void SendResume()
	{
		FireAndForget(CfgEdiUrl.Value + "/Edi/Resume?AtCurrentTime=true", "Resume");
		LastSent = "__resumed__";
		LastSentTime = Time.realtimeSinceStartup;
		_lastSentRow = null;
		_lastSentLoopMs = 0;
		DBG("EDI", "Resume?AtCurrentTime=true");
	}

	internal static void SendStop()
	{
		_fillerPlaybackActive = false;
		FireAndForget(CfgEdiUrl.Value + "/Edi/Pause", "Stop");
		LastSent = "__stopped__";
		LastSentTime = Time.realtimeSinceStartup;
		_lastSentRow = null;
		_lastSentLoopMs = 0;
		ShouldBeStopped = true;
		DBG("EDI", "Stop (Pause without untilResume)");
	}

	private void OnApplicationFocus(bool hasFocus)
	{
		if (!FrameworkReady) return;
		if (!CfgPauseOnFocusLoss.Value)
		{
			return;
		}
		if (!hasFocus)
		{
			if (!EdiPausedByFocus)
			{
				EdiPausedByFocus = true;
				DBG("FOCUS", "lost (alt-tab/minimize) -> Edi/Pause");
				SendPause();
			}
		}
		else if (EdiPausedByFocus)
		{
			EdiPausedByFocus = false;
			if (PauseHooks.GamePaused && PauseHooks.DevicePausedByMenu)
			{
				DBG("FOCUS", "regained while pause menu still holds device paused -> skip Resume");
				return;
			}
			DBG("FOCUS", "regained -> Edi/Resume");
			SendResume();
			if (ShouldBeStopped)
			{
				DBG("FOCUS", "was stopped before pause, re-stopping after resume");
				SendStop();
			}
		}
	}

	private void OnSceneChanged(Scene from, Scene to)
	{
		OverkeksMode.SceneChanged();
		CommunityCombatRuntime.Reset();
		OverkeksFeatureRuntime.Reset();
		OverkeksEnemyRuntime.Reset();
		// Both facts are read before ResetForScene clears the second one. `playerStats` says
		// whether the new scene's Awake has already run at this point - the ordering Â§148 could
		// not settle from the source - and `baseHeat` is what that ordering decides.
		DBG("SCENE", "'" + from.name + "' -> '" + to.name + "'"
			+ " playerStats=" + ((Object.FindAnyObjectByType<PlayerStats>() != null) ? "yes" : "no")
			+ " baseHeat=" + HeatLockSystem.BaseHeatForDiag.ToString("0.#"));
		FreeCam.HandleSceneChanged();
		GrabHooks.CurrentAnimator = null;
		GrabHooks.CurrentEnemyKey = null;
		GrabHooks.CurrentAnims = null;
		GrabHooks.CurrentRawPrefab = null;
		CameraSwapHooks.ResetState();
		NearbyEnemyHider.RestoreAll();
		PlayerDead = false;
		EnemyKeepAliveHelper.ResetForScene();
		GrappleDeathSequence.Reset("scene change");
		HeatLockSystem.ResetForScene(to.name);
		_lastStateHash = 0;
		_lastInteractStateHash = 0;
		ResetImpGrappleTrackingOnly();
		_interactWasActive = false;
		DebugEnemySpawn.ClearCache();
		EnemySpawnShuffle.ClearCache();
		GrabScreenAudioFill.ClearCache();
		SerpentHypnosis.ResetNow();
		ChaserStomp.ResetNow();
		SceneEscapeGate.EndScene();
		EnemyReactivationHelper.CancelScheduled();
		MimicGrabGate.ResetForNewScene();
		AiStateGuard.ResetForNewScene();
		GrabTransitionGate.ResetForNewScene();
		EnemyGrabGate.ResetForNewScene();
		PauseHooks.ResetForNewScene();
		ImpGrappleGate.ResetForNewScene();
		// Menus (incl. game over -> main menu) fall silent rather than keep the filler running
		// on the device - unless FillerInMenus says otherwise, which is the one place a player
		// asked for the opposite.
		_inMenuScene = IsMenuSceneName(to.name);
		if (_inMenuScene)
		{
			DBG("SCENE", CfgFillerInMenus.Value ? "menu scene -> menu filler" : "menu scene -> stopping EDI");
			GoMenuFiller();
		}
		else
		{
			GoFiller();
		}
	}

	private void OnApplicationQuit()
	{
		OverkeksMode.Exit("quit");
		CommunityCombatRuntime.Reset();
		OverkeksFeatureRuntime.Reset();
		OverkeksEnemyRuntime.Reset();
		if (!FrameworkReady) return;
		// Fire-and-forget would be lost when the process exits, so block briefly to
		// make sure Edi actually receives the stop.
		try
		{
			DBG("EDI", "application quitting -> Stop");
			Http.PostAsync(CfgEdiUrl.Value + "/Edi/Pause", new StringContent("")).Wait(1500);
		}
		catch
		{
		}
	}

	private static void FireAndForget(string url)
	{
		FireAndForget(url, "POST");
	}

	// The POST is never awaited, so the try/catch below only ever sees a synchronous throw (a
	// malformed URI). A refused connection or a timeout lands later, on the task, and without this
	// continuation it is finalised with nobody looking at it - which is how a whole session can run
	// with a log that reads as perfect and no device moving. The continuation runs on a thread-pool
	// thread, so nothing blocks Unity's main loop, and reading t.Exception is also what marks the
	// fault observed.
	//
	// NotOnRanToCompletion, not OnlyOnFaulted: HttpClient enforces its own Timeout by *cancelling*
	// the task, so a timed-out POST ends Canceled with a null Exception and an OnlyOnFaulted
	// continuation never runs at all. That is exactly the failure worth seeing, so both terminal
	// states have to be covered.
	private static void FireAndForget(string url, string what)
	{
		try
		{
			Http.PostAsync(url, new StringContent(string.Empty)).ContinueWith(delegate(Task<HttpResponseMessage> t)
			{
				Exception ex2 = t.Exception?.GetBaseException();
				LogPostFailure(what, (ex2 != null) ? ex2.Message : (t.IsCanceled ? $"no response within {Http.Timeout.TotalSeconds:0.#}s - is Edi running?" : "unknown error"));
			}, TaskContinuationOptions.NotOnRanToCompletion);
		}
		catch (Exception ex)
		{
			LogPostFailure(what, ex.Message);
		}
	}

	private static void LogPostFailure(string what, string reason)
	{
		ManualLogSource log = Log;
		if (log != null)
		{
			log.LogWarning((object)("EDI " + what + " failed: " + reason));
		}
	}
}
