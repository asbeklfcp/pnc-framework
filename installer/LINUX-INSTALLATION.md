# Linux / Steam Deck installation

Use native Linux Python 3.11+ and OpenSSL (not Windows Python or PowerShell inside Wine).
Close PNC. Keep a clean game backup. Download Install-Linux.py from the official PNC Framework release and run it in Desktop Mode / a terminal:

    python3 Install-Linux.py "/path/to/your/PNC folder"

You can also run without a path and paste the game folder when prompted. The folder must contain Post Nut Calamity.exe and the verified PNC 0.4.2 Windows Mono build. The script downloads the existing official base installer, the pinned BepInEx 6 Unity Mono x64 loader, and the latest approved signed update. Existing framework configurations, unrelated mods and saves are preserved. The tested loader is reused if its required binaries match. Unknown or partial installations are refused rather than overwritten.

For Steam/Proton, if BepInEx is not already configured, set the game's Launch Options once:

    WINEDLLOVERRIDES="winhttp.dll=n,b" %command%

For Wine, configure the same winhttp native/builtin override in the game's Wine prefix. See https://docs.bepinex.dev/master/articles/advanced/steam_interop.html .

Run the same installer again with the game closed to apply a later cumulative signed update. It retains the original signed installed-release.xml receipt, verifies hashes and rejects modified packages. It will refuse a non-cumulative update if the existing inventory cannot supply the unchanged files. Existing Windows updater helper and security checks are unchanged; this does not yet fix the in-game updater under Proton.

If installation stops, read its error. Do not manually overwrite DLLs or delete receipts. Transaction backups and journals are in BepInEx/framework-install-backups. The installer automatically rolls back file changes on ordinary installation failures; interrupted process/power-loss recovery and native uninstall are not yet implemented. Linux installer is a preview pending real Proton testing.