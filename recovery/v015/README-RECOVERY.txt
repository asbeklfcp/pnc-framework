PNC FRAMEWORK v0.1.5 -> v0.1.6 RECOVERY

This tool is ONLY for users who already installed v0.1.5 and see:
"Update was not prepared: Installed release receipt mismatch"

CAUSE
v0.1.5's updated manager component mistakenly still reports the prior
public v0.1.4 version. The v0.1.5 EDI game plugin IS correctly installed.
That manager may refuse to prepare its next golden-arrow update.

WHAT IT DOES
The helper does not patch the game, replace scripts, or disable security.
It uses your existing, locally installed OFFICIAL signed update policy
to verify the v0.1.5 receipt and safely roll back the most recent update
to v0.1.4. It verifies file hashes and preserves the EDI gallery and saves.
Once the game returns to v0.1.4, the golden arrow can update normally
directly to signed v0.1.6 (or a later published stable release).

HOW TO USE
1. Close the game completely. Do not run any game executables.
2. Double-click Recover-v015.cmd.
3. Select the actual Post Nut Calamity.exe inside the REAL game folder.
   Do not use a Windows junction, shortcut or private developer build.
4. The tool verifies the receipt, trusted files and rollback transaction.
   If anything differs, it stops safely and tells you what is wrong.
5. Once SUCCESS appears, open the game from the real folder.
6. Click golden arrow > Check Again > install v0.1.6.
7. Exit game normally and let the official updater install.
8. Restart the game. No EDI scripts, saves, profiles or unlocks are
   deleted by the recovery.

This is not a new installer or a new signing key. It only calls the
existing trusted, hash-verified update rollback code already installed
with the game. Keep the complete official v0.1.6 release and do not
download recovery tools from random private messages.