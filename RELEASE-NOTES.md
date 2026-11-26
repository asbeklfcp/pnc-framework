# PNC Framework v0.1.13 - Living Map Performance Hotfix

For PNC 0.4.2.

- Fixed repeated optional-component lookups when optional characters are disabled or not installed.
- The map now caches available and unavailable component types, and refreshes missing lookups when new assemblies load.
- Reduces map stuttering and repeated HarmonyX missing-type warnings, without reducing map resolution, visual quality or animation settings.
- Optional characters remain entirely optional. Existing map settings and gameplay are unchanged.

Use the normal in-game updater. New Windows players can use the complete starter ZIP. Experimental Linux/Proton support remains unchanged.

# PNC Framework v0.1.12 - Community Update

For Post Nut Calamity 0.4.2.

## Gameplay
- Joker is less frequent: no guaranteed entrance spawn, roaming placement checks every 30 seconds instead of 8, and at most three active traps.
- New Normal Characters section under Optional Characters, alongside Custom Enemies. Ten regular character families have individual switches, enabled by default. Disabled normal characters are excluded from supported room and arena spawn selections; disabling Mimic also prevents future mimic chest allocations. Required bosses and special scripted encounters are unchanged.
- Settings are saved automatically. No manual configuration editing is required.

## Experimental Linux / Proton support
- A small native Linux installer is available as a separate download. It retrieves the existing base package and current approved signed update, preserving settings and the signed receipt. No new full installation ZIP is required.
- Existing Proton installations should use the Linux installer once to reach this version. After that, the experimental in-game updater prepares signed updates for application on the next game launch, before framework plugins load. Windows retains its existing PowerShell updater.
- Native Linux/Proton execution has not been tested; please report compatibility issues with logs. The Linux installer and Proton updater are experimental.

The reported Joker animation freeze with EDI could not be reproduced in an isolated animation-clock test; no speculative animation change is included.

---

# PNC Framework v0.1.11 ? Living Map

**For Post Nut Calamity 0.4.2.**

## Living Map
- New world map and configurable minimap, with actual dungeon geometry, multiple projections, room and character markers, floor selection, and navigation guidance.
- Open or close the map with **M** by default. Rebind the shortcut in Mod Manager > Controls.
- Move the minimap anywhere on the screen from the map's Minimap settings.
- Explore with Map, Free Cam, and Player Cam. Map and Free Cam retain their positions within a dungeon; Player Cam starts at your current viewpoint. All reset appropriately when entering a new dungeon or shop.
- Smooth camera movement, right-click orbit/look, WASD movement, zoom, speed adjustments, and manual recentering.
- Optional full-dungeon viewing; exploration progress remains separate.
- Simplified Mod Manager: only Enable Map is listed in the Living Map settings. Camera and display preferences are in the map itself.

## Compatibility
The signed golden-arrow update retains the existing framework components and player settings. No game files or private developer tools are included.

## v0.1.6 - Golden Arrow Receipt Recognition Hotfix

Corrects the public manager's embedded release ID so it matches the signed installed-release receipt. v0.1.5 installed properly, but its manager still identified itself as v0.1.4, causing false "Installed release receipt mismatch" errors after installation. v0.1.6 updates the manager to 1.8.16 and keeps the tested EDI 3.0.26 plugin. All gameplay, scripts, profiles and previously tested Wolf/D17, D16, Gravy and randomized Brothel compatibility are preserved. Players already on v0.1.5 may need a one-time trusted rollback recovery before the golden arrow can update normally; do not delete or edit the signed receipt manually.
## v0.1.5 — Vanilla Brothel EDI compatibility hotfix

Corrects EDI playback in the Shop/Brothel random camera scenes by using the selected live animation instead of controller clip order. Vanilla QA passed for Blinded Beast, Nun-Imp, Pinky and the Gravy shopkeeper. Preserves the original random outcomes, native automatic ending, gameplay limits, the complete v0.1.4 EDI library, and the previously tested D17 Werewolf Cowgirl (Wolf) scene. Physical-device synchronization is not certified.
## v0.1.3 — Clean-install EDI gallery compatibility hotfix

Fixes missing `Edi/Gallery/Definitions.csv` on fresh public installations. The signed update now creates the gallery definition file from the verified script bundle, then installs all 666 scripts and merges 178 aliases safely. Existing customized scripts and definitions remain preserved or held for review. The public signing key and full 666-file content are unchanged. Some UI elements may look unfinished or buggy; further visual polish will follow. The experimental V31 UI is not included.

# PNC Framework v0.1 — Initial Public Release

**For Post Nut Calamity 0.4.2 — Windows x64**

This community framework includes five selectable gameplay profiles: **Vanilla, Pressure And Release, God Mode, Overkeks and Custom**.

Highlights:
- Parchment-style F11 Mod Manager.
- Customizable framework controls.
- Supported community gameplay integrations.
- Custom-content and gallery compatibility improvements.
- Gameplay and enemy-system modifications.
- Parchment patch notes and update interface.
- Installer, removal utility and rollback backups.

## ⚠️ Known gameplay issues

This first public release has known bugs. Baphomet may fail to pursue, attack effectively or teleport consistently; Wolf's second-form grab may occasionally fail; Wendigo may retain a red damage tint or behave inconsistently after defeat; Dragon may retain damage tint; and some high-heat conditions may prevent expected captures. Conditions vary and other bugs may exist. Future updates will focus on these problems.

Please report the gameplay profile, enemy, reproduction steps and relevant logs with personal information removed.

Optional Joker/Witch packages and experimental Archive Passage content are **not included**. The signed public updater and release history are active; the separate-PC v0.1.0 → v0.1.1 test passed.

Download **PNC-Framework-v0.1.0-PNC0.4.2-win64.zip**. You need your own supported game. Extract the complete ZIP, run **Install.cmd**, and select **Post Nut Calamity.exe**. Missing BepInEx is downloaded from its official source and verified automatically; no manual prerequisite download is required. Clean installs start in Vanilla with default controls and normal gallery progression. See [Installation](https://github.com/asbeklfcp/pnc-framework/blob/main/INSTALLATION.md), [Known issues](https://github.com/asbeklfcp/pnc-framework/blob/main/KNOWN-ISSUES.md) and [Credits](https://github.com/asbeklfcp/pnc-framework/blob/main/CREDITS.md).

## v0.1.1 — GitHub updater delivery

The signed public updater is active. A clean GitHub v0.1.0 installation and a genuine signed v0.1.0 → v0.1.1 update passed on a separate PC. The test confirmed the golden arrow, chime and border, published patch notes, download and installation, completion window, Restart Game, successful startup, and “You have the latest approved release.” The same update was not offered again.

v0.1.1 is delivery-only: no gameplay, controls, settings or visual changes. Known gameplay bugs remain. Future offers require an approved signed release and a verified versioned package; ordinary commits and private builds do not trigger notifications. Internet access is required for online checks and downloads. This successful public test does not certify every device, failure or rollback scenario.

Existing users: open the update arrow → **Check again** → read the notes → **Download Update** → wait for **UPDATE READY** → **Exit Game and Install** → completion window → **Restart Game**. The v0.1.1 `.update.zip` is for the in-game updater. First installations still use the complete v0.1.0 installer ZIP.

## v0.1.2 — EDI gallery script update

Complete 666-track library (138 Handy1, 138 Handy2, 195 Handy2 Pro, 195 Detailed); 16 new gallery definitions; 178 configuration mappings. The signed manager update preserves identical existing files and holds customized differences for review with rollback backups.

**UI notice:** Some interface elements may appear unfinished or buggy. We released scripts before completing the V31 visual polish, which is not included in this update. External EDI hardware sync remains unverified.
