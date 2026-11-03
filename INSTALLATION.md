# Installation — PNC Framework v0.1

1. Use a separate playable copy of **PNC 0.4.2, Windows x64 Unity Mono**. Keep a clean backup.
2. If BepInEx is missing, Install.cmd automatically downloads the tested BepInEx 6 Unity Mono x64 build **6.0.0-be.785** from the official source and verifies its pinned SHA-256 before extracting it. Internet access is required for that download. Compatible existing loaders are left untouched; partial or incompatible installations stop safely.
3. Close that game. Extract the entire framework ZIP into a separate folder.
4. Run **Install.cmd**. At the prompt, paste your own game directory containing **Post Nut Calamity.exe**. Do not paste the executable filename.
5. Launch the game from that directory. Use **F11** or the Mod Manager button.

The installer verifies the game fingerprint and prerequisites, installs files relative to your selected game directory, and creates a rollback backup under BepInEx/framework-install-backups. New installations start with Vanilla, diagnostics off, Overkeks extra activation range0 and optional transformation off. Existing settings and unrelated plugins are retained. No personal controls file is bundled.

For removal, close the game and run **Remove.cmd**, choosing the same game directory. Recorded framework files move out of loading paths to BepInEx/framework-removed. Settings, saves, unrelated mods, BepInEx and backups remain. This does not remove the BepInEx loader itself.

For installer rollback, run **Rollback-Install.ps1 -GameRoot "YOUR GAME DIRECTORY"** in Windows PowerShell. It restores previous framework files while retaining current settings. If a later updater transaction changed the files, roll that update back first; do not force mismatched backups.

If installation refuses a changed or duplicate framework DLL, investigate before overwriting. Do not copy individual DLLs over unknown old mod installations. Keep the whole extracted package together: Install.ps1, PackageSupport.ps1, package-manifest.json and payload are required.

Logs: BepInEx/LogOutput.log inside your game directory. Optional Edi-not-running messages indicate that the external Edi service is absent. No Edi engine, scripts or device setup is included.

Updates: use manual versioned ZIP downloads from GitHub Releases for now. The bundled updater points only to the official HTTPS repository; public automatic delivery has not been verified or activated for this first release. No local/private feed is required for startup.

