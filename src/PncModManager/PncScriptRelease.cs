using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using BepInEx;
using BepInEx.Logging;

namespace PncModManager
{
    internal static class PncScriptRelease
    {
        private const string ReleaseId="0.1.3";
        private static byte[] ReadResource(string name)
        {
            using(var input=Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if(input==null)throw new InvalidDataException("Signed script update is missing "+name);
                using(var output=new MemoryStream()){input.CopyTo(output);return output.ToArray();}
            }
        }
        private static string Hash(byte[] bytes)
        {
            using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();
        }
        private static void WriteVerified(string path,byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if(File.Exists(path))
            {
                if(Hash(File.ReadAllBytes(path))!=Hash(bytes))
                    throw new InvalidDataException("Different existing EDI update resource: "+Path.GetFileName(path));
                return;
            }
            File.WriteAllBytes(path,bytes);
        }
        internal static void TryApply(ManualLogSource logger)
        {
            try
            {
                string root=Paths.GameRootPath;
                ScriptMergeCore.CheckRoot(root);
                string home=ScriptMergeCore.Home(root);
                string receipt=Path.Combine(home,"script-release-"+ReleaseId+".receipt.txt");
                if(File.Exists(receipt))return;
                if(System.Diagnostics.Process.GetProcessesByName("Edi").Length>0)
                {
                    logger.LogWarning("[EDI UPDATE] Close Edi.exe and restart PNC to finish installing the 0.1.2 scripts.");
                    return;
                }
                byte[] zip=ReadResource("PNC.Public.EDI.Bundle.Zip");
                if(Hash(zip)!=ScriptMergeCore.PackageHash)
                    throw new InvalidDataException("Signed script resource failed SHA-256 verification");
                byte[] aliases=ReadResource("PNC.Public.EDI.AliasAdditions.ini");
                string pkg=Path.Combine(home,"packages",ScriptMergeCore.PackageName);
                string ini=Path.Combine(home,"packages","alias_additions.ini");
                WriteVerified(pkg,zip);
                WriteVerified(ini,aliases);
                var plan=ScriptMergeCore.Analyze(root);
                var result=ScriptMergeCore.Apply(root,plan);
                // A receipt is written only after the safe merge transaction succeeds.
                File.WriteAllText(receipt,
                    "Signed PNC Framework "+ReleaseId+" EDI scripts applied "+DateTime.UtcNow.ToString("u")+"\n"+
                    result.Summary()+"\nBackup: "+result.RunFolder+"\nPending: "+result.PendingFolder+"\n");
                logger.LogInfo("[EDI UPDATE] Installed signed release "+ReleaseId+": "+result.Summary().Replace("\n"," | "));
                if(result.ConflictsRemaining>0)
                    logger.LogWarning("[EDI UPDATE] Kept original user-customized files. Review pending alternatives: "+result.PendingFolder);
            }
            catch(Exception e)
            {
                // The game must remain playable even if scripts cannot be merged.
                logger.LogError("[EDI UPDATE] Script install not completed; original game remains available: "+e);
            }
        }
    }
}
