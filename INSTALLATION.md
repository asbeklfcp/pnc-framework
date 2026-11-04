# Installation — PNC Framework v0.1

1. Use a separate playable copy of **PNC 0.4.2, Windows x64 Unity Mono**. Keep a clean backup.
2. If BepInEx is missing, Install.cmd automatically downloads the tested BepInEx 6 Unity Mono x64 build **6.0.0-be.785** from the official source and verifies its pinned SHA-256 before extracting it. Internet access is required for that download. Compatible existing loaders are left untouched; partial or incompatible installations stop safely.
3. Close that game. Download **PNC-Framework-v0.1.0-PNC0.4.2-win64.zip** from the [v0.1.0 release](https://github.com/asbeklfcp/pnc-framework/releases/tag/v0.1.0), then extract the entire ZIP into a separate folder. Do not choose a `.update.zip`, source archive or validation package.
4. Run **Install.cmd**. In the file picker, select **Post Nut Calamity.exe** in your game installation. The folder is determined automatically.
5. Launch the game from that directory. Use **F11** or the Mod Manager button.

The installer verifies the game fingerprint and prerequisites, installs files relative to your selected game directory, and creates a rollback backup under BepInEx/framework-install-backups. Clean new installations start with Vanilla, default framework controls, normal gallery progression (Unlock All off), diagnostics off, Overkeks extra activation range 0 and optional transformation off. With no previous framework configuration, no gallery-migration question appears. Existing settings and unrelated plugins are retained. No personal controls file is bundled.

For removal, close the game and run **Remove.cmd**, choosing the same game directory. Recorded framework files move out of loading paths to BepInEx/framework-removed. Settings, saves, unrelated mods, BepInEx and backups remain. This does not remove the BepInEx loader itself.

For installer rollback, run **Rollback-Install.ps1 -GameRoot "YOUR GAME DIRECTORY"** in Windows PowerShell. It restores previous framework files while retaining current settings. If a later updater transaction changed the files, roll that update back first; do not force mismatched backups.

If installation refuses a changed or duplicate framework DLL, investigate before overwriting. Do not copy individual DLLs over unknown old mod installations. Keep the whole extracted package together: Install.ps1, PackageSupport.ps1, package-manifest.json and payload are required.

Logs: BepInEx/LogOutput.log inside your game directory. Optional Edi-not-running messages indicate that the external Edi service is absent. No Edi engine, scripts or device setup is included.

Updates: use the in-game arrow for approved signed releases; follow the steps below. No private feed, manual DLL replacement or extra installer is needed for the v0.1.0 → v0.1.1 update.

## Gallery defaults and reinstalling

New installs explicitly use normal gallery progression (Gallery / UnlockAll = false). If an older configuration has Unlock All enabled or omitted, the installer asks: choose **N** for normal progression or **K** to preserve your existing unlock preference. Only that configuration value changes, with a verified rollback backup; saves and discoveries are untouched. Do not use the manager's **Reset unlocked** button for this correction: that button clears discoveries.

To test first-install defaults without changing saves, use a separate PNC 0.4.2 folder with no framework configuration in BepInEx/config. Existing Windows gallery discoveries remain visible and are not evidence of Unlock All being enabled. Check Gallery / UnlockAll is false in F11 instead.

## Public update delivery status

The signed public updater is active. A clean GitHub v0.1.0 installation and a genuine signed v0.1.0 → v0.1.1 update passed on a separate PC. The test confirmed the golden arrow, chime and border, published patch notes, download and installation, completion window, Restart Game, successful startup, and “You have the latest approved release.” The same update was not offered again.

v0.1.1 is delivery-only: no gameplay, controls, settings or visual changes. Known gameplay bugs remain. Future offers require an approved signed release and a verified versioned package; ordinary commits and private builds do not trigger notifications. Internet access is required for online checks and downloads. This successful public test does not certify every device, failure or rollback scenario.

### Updating through the game

1. Open the arrow beside the game version on the main menu and choose **Check again** if needed.
2. Read the patch notes, then select **Download Update**.
3. Wait for verification and **UPDATE READY**.
4. Select **Exit Game and Install**. Allow the game to close and the updater to finish.
5. In the completion window, select **Restart Game**, or close it and launch later.
6. Check again after restarting. When current, the message is **“You have the latest approved release.”**

The v0.1.1 `.update.zip` is consumed by the game; it is not the complete first-install package. New players should install the full v0.1.0 ZIP, then update through the game. If a check is unavailable, retry later; an unavailable check does not confirm you are up to date. A newly published offer may take time to appear while GitHub caches refresh.
