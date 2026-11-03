using System.Runtime.CompilerServices;

// The custom-enemy plugin is the other half of this codebase rather than a third party: it was
// this assembly until §131, it ships from this repo, and it is built by this project. Handing it
// `internal` access is what let that split happen without turning a hundred members into public
// API - and without inviting anyone else to bind against them, which a public surface would.
//
// PncModManager gets no such grant on purpose: it configures whatever plugins are loaded and
// deliberately knows nothing about this one (§127).
[assembly: InternalsVisibleTo("PncCustomEnemies")]
