# Source build notes

The src directories contain the actual accepted compile inputs for core3.0.25, controls1.0, loader1.2.4 and manager1.8.11. Public release version is0.1.0. Portable project files replace development paths and remove automatic deployment targets. This preparation did not change gameplay source or compile new gameplay fixes.

Install the .NET SDK and provide your own compatible PNC/BepInEx installation. From each project directory, run `dotnet build -c Release -p:GameDir="YOUR GAME DIRECTORY/"`. Include the final slash. Build Controls and PncEdi first; manager/loader references expect their DLLs in that chosen game's BepInEx/plugins directory. Dependencies/game DLLs are not in this repository. Source build on another machine is not independently verified.

The shipped core/controls binary differs from its accepted original only in the CodeView PDB filename field: personal debugger paths were replaced by the bare PDB filename. Executable IL and managed metadata remain byte-identical. Shipped binaries, not a fresh gameplay rebuild, are used for this release. SHA256SUMS covers distribution archives.

Updater helper source/resources are in updater. Stable public trust material is intentionally public; no signing private key is included. The stable feed is not active for automatic delivery in v0.1. No ordinary commit or build publishes updates.
