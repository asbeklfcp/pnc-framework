using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using BepInEx.Logging;

namespace PncEdi;

public static class FrameworkCompatibility
{
    public const string ExpectedGameHash = "86b559f74066d0013f32cffd9e38f5a5749c356f5bc29f761c1656ffe39433ce";
    public static string Status { get; private set; } = "Not checked";
    internal static bool Verify(ManualLogSource log)
    {
        try
        {
            using var stream = File.OpenRead(typeof(PlayerStats).Assembly.Location);
            using var hash = SHA256.Create();
            string actual = string.Concat(hash.ComputeHash(stream).Select(b => b.ToString("x2")));
            bool known = string.Equals(actual, ExpectedGameHash, StringComparison.Ordinal);
            Status = known ? "Known local 0.4.2 assembly; new-content coverage remains partial" : "Unsupported game assembly " + actual;
            if (known) log.LogInfo("[FRAMEWORK-COMPAT] " + Status);
            else log.LogError("[FRAMEWORK-COMPAT] " + Status + "; integration patches were not installed.");
            return known;
        }
        catch (Exception ex) { Status = "Cannot fingerprint game: " + ex.Message; log.LogError(Status); return false; }
    }
}
