# PNC Framework v0.1

**For Post Nut Calamity 0.4.2 — Windows x64, Unity Mono**

A community mod framework bringing historical PNC gameplay modifications together with an in-game manager.

> **Early release with known gameplay bugs.** Baphomet, Wolf, Dragon, Wendigo and some high-heat capture conditions have reported issues. Read [Known issues](KNOWN-ISSUES.md) before installing.

## Latest public update — v0.1.3: EDI gallery scripts (clean-install fix)

**v0.1.3 hotfix:** Fresh public installs without an existing Edi/Gallery/Definitions.csv now get that file safely initialized from the signed script bundle.

The signed in-game updater delivers the complete 666-script EDI gallery library: 138 Handy1, 138 Handy2, 195 Handy2 Pro and 195 Detailed files. It safely adds missing script files, 16 new gallery definitions and 178 mapping aliases without overwriting user-customized differences by default. Restart the game after updating, and restart the optional external EDI app if required.

**Early UI notice:** Some interface elements may look unfinished, inconsistent, or buggy. We shipped the tested scripts first rather than delay them for visual polish. The experimental V31 Living Chronicle UI is not part of this release.

Existing installations can use the golden update arrow beside the game version. New installations should first install the full v0.1.0 package, then update. External EDI-device synchronization varies and has not been exhaustively tested. See the [v0.1.3 release](https://github.com/asbeklfcp/pnc-framework/releases/tag/v0.1.3).

## Download and install

Download **PNC-Framework-v0.1.0-PNC0.4.2-win64.zip** from [GitHub Releases](https://github.com/asbeklfcp/pnc-framework/releases/tag/v0.1.0). Extract the complete ZIP to a separate folder. See [Installation](INSTALLATION.md).

Requires your own supported PNC 0.4.2 game. Run Install.cmd and select Post Nut Calamity.exe: if BepInEx is missing, the installer downloads the exact tested BepInEx 6 Unity Mono x64 build (6.0.0-be.785) from the official source and verifies its SHA-256. Compatible existing loaders are retained. Internet access is needed for the loader download. The game, Edi and funscripts are not included; no development tools are required.

## Included

- Five selectable profiles: Vanilla, Pressure And Release, God Mode, Overkeks and Custom (last).
- Parchment-style F11 Mod Manager and configurable framework controls.
- Supported community gameplay integration and native-enemy compatibility changes.
- Custom-content loader and gallery compatibility improvements.
- Independent parchment patch notes and update interface.
- Installer, removal utility and framework rollback backups.

Optional Joker/Witch packages and experimental Archive Passage are not included. Signed public updating and release history are active; the separate-PC v0.1.0 → v0.1.1 acceptance test passed.

See [Credits](CREDITS.md), [Known issues](KNOWN-ISSUES.md), [Release notes](RELEASE-NOTES.md) and [Source build notes](BUILDING.md).

Clean new installs start in Vanilla with default framework controls, diagnostics off and normal gallery progression (Unlock All off). With no previous framework configuration, no gallery-migration question appears. Existing Windows saves and gallery discoveries are retained. Reinstalling an older configuration prompts you to choose normal progression or keep your existing Unlock All preference, without changing saved discoveries.

## Public update delivery status

The signed public updater is active. A clean GitHub v0.1.0 installation and a genuine signed v0.1.0 → v0.1.1 update passed on a separate PC. The test confirmed the golden arrow, chime and border, published patch notes, download and installation, completion window, Restart Game, successful startup, and “You have the latest approved release.” The same update was not offered again.

v0.1.1 is delivery-only: no gameplay, controls, settings or visual changes. Known gameplay bugs remain. Future offers require an approved signed release and a verified versioned package; ordinary commits and private builds do not trigger notifications. Internet access is required for online checks and downloads. This successful public test does not certify every device, failure or rollback scenario.

Use the complete v0.1.0 ZIP for a first installation. The v0.1.1 `.update.zip` is downloaded by the game, not a separate installer. Open the arrow beside the game version → **Check again** → read the notes → **Download Update** → **UPDATE READY** → **Exit Game and Install** → wait for completion → **Restart Game**.
