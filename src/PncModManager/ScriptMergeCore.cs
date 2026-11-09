using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PncModManager
{
    internal enum FileState { New, Identical, RecognizedPrevious, Conflict }
    internal sealed class IncomingFile
    {
        internal string Relative;
        internal byte[] Data;
        internal string NewHash;
        internal string ExistingHash;
        internal FileState State;
        internal Conflict Conflict;
    }
    internal sealed class RowEntry
    {
        internal string Name;
        internal string Line;
        internal string Original;
        internal Conflict Conflict;
    }
    internal sealed class AliasEntry
    {
        internal string Section;
        internal string Key;
        internal string Incoming;
        internal string Original;
        internal Conflict Conflict;
    }
    internal sealed class Conflict
    {
        internal string Kind;
        internal string Name;
        internal string Detail;
        internal bool UseIncoming;
        internal bool Resolved;
        internal IncomingFile File;
        internal RowEntry Row;
        internal AliasEntry Alias;
    }
    internal sealed class ScanPlan
    {
        internal readonly List<IncomingFile> Files=new List<IncomingFile>();
        internal readonly List<RowEntry> Rows=new List<RowEntry>();
        internal readonly List<AliasEntry> Aliases=new List<AliasEntry>();
        internal readonly List<Conflict> Conflicts=new List<Conflict>();
        internal string DefinitionsSnapshot;
        internal string IncomingDefinitionsCsv;
        internal bool NewDefinitions;
        internal string ConfigSnapshot;
        internal string DefinitionsHash;
        internal string ConfigHash;
        internal bool SafeApplied;
        internal string LastRun="";
        internal string LastReport="";
        internal int FileAdds, FileIdentical, FileKnownOlder, FileConflicts, NewRows, ExistingRows, NewAliases, ExistingAliases;
    }
    internal sealed class ApplyResult
    {
        internal int Added, Updated, Kept, NewRows, NewAliases, Pending, Replaced, ConflictsRemaining;
        internal string RunFolder, PendingFolder;
        internal string Summary()
        {
            return "Installed "+Added+" new scripts  |  Updated "+Updated+
                "\nMerged "+NewRows+" scene definitions and "+NewAliases+" config mappings"+
                "\nKept "+Kept+" existing files  |  Saved "+Pending+" pending conflicts"+
                "\nReplaced "+Replaced+" user-approved conflicts"+
                "\nStill needs review: "+ConflictsRemaining;
        }
    }
    internal static class ScriptMergeCore
    {
        // Full EDI gallery payload authenticated by the signed public manager
        // and independently verified by SHA-256 before transactional merge.
        internal const string PackageName = "PNC-EDI-0.4.2-Gallery-Full-678.zip";
        internal const string PackageHash = "65f74b1f6b9796ce0bc6e0ef49a712b1f7371b97602c75b6beeba5616a2ef021";
        internal const string ZipPrefix = "UPDATE_PROPOSED/Edi/Gallery/";
        internal static readonly string[] Variants={"handy1","handy2","handy2pro","detailed"};
        private static readonly StringComparer Ci=StringComparer.OrdinalIgnoreCase;
        internal static string Home(string game) {return Path.Combine(game,"BepInEx","framework-script-updates");}
        internal static string Gallery(string game) {return Path.Combine(game,"Edi","Gallery");}
        internal static string Config(string game) {return Path.Combine(game,"BepInEx","config","com.edi.pnc.cfg");}
        internal static void CheckRoot(string game)
        {
            string normalized=Path.GetFullPath(game).TrimEnd(Path.DirectorySeparatorChar);
            if(!File.Exists(Path.Combine(normalized,"Post Nut Calamity.exe")))
                throw new InvalidOperationException("Not a PNC game root");
            CheckNoLink(normalized);
        }
        private static void CheckNoLink(string path)
        {
            var full=Path.GetFullPath(path);
            var item=File.Exists(full) ? (FileSystemInfo)new FileInfo(full) : new DirectoryInfo(full);
            if(item.Exists && (item.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new InvalidOperationException("Refusing linked path: "+full);
            var dir=Directory.Exists(full) ? new DirectoryInfo(full) : Directory.GetParent(full);
            while(dir!=null)
            {
                if(dir.Exists && (dir.Attributes&FileAttributes.ReparsePoint)!=0)
                    throw new InvalidOperationException("Refusing linked directory: "+dir.FullName);
                dir=dir.Parent;
            }
        }
        private static string SafePath(string game, string relative)
        {
            string root=Path.GetFullPath(game).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            string full=Path.GetFullPath(Path.Combine(game,relative.Replace('/',Path.DirectorySeparatorChar)));
            if(!full.StartsWith(root,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsafe path outside PNC game: "+relative);
            CheckNoLink(full);
            return full;
        }
        private static string Digest(byte[] data)
        {
            using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-","").ToLowerInvariant();
        }
        private static string DigestFile(string file){return File.Exists(file)?Digest(File.ReadAllBytes(file)):null;}
        private static byte[] Utf8(string s){return new UTF8Encoding(false).GetBytes(s);}
        private static string Decode(byte[] data)
        {
            return new UTF8Encoding(false,false).GetString(data).TrimStart('\uFEFF');
        }
        private static string[] Lines(string s){return s.Replace("\r\n","\n").Replace("\r","\n").Split('\n');}
        private static string RowName(string line)
        {
            int comma=line.IndexOf(',');
            return comma>0?line.Substring(0,comma).Trim().Trim('"'):"";
        }
        private static Dictionary<string,string> Rows(string contents)
        {
            var found=new Dictionary<string,string>(Ci);
            foreach(var line in Lines(contents).Skip(1))
            {
                if(String.IsNullOrWhiteSpace(line)) continue;
                string name=RowName(line);
                if(name.Length==0) throw new InvalidDataException("Invalid Definitions row");
                if(found.ContainsKey(name))throw new InvalidDataException("Duplicate Definitions name: "+name);
                found.Add(name,line);
            }
            return found;
        }
        private static Dictionary<string,Dictionary<string,string>> ParseIni(string s)
        {
            var all=new Dictionary<string,Dictionary<string,string>>(Ci);
            string section="";
            foreach(var line in Lines(s))
            {
                string raw=line.Trim();
                if(raw.StartsWith("[")&&raw.EndsWith("]")) { section=raw.Substring(1,raw.Length-2);if(!all.ContainsKey(section))all[section]=new Dictionary<string,string>(Ci);continue;}
                int eq=raw.IndexOf('=');
                if(eq<1||section.Length==0||raw.StartsWith("#"))continue;
                string key=raw.Substring(0,eq).Trim(),val=raw.Substring(eq+1).Trim();
                if(all[section].ContainsKey(key))throw new InvalidDataException("Duplicate EDI alias: "+section+"/"+key);
                all[section][key]=val;
            }
            return all;
        }
        private static Dictionary<string,Dictionary<string,string>> ParseLiveConfig(string s)
        {
            var result=new Dictionary<string,Dictionary<string,string>>(Ci);
            var keys=new[]{"EnemyRemap","GalleryAliases","PeekClipMap","DioramaAmbientMap","InGameAliases"};
            foreach(var section in keys)
            {
                var m=Regex.Match(s,@"(?m)^"+Regex.Escape(section)+@"[ \t]*=[ \t]*([^\r\n]*)");
                // InGameAliases was introduced after the original public profile.
                // Older player configs without this optional key still upgrade safely.
                if(!m.Success)
                {
                    if(Ci.Equals(section,"InGameAliases"))
                    {
                        result.Add(section,new Dictionary<string,string>(Ci));
                        continue;
                    }
                    throw new InvalidDataException("Live config missing "+section);
                }
                var dict=new Dictionary<string,string>(Ci);
                foreach(var piece in m.Groups[1].Value.Split(';'))
                {
                    int eq=piece.IndexOf('=');
                    if(eq<1)continue;
                    string k=piece.Substring(0,eq).Trim(),v=piece.Substring(eq+1).Trim();
                    if(dict.ContainsKey(k) && !Ci.Equals(dict[k],v))
                        throw new InvalidDataException("Duplicated different alias: "+section+"/"+k);
                    dict[k]=v;
                }
                result.Add(section,dict);
            }
            return result;
        }
        // The previous signed EDI release mapped the real Thread Mistress loop
        // to a one-shot 'start' track. Only this exact previous default may be
        // migrated automatically; unknown or personalized mappings stay conflicts.
        private static bool IsKnownPreviousAlias(string section,string key,string oldValue,string newValue)
        {
            return Ci.Equals(section,"PeekClipMap")
                && Ci.Equals(key,"thread mistress grabscreen loop")
                && Ci.Equals(oldValue,"thread_mistress_start")
                && Ci.Equals(newValue,"thread_mistress_loop");
        }
        private static Dictionary<string,string> ManagedIndex(string game)
        {
            var res=new Dictionary<string,string>(Ci);
            string p=Path.Combine(Home(game),"managed-hashes.tsv");
            if(!File.Exists(p))return res;
            foreach(var line in File.ReadAllLines(p))
            {
                int tab=line.IndexOf('\t');
                if(tab>0)res[line.Substring(0,tab)]=line.Substring(tab+1).Trim();
            }
            return res;
        }
        internal static ScanPlan Analyze(string game)
        {
            CheckRoot(game);
            string home=Home(game);
            string package=Path.Combine(home,"packages",PackageName);
            if(!File.Exists(package)||DigestFile(package)!=PackageHash)
                throw new InvalidDataException("EDI script package hash mismatch (nothing changed)");
            var plan=new ScanPlan();
            var managed=ManagedIndex(game);
            string pendingFolder=Path.Combine(home,"work");
            Directory.CreateDirectory(pendingFolder);
            string copy=Path.Combine(pendingFolder,"staged-package.zip");
            File.Copy(package,copy,true); // Staging from a verified signed assembly resource
            if(DigestFile(copy)!=PackageHash)throw new InvalidDataException("Staged package failed checksum");
            var seen=new HashSet<string>(Ci);
            string incomingCsv=null;
            long expanded=0;
            using(var stream=File.OpenRead(copy))
            using(var archive=new ZipArchive(stream,ZipArchiveMode.Read))
            {
                foreach(var entry in archive.Entries)
                {
                    if(!entry.FullName.StartsWith(ZipPrefix,StringComparison.Ordinal))continue;
                    string name=entry.FullName.Substring(ZipPrefix.Length);
                    if(name.Length==0||name.EndsWith("/"))continue;
                    var parts=name.Split('/');
                    bool def=name=="Definitions.csv";
                    bool script=parts.Length==2 && Variants.Contains(parts[0],Ci)
                        && Regex.IsMatch(parts[1],@"^[A-Za-z0-9_.\-]+\.funscript$",RegexOptions.IgnoreCase);
                    if(!def&&!script) throw new InvalidDataException("Unexpected script ZIP entry: "+name);
                    if(!seen.Add(name))throw new InvalidDataException("Duplicate payload path: "+name);
                    if(entry.Length>1024*1024 || entry.Length<1)throw new InvalidDataException("Invalid entry size");
                    expanded+=entry.Length;
                    if(expanded>16*1024*1024)throw new InvalidDataException("EDI payload exceeds limit");
                    byte[] data;
                    using(var input=entry.Open())using(var dst=new MemoryStream())
                    {input.CopyTo(dst);data=dst.ToArray();}
                    if(data.LongLength!=entry.Length)throw new InvalidDataException("Unexpected truncated script");
                    if(def){incomingCsv=Decode(data);continue;}
                    string rel="Edi/Gallery/"+name;
                    string target=SafePath(game,rel);
                    var file=new IncomingFile{Relative=rel,Data=data,NewHash=Digest(data),ExistingHash=DigestFile(target)};
                    if(file.ExistingHash==null){file.State=FileState.New;plan.FileAdds++;}
                    else if(file.ExistingHash==file.NewHash){file.State=FileState.Identical;plan.FileIdentical++;}
                    else if(managed.ContainsKey(rel) && Ci.Equals(managed[rel],file.ExistingHash))
                    {file.State=FileState.RecognizedPrevious;plan.FileKnownOlder++;}
                    else
                    {
                        file.State=FileState.Conflict;plan.FileConflicts++;
                        var c=new Conflict { Kind="Script",Name=name,Detail="Same filename, different contents; current version is unknown or customized", File=file };
                        file.Conflict=c;plan.Conflicts.Add(c);
                    }
                    plan.Files.Add(file);
                }
            }
            if(incomingCsv==null||plan.Files.Count!=678)
                throw new InvalidDataException("Unexpected EDI script package: "+plan.Files.Count+" scripts");
            string defs=SafePath(game,"Edi/Gallery/Definitions.csv");
            string cfg=SafePath(game,"BepInEx/config/com.edi.pnc.cfg");
            // A new public PNC installation may have the EDI plugin/config but no
            // external Edi/Gallery directory yet. Seed Definitions.csv from the
            // signed, checksum-validated 666-script resource on first apply.
            if(!File.Exists(cfg))throw new InvalidDataException("Missing live EDI config");
            plan.NewDefinitions=!File.Exists(defs);
            plan.IncomingDefinitionsCsv=incomingCsv;
            plan.DefinitionsSnapshot=plan.NewDefinitions?"":Decode(File.ReadAllBytes(defs));
            plan.ConfigSnapshot=Decode(File.ReadAllBytes(cfg));
            plan.DefinitionsHash=DigestFile(defs);
            plan.ConfigHash=DigestFile(cfg);
            var existingRows=Rows(plan.DefinitionsSnapshot);
            foreach(var kv in Rows(incomingCsv))
            {
                if(existingRows.TryGetValue(kv.Key,out string current))
                {
                    if(String.Equals(kv.Value.Trim(),current.Trim(),StringComparison.OrdinalIgnoreCase))
                        plan.ExistingRows++;
                    else
                    {
                        var r=new RowEntry{Name=kv.Key,Line=kv.Value,Original=current};
                        var c=new Conflict{Kind="Definition",Name=kv.Key,Detail="Existing Definitions.csv row differs; keeping its mapping by default",Row=r};
                        r.Conflict=c;plan.Rows.Add(r);plan.Conflicts.Add(c);
                    }
                }
                else{plan.Rows.Add(new RowEntry{Name=kv.Key,Line=kv.Value});plan.NewRows++;}
            }
            string aliases=Path.Combine(home,"packages","alias_additions_v014.ini");
            if(!File.Exists(aliases))throw new InvalidDataException("Missing private alias manifest");
            var requested=ParseIni(File.ReadAllText(aliases,Encoding.UTF8));
            var currentConfig=ParseLiveConfig(plan.ConfigSnapshot);
            foreach(var sec in requested)
            {
                if(!currentConfig.TryGetValue(sec.Key,out var old))throw new InvalidDataException("Unrecognized alias section "+sec.Key);
                foreach(var kv in sec.Value)
                {
                    if(old.TryGetValue(kv.Key,out string value))
                    {
                        if(Ci.Equals(value,kv.Value))plan.ExistingAliases++;
                        else if(IsKnownPreviousAlias(sec.Key,kv.Key,value,kv.Value))
                        {
                            // Exact old public mapping, not a user-customized value.
                            // Upgrade this one known default without a false conflict.
                            plan.Aliases.Add(new AliasEntry{Section=sec.Key,Key=kv.Key,Incoming=kv.Value,Original=value});
                            plan.NewAliases++;
                        }
                        else
                        {
                            var a=new AliasEntry{Section=sec.Key,Key=kv.Key,Incoming=kv.Value,Original=value};
                            var c=new Conflict{Kind="Mapping",Name=sec.Key+"/"+kv.Key,Detail="Existing config maps this key differently",Alias=a};
                            a.Conflict=c;plan.Aliases.Add(a);plan.Conflicts.Add(c);
                        }
                    }
                    else{plan.Aliases.Add(new AliasEntry{Section=sec.Key,Key=kv.Key,Incoming=kv.Value});plan.NewAliases++;}
                }
            }
            plan.Conflicts.Sort((a,b)=>String.Compare(a.Kind+"/"+a.Name,b.Kind+"/"+b.Name,StringComparison.OrdinalIgnoreCase));
            return plan;
        }
        private static string ReplaceConfigValue(string original,string sec,string key,string val)
        {
            var m=Regex.Match(original,@"(?m)^"+Regex.Escape(sec)+@"[ \t]*=[ \t]*([^\r\n]*)");
            if(!m.Success)
            {
                // Optional section in legacy installs: append it, never rewrite
                // Profile, Debug or any other private game settings.
                if(!Ci.Equals(sec,"InGameAliases"))
                    throw new InvalidDataException("Live config missing "+sec);
                return original.TrimEnd('\r','\n')+Environment.NewLine+sec+" = "+key+"="+val+Environment.NewLine;
            }
            string value=m.Groups[1].Value;
            var pieces=value.Split(';').Where(x=>!String.IsNullOrWhiteSpace(x)).ToList();
            bool found=false;
            for(int i=0;i<pieces.Count;i++)
            {
                int eq=pieces[i].IndexOf('=');
                if(eq<1)continue;
                if(Ci.Equals(pieces[i].Substring(0,eq).Trim(),key))
                {
                    pieces[i]=key+"="+val;found=true;break;
                }
            }
            if(!found)pieces.Add(key+"="+val);
            string replaced=String.Join(";",pieces);
            return original.Substring(0,m.Groups[1].Index)+replaced+original.Substring(m.Groups[1].Index+m.Groups[1].Length);
        }
        private static string MergeCsv(string source,IEnumerable<RowEntry> entries)
        {
            var dictionary=Rows(source);
            var append=new List<string>();
            foreach(var x in entries)
            {
                if(x.Conflict!=null && !x.Conflict.UseIncoming)continue;
                if(dictionary.ContainsKey(x.Name))
                {
                    string current=dictionary[x.Name];
                    if(x.Conflict==null)continue;
                    // Patch only the exact row selected, retaining all other custom/user rows.
                    var lines=Lines(source).ToList();
                    for(int i=1;i<lines.Count;i++)
                    {
                        if(Ci.Equals(RowName(lines[i]),x.Name)){lines[i]=x.Line;break;}
                    }
                    source=String.Join("\r\n",lines);
                }
                else{append.Add(x.Line);dictionary.Add(x.Name,x.Line);}
            }
            if(append.Count>0)
            {
                source=source.TrimEnd('\r','\n')+"\r\n"+String.Join("\r\n",append)+"\r\n";
            }
            return source;
        }
        internal static ApplyResult Apply(string game,ScanPlan plan,bool onlySelectedConflicts=false)
        {
            CheckRoot(game);
            // Do not modify script files while the optional EDI desktop app might be using them.
            if(System.Diagnostics.Process.GetProcessesByName("Edi").Length>0)
                throw new InvalidOperationException("Please close the separate EDI app before applying script updates, then retry");
            if(plan==null)throw new InvalidOperationException("Analyze before applying");
            if(onlySelectedConflicts&&!plan.SafeApplied)throw new InvalidOperationException("Install safe changes first");
            if(!onlySelectedConflicts&&plan.SafeApplied)throw new InvalidOperationException("Safe changes already applied");
            string home=Home(game);
            string id=DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N").Substring(0,6);
            string tx=Path.Combine(home,"backups",id);
            string pending=Path.Combine(home,"pending",id);
            Directory.CreateDirectory(tx);
            Directory.CreateDirectory(pending);
            var result=new ApplyResult{RunFolder=tx,PendingFolder=pending};
            var applied=new List<Tuple<string,byte[]>>();
            var updatedManaged=ManagedIndex(game);
            try
            {
                foreach(var item in plan.Files)
                {
                    if(onlySelectedConflicts && item.State!=FileState.Conflict)continue;
                    if(item.State==FileState.Identical){if(!onlySelectedConflicts)result.Kept++;continue;}
                    if(item.State==FileState.Conflict && (item.Conflict.Resolved||!item.Conflict.UseIncoming))
                    {
                        if(!item.Conflict.Resolved)
                        {
                            // Flat, hashed backup names remain safe on deeply nested Windows installs.
                            string stash=Path.Combine(pending,Digest(Utf8(item.Relative)).Substring(0,24)+".pending");
                            File.WriteAllBytes(stash,item.Data);
                            File.AppendAllText(Path.Combine(pending,"FILES.tsv"),
                                item.Relative+"\t"+Path.GetFileName(stash)+Environment.NewLine,Encoding.UTF8);
                            result.Pending++;
                        }
                        continue;
                    }
                    string dest=SafePath(game,item.Relative);
                    string oldHash=DigestFile(dest);
                    if(!String.Equals(oldHash,item.ExistingHash,StringComparison.OrdinalIgnoreCase))
                        throw new IOException("File changed since scan: "+item.Relative+"; no overwrite");
                    byte[] oldBytes=File.Exists(dest)?File.ReadAllBytes(dest):null;
                    WriteActive(game,tx,item.Relative,item.Data,oldBytes,applied);
                    updatedManaged[item.Relative]=item.NewHash;
                    if(oldBytes==null)result.Added++;else result.Updated++;
                    if(item.State==FileState.Conflict){result.Replaced++;item.Conflict.Resolved=true;}
                }
                if(!onlySelectedConflicts)
                {
                    foreach(var c in plan.Conflicts.Where(x=>x.Kind=="Definition"&&!x.UseIncoming))
                    {
                        File.WriteAllText(Path.Combine(pending,"Definitions_held_rows.txt"),
                            String.Join(Environment.NewLine,plan.Conflicts.Where(x=>x.Kind=="Definition"&&!x.UseIncoming).Select(x=>x.Row.Line)),
                            Encoding.UTF8);
                        break;
                    }
                    foreach(var c in plan.Conflicts.Where(x=>x.Kind=="Mapping"&&!x.UseIncoming))
                    {
                        File.WriteAllText(Path.Combine(pending,"Mappings_held.txt"),
                            String.Join(Environment.NewLine,plan.Conflicts.Where(x=>x.Kind=="Mapping"&&!x.UseIncoming).Select(x=>x.Name+" -> "+x.Alias.Incoming)),
                            Encoding.UTF8);
                        break;
                    }
                }
                var rowEntries=onlySelectedConflicts?plan.Rows.Where(x=>x.Conflict!=null&&x.Conflict.UseIncoming&&!x.Conflict.Resolved).ToList():plan.Rows;
                var aliasEntries=onlySelectedConflicts?plan.Aliases.Where(x=>x.Conflict!=null&&x.Conflict.UseIncoming&&!x.Conflict.Resolved).ToList():plan.Aliases;
                if(rowEntries.Count>0 || (!onlySelectedConflicts && plan.NewDefinitions))
                {
                    string rel="Edi/Gallery/Definitions.csv";
                    string dest=SafePath(game,rel);
                    byte[] oldData=File.Exists(dest)?File.ReadAllBytes(dest):null;
                    string oldText=oldData==null?"":Decode(oldData);
                    string newText=oldData==null?plan.IncomingDefinitionsCsv:MergeCsv(oldText,rowEntries);
                    if(oldText!=newText)
                    {
                        if(!onlySelectedConflicts && DigestFile(dest)!=plan.DefinitionsHash)
                            throw new IOException("Definitions changed since scan; retry");
                        WriteActive(game,tx,rel,Utf8("\uFEFF"+newText),oldData,applied);
                        result.NewRows=onlySelectedConflicts?rowEntries.Count:plan.NewRows;
                    }
                    foreach(var entry in rowEntries.Where(x=>x.Conflict!=null&&x.Conflict.UseIncoming))entry.Conflict.Resolved=true;
                }
                if(aliasEntries.Count>0)
                {
                    string rel="BepInEx/config/com.edi.pnc.cfg";
                    string dest=SafePath(game,rel);
                    byte[] existing=File.ReadAllBytes(dest);
                    string before=Decode(existing),after=before;
                    if(!onlySelectedConflicts && Digest(existing)!=plan.ConfigHash)
                        throw new IOException("EDI config changed since scan; retry");
                    foreach(var a in aliasEntries)
                    {
                        if(a.Conflict!=null&&!a.Conflict.UseIncoming)continue;
                        after=ReplaceConfigValue(after,a.Section,a.Key,a.Incoming);
                        if(a.Conflict!=null) a.Conflict.Resolved=true;
                    }
                    if(after!=before)
                    {
                        WriteActive(game,tx,rel,Utf8("\uFEFF"+after),existing,applied);
                        result.NewAliases=onlySelectedConflicts?aliasEntries.Count:plan.NewAliases;
                    }
                }
                if(!onlySelectedConflicts)plan.SafeApplied=true;
                result.ConflictsRemaining=plan.Conflicts.Count(c=>!c.Resolved);
                var index=new StringBuilder();
                foreach(var kv in updatedManaged.OrderBy(x=>x.Key))index.Append(kv.Key).Append('\t').Append(kv.Value).Append('\n');
                string indexRel="BepInEx/framework-script-updates/managed-hashes.tsv";
                string indexPath=SafePath(game,indexRel);
                WriteActive(game,tx,indexRel,Utf8(index.ToString()),File.Exists(indexPath)?File.ReadAllBytes(indexPath):null,applied);
                var log=new StringBuilder();
                log.AppendLine("PNC EDI GALLERY SCRIPT RELEASE 0.1.4 - "+DateTime.UtcNow.ToString("u"));
                log.AppendLine("Game: "+game);
                log.AppendLine(result.Summary());
                log.AppendLine("Pending alternative scripts: "+pending);
                log.AppendLine("Backup of modified files: "+tx);
                log.AppendLine("Restart the separate EDI app to reload gallery scripts.");
                foreach(var c in plan.Conflicts)log.AppendLine(c.Kind+" | "+c.Name+" | "+(c.Resolved?"replaced":"original kept; incoming pending"));
                string report=Path.Combine(tx,"REPORT.txt");
                File.WriteAllText(report,log.ToString(),Encoding.UTF8);
                plan.LastRun=id;plan.LastReport=log.ToString();
                return result;
            }
            catch(Exception ex)
            {
                // Roll back every file this run changed, in reverse order.
                for(int i=applied.Count-1;i>=0;i--)
                {
                    var item=applied[i];
                    try
                    {
                        if(item.Item2==null){if(File.Exists(item.Item1))File.Delete(item.Item1);}
                        else{File.WriteAllBytes(item.Item1,item.Item2);}
                    }
                    catch(Exception undo)
                    {File.AppendAllText(Path.Combine(tx,"ROLLBACK_ERRORS.txt"),item.Item1+" - "+undo+Environment.NewLine);}
                }
                File.WriteAllText(Path.Combine(tx,"FAILED.txt"),ex.ToString(),Encoding.UTF8);
                throw;
            }
        }
        private static void WriteActive(string game,string tx,string relative,byte[] newData,byte[] oldData,List<Tuple<string,byte[]>> applied)
        {
            string dest=SafePath(game,relative);
            // A flat hash-based backup prevents Windows MAX_PATH failures without losing mapping.
            string backup=Path.Combine(tx,Digest(Utf8(relative)).Substring(0,24)+".bak");
            if(oldData!=null)File.WriteAllBytes(backup,oldData);
            else File.WriteAllText(backup+".new","No original existed",Encoding.UTF8);
            File.AppendAllText(Path.Combine(tx,"FILE_MAP.tsv"),
                relative+"\t"+Path.GetFileName(backup)+(oldData==null?".new":"")+Environment.NewLine,Encoding.UTF8);
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            // Temp file lives under the short transaction directory, not at a long script path.
            string temp=Path.Combine(tx,Guid.NewGuid().ToString("N")+".tmp");
            File.WriteAllBytes(temp,newData);
            try
            {
                if(oldData==null)File.Move(temp,dest);
                else File.Replace(temp,dest,null);
                applied.Add(Tuple.Create(dest,oldData));
            }
            finally {if(File.Exists(temp))File.Delete(temp);}
        }
    }
}
