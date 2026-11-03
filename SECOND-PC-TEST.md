# Second-PC updater validation only

Use only on the second PC in a separate PNC 0.4.2 copy with BepInEx and the actual public PNC Framework v0.1.0 ZIP installed. Do not put this kit in the main PC, Testing or Clean. Do not use cloud-synchronized game/save folders; launch the standalone executable, not a cloud-save launcher.

1. First confirm public v0.1.0 starts and F11 works. Close this second-PC game.
2. Extract this kit's BepInEx folder beside that game's Post Nut Calamity.exe. It adds one validation DLL and one opt-in marker, and replaces no framework DLLs or settings.
3. Launch. Within the normal update-check interval (about30seconds), look for the golden arrow and notification. Open notes and confirm release0.1.1 is described as controlled updater-delivery validation, with no gameplay fixes.
4. Click Download update. Confirm progress, then UPDATE READY. Keep screenshots/results for the chat; no boss test is needed.
5. Use Exit Game and Install. Wait for the horizontal desktop parchment. Click Restart Game.
6. Confirm F11 works, the manager version is1.8.12, the arrow is grey after checking, Vanilla remains selected and saved settings/bindings remain. The validation DLL itself serves as an unrelated plugin preservation fixture.
7. Close only this second-PC game when finished. Remove BepInEx/plugins/PncUpdaterValidation.dll and BepInEx/framework-updates/updater-validation-0.1.1.optin. Do not remove any other file. Keep updater backups/logs until acceptance is saved.

This is a REAL signed update of PncModManager.dll to release0.1.1. Core3.0.25 and other gameplay DLLs stay unchanged. The test add-on routes ONLY the two exact metadata requests latest.xml/history.xml to the official repository's updater-validation-0.1.1 branch. It does not fake UI, disable TLS/signatures, alter package URL validation or modify the installer. Package download uses GitHub Releases and its approved asset CDN. The ordinary main/stable feed remains inactive until live acceptance.

No signing secret is included. Do not download or replace the update DLL manually. The in-game updater must perform the installation. If anything fails, stop and report the visible message; do not retry repeatedly or mix packages.
