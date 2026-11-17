using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Logging;

namespace PncCustomEnemies
{
    // Delivered INSIDE the PncCustomEnemies DLL, authenticated by the existing
    // signed four-component updater. Neither its trust key nor helper changes.
    internal static class SignedCharacterContent
    {
        const string Version = "0.1.8";
        const string ZipName = "PNC.Public.Characters.Bundle.Zip";
        const string ChecksName = "PNC.Public.Characters.SHA256SUMS";
        const string ZipHash = "23f18152bcc527d864b8e029d8f22fbc54dbb209c67eb33f5a63396f7e04b3f0";
        const string ChecksHash = "90511ee1e14a2ed5d790dce072556561c4f6cfcbe238bca5c4882c9acd49a22c";
        static readonly string[] Packages = { "joker-wall", "femboy-witch" };
        // Only exact publisher-reviewed historical bytes may be replaced.
        // Includes the publicly signed v0.1.7 manifests, plus the older
        // approved pre-v0.1.7 package hashes for skip-version upgrades.
        // Unknown code and customized manifests remain untouched.
        static readonly Dictionary<string, HashSet<string>> KnownOld = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase) {
            { "joker-wall/WallPictureTrap.dll", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "8bd621d125723717b26a5b7c83c5f68403e080750b363147ba51a0094b465312" } },
            { "joker-wall/wall-trap.json", new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
                "c7c13ad08c584b3eacd41c4beae399deb1e01d3994d6c93784830cb963c6ad6f",
                "c18d22e61b35967b09d9d32a9483670f44ea1f03aa90e37db0a1b54c3f81c902" } },
            { "femboy-witch/CharmWitch.dll", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "0d72d3629f8029147cc1e6331f275ef855f5f6a95107d2f0c625eaa0a0facaf0" } },
            { "femboy-witch/enemy.json", new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
                "266ad34716c1e9aa77605b14b972702a53b393d8af47c6c3ef71ba0c7da7b78a" } }
        };
        static bool WasApprovedPriorFile(string path, string hash)
        {
            HashSet<string> approved;
            return KnownOld.TryGetValue(path, out approved) && approved.Contains(hash);
        }
        sealed class FileData { internal string Name, Sha; internal byte[] Bytes; }
        sealed class Applied { internal string Path, Backup, Hash; internal bool New; }
        static string Sha(byte[] b) { using (var h=SHA256.Create()) return BitConverter.ToString(h.ComputeHash(b)).Replace("-","").ToLowerInvariant(); }
        static string FileSha(string f) { using (var h=SHA256.Create()) using(var s=File.OpenRead(f)) return BitConverter.ToString(h.ComputeHash(s)).Replace("-","").ToLowerInvariant(); }
        static byte[] ReadResource(string name)
        {
            using(var s=Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if(s==null)throw new InvalidDataException("Missing character release resource");
                using(var b=new MemoryStream()){s.CopyTo(b);return b.ToArray();}
            }
        }
        static void NoLinks(string path)
        {
            string full=Path.GetFullPath(path);
            var file=File.Exists(full)?(FileSystemInfo)new FileInfo(full):new DirectoryInfo(full);
            if(file.Exists && (file.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Linked character path: "+full);
            for(var d=Directory.Exists(full)?new DirectoryInfo(full):Directory.GetParent(full);d!=null;d=d.Parent)
                if(d.Exists && (d.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Linked ancestor: "+d.FullName);
        }
        static string ValidatedPath(string name,out string pack)
        {
            const string prefix="BepInEx/custom-enemies/";
            if(!name.StartsWith(prefix,StringComparison.Ordinal)||name.IndexOf('\\')>=0)throw new InvalidDataException("Unsafe bundled path");
            var parts=name.Substring(prefix.Length).Split('/');
            if(parts.Length<2||parts.Length>6)throw new InvalidDataException("Unexpected bundle folder depth");
            foreach(var part in parts)
                if(part=="."||part==".."||!Regex.IsMatch(part,@"^[A-Za-z0-9][A-Za-z0-9._-]*$",RegexOptions.CultureInvariant))
                    throw new InvalidDataException("Invalid bundle path component");
            pack=parts[0];
            if(pack!="joker-wall"&&pack!="femboy-witch")throw new InvalidDataException("Unapproved character ID");
            return string.Join("/",parts.Skip(1));
        }
        static Dictionary<string,List<FileData>> Verify()
        {
            var zip=ReadResource(ZipName);
            var checks=ReadResource(ChecksName);
            if(zip.Length>32*1024*1024||Sha(zip)!=ZipHash||Sha(checks)!=ChecksHash)
                throw new InvalidDataException("Character content hash is not publisher-approved");
            var expected=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            foreach(var raw in Encoding.UTF8.GetString(checks).Split('\n'))
            {
                string row=raw.TrimEnd('\r');
                if(row.Length==0)continue;
                if(row.Length<68||row.Substring(64,2)!="  ")throw new InvalidDataException("Invalid content checksum row");
                string name=row.Substring(66),pack;
                ValidatedPath(name,out pack);
                string hash=row.Substring(0,64);
                if(!Regex.IsMatch(hash,@"^[a-f0-9]{64}$")||!expected.TryAdd(name,hash))
                    throw new InvalidDataException("Invalid or duplicate checksum row");
            }
            if(expected.Count!=80)throw new InvalidDataException("Incomplete character manifest");
            var result=Packages.ToDictionary(k=>k,k=>new List<FileData>(),StringComparer.OrdinalIgnoreCase);
            var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total=0;
            using(var data=new MemoryStream(zip))
            using(var archive=new ZipArchive(data,ZipArchiveMode.Read,false))
            {
                foreach(var entry in archive.Entries)
                {
                    string pack,rel=ValidatedPath(entry.FullName,out pack);
                    total+=entry.Length;
                    if(entry.Length<1||entry.Length>24*1024*1024||total>64*1024*1024||
                       !seen.Add(entry.FullName)||!expected.ContainsKey(entry.FullName))
                        throw new InvalidDataException("Unapproved character archive entry");
                    byte[] bytes;
                    using(var stream=entry.Open())using(var tmp=new MemoryStream())
                    {stream.CopyTo(tmp);bytes=tmp.ToArray();}
                    if(bytes.LongLength!=entry.Length||Sha(bytes)!=expected[entry.FullName])
                        throw new InvalidDataException("Character file checksum mismatch");
                    result[pack].Add(new FileData{Name=rel,Bytes=bytes,Sha=expected[entry.FullName]});
                }
            }
            if(seen.Count!=expected.Count)throw new InvalidDataException("Missing character archive entry");
            foreach(string id in Packages)
                if(!result[id].Any(x=>x.Name==(id=="joker-wall"?"wall-trap.json":"enemy.json")))
                    throw new InvalidDataException("Missing character package manifest");
            return result;
        }
        static string Home(string root)=>Path.Combine(root,"BepInEx","framework-character-updates");
        static string PackagePath(string root,string id)=>Path.Combine(root,"BepInEx","custom-enemies",id);
        static string Receipt(string root,string id)=>Path.Combine(Home(root),"installed-"+Version+"-"+id+".txt");
        static bool IsCodeOrManifest(string name)=>name.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)||name.EndsWith(".json",StringComparison.OrdinalIgnoreCase);
        static void ApplyOne(string root,string id,List<FileData> files,ManualLogSource log)
        {
            string dest=PackagePath(root,id),receipt=Receipt(root,id);
            NoLinks(dest);NoLinks(receipt);
            if(File.Exists(receipt))return;
            if(Directory.Exists(dest))
            {
                string manifest=id=="joker-wall"?"wall-trap.json":"enemy.json";
                string oldPath=Path.Combine(dest,manifest);
                NoLinks(oldPath);
                if(!File.Exists(oldPath)){log.LogWarning("[CHARACTER UPDATE] Partial/custom "+id+" untouched");return;}
                string actual=FileSha(oldPath);
                if(actual!=files.Single(f=>f.Name==manifest).Sha &&
                    !WasApprovedPriorFile(id+"/"+manifest,actual))
                {log.LogWarning("[CHARACTER UPDATE] Custom manifest in "+id+" untouched");return;}
            }
            var incoming=new List<FileData>();
            int preserved=0;
            foreach(var f in files)
            {
                string path=Path.Combine(dest,f.Name.Replace('/',Path.DirectorySeparatorChar));
                NoLinks(path);
                if(!File.Exists(path)){incoming.Add(f);continue;}
                string actual=FileSha(path);
                if(actual==f.Sha)continue;
                if(WasApprovedPriorFile(id+"/"+f.Name,actual))incoming.Add(f);
                else if(IsCodeOrManifest(f.Name))
                {log.LogWarning("[CHARACTER UPDATE] Unknown code/config in "+id+"; all files preserved");return;}
                else preserved++;
            }
            string home=Home(root);
            Directory.CreateDirectory(home);
            if(incoming.Count==0)
            {
                File.WriteAllText(receipt,"Verified "+id+"; customized files preserved: "+preserved+"\n");
                return;
            }
            string txn=Path.Combine(home,"txn-"+Guid.NewGuid().ToString("N"));
            var applied=new List<Applied>();
            try
            {
                Directory.CreateDirectory(txn);
                NoLinks(txn);
                foreach(var f in incoming)
                {
                    string staged=Path.Combine(txn,"stage",f.Name.Replace('/',Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(staged));
                    File.WriteAllBytes(staged,f.Bytes);
                    if(FileSha(staged)!=f.Sha)throw new InvalidDataException("Character staging hash mismatch");
                }
                if(!Directory.Exists(dest))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    NoLinks(dest);
                    Directory.Move(Path.Combine(txn,"stage"),dest);
                    applied.Add(new Applied{Path=dest,New=true,Hash="directory"});
                }
                else foreach(var f in incoming)
                {
                    string target=Path.Combine(dest,f.Name.Replace('/',Path.DirectorySeparatorChar));
                    NoLinks(target);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    string staged=Path.Combine(txn,"stage",f.Name.Replace('/',Path.DirectorySeparatorChar));
                    if(File.Exists(target))
                    {
                        string actual=FileSha(target);
                        if(!WasApprovedPriorFile(id+"/"+f.Name,actual))
                            throw new InvalidDataException("Character content changed during preparation");
                        string backup=Path.Combine(txn,"backup",f.Name.Replace('/',Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(backup));
                        File.Copy(target,backup);
                        if(FileSha(backup)!=actual)throw new InvalidDataException("Character backup hash mismatch");
                        File.Replace(staged,target,null);
                        applied.Add(new Applied{Path=target,Backup=backup,Hash=f.Sha});
                    }
                    else
                    {
                        File.Move(staged,target);
                        applied.Add(new Applied{Path=target,New=true,Hash=f.Sha});
                    }
                    if(FileSha(target)!=f.Sha)throw new InvalidDataException("Installed character content hash mismatch");
                }
                File.WriteAllText(receipt,"Signed PNC Framework "+Version+" installed "+id+" "+DateTime.UtcNow.ToString("u")+
                    "\nInstalled: "+incoming.Count+"; user-edited assets preserved: "+preserved+"\n");
                log.LogInfo("[CHARACTER UPDATE] "+id+" installed "+incoming.Count+" approved files; preserved "+preserved+" custom assets");
            }
            catch
            {
                for(int i=applied.Count-1;i>=0;i--)
                {
                    var f=applied[i];
                    if(f.New)
                    {
                        if(f.Hash=="directory"){if(Directory.Exists(f.Path))Directory.Delete(f.Path,true);}
                        else if(File.Exists(f.Path)&&FileSha(f.Path)==f.Hash)File.Delete(f.Path);
                    }
                    else if(File.Exists(f.Backup))File.Copy(f.Backup,f.Path,true);
                }
                throw;
            }
            finally{if(Directory.Exists(txn))Directory.Delete(txn,true);}
        }
        internal static void TryApply(ManualLogSource log)
        {
            try
            {
                string root=Paths.GameRootPath;
                if(!File.Exists(Path.Combine(root,"Post Nut Calamity.exe")))throw new InvalidDataException("Unexpected game root");
                NoLinks(root);NoLinks(PackagePath(root,"joker-wall"));NoLinks(PackagePath(root,"femboy-witch"));NoLinks(Home(root));
                if(Packages.All(p=>File.Exists(Receipt(root,p))))return;
                var all=Verify(); // all hashes verified before any game-directory modifications
                foreach(var p in Packages)
                    try{ApplyOne(root,p,all[p],log);}
                    catch(Exception e){log.LogError("[CHARACTER UPDATE] "+p+" failed; changes rolled back: "+e);}
            }
            catch(Exception e){log.LogError("[CHARACTER UPDATE] Refused invalid release: "+e);}
        }
    }
}
