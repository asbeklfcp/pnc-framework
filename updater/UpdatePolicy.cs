// Shared by the Unity manager, Windows helper and offline tests. C# 5 / .NET 4.8 compatible.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace PncFramework.Update {
    public sealed class FileSpec {
        public string Name, Hash, Assembly, Version;
        public long Size;
    }
    public sealed class Release {
        public string Id, Channel, Notes, GameHash, PackageHash, PackageUrl;
        public long PackageSize;
        public DateTime ExpiresUtc;
        public List<FileSpec> Files = new List<FileSpec>();
        public Dictionary<string,string> Inventory = new Dictionary<string,string>(StringComparer.Ordinal);
    }
    public static class Policy {
        public const string GameHash = "86b559f74066d0013f32cffd9e38f5a5749c356f5bc29f761c1656ffe39433ce";
        public const long MaxPackage = 32 * 1024 * 1024;
        static readonly HashSet<string> Allowed = new HashSet<string>(StringComparer.Ordinal) {
            "PncModManager.dll", "PncEdi.dll", "PncControls.dll", "PncCustomEnemies.dll"
        };
        public static void Need(bool condition, string reason) { if (!condition) throw new InvalidDataException(reason); }
        public static string Hash(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        public static string HashFile(string path) { using (var s = File.OpenRead(path)) using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(s)).Replace("-", "").ToLowerInvariant(); }
        static XmlDocument Xml(byte[] bytes) {
            Need(bytes.Length <= 256 * 1024, "Manifest is too large");
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 256 * 1024 };
            var doc = new XmlDocument(); doc.XmlResolver = null;
            using (var stream = new MemoryStream(bytes)) using (var reader = XmlReader.Create(stream, settings)) doc.Load(reader);
            return doc;
        }
        static string Attr(XmlElement e, string name) { var s = e.GetAttribute(name); Need(!String.IsNullOrEmpty(s), "Missing " + name); return s; }
        static bool IsHash(string s) {
            if (s.Length != 64) return false;
            foreach (char c in s) if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) return false;
            return true;
        }
        public static byte[] VerifySigned(byte[] envelope,string publicKey) {
            var outer=Xml(envelope).DocumentElement;Need(outer!=null && outer.Name=="signedRelease","Wrong signed envelope");
            var payload=Convert.FromBase64String(outer["payload"].InnerText);var sig=Convert.FromBase64String(outer["signature"].InnerText);
            using(var rsa=RSA.Create()){rsa.FromXmlString(publicKey);Need(rsa.VerifyData(payload,sig,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1),"Publisher signature is invalid");}
            return payload;
        }
        public static Dictionary<string,string> VerifyInstalled(string root,string publicKey,string channel,string installed) {
            string inventory=Path.Combine(root,"BepInEx","framework-updater","baseline-inventory.xml");
            var baseline=Xml(VerifySigned(File.ReadAllBytes(inventory),publicKey)).DocumentElement;
            Need(baseline.Name=="inventory" && Attr(baseline,"channel")==channel && Attr(baseline,"gameHash")==GameHash,"Baseline inventory mismatch");
            string baselineId=Attr(baseline,"id");var expected=new Dictionary<string,string>(StringComparer.Ordinal);
            foreach(XmlElement f in baseline.GetElementsByTagName("file")){string n=Attr(f,"name");Need(Allowed.Contains(n),"Unsafe inventory entry");expected.Add(n,Attr(f,"sha256"));}
            Need(expected.Count==Allowed.Count,"Incomplete baseline inventory");
            if(installed!=baselineId){
                // Receipts are authenticated installed releases, not an offer; expiry only applies to new offers.
                string receipt=Path.Combine(root,"BepInEx","framework-updates","installed-release.xml");
                var e=Xml(VerifySigned(File.ReadAllBytes(receipt),publicKey)).DocumentElement;
                Need(e.Name=="release" && Attr(e,"channel")==channel && Attr(e,"approved")=="true" && Attr(e,"gameHash")==GameHash && Attr(e,"id")==installed,"Installed release receipt mismatch");
                // Each release describes the complete framework inventory, even if its archive changes only a subset.
                foreach(XmlElement f in e["inventory"].GetElementsByTagName("file")){string n=Attr(f,"name");Need(Allowed.Contains(n),"Unsafe release inventory entry");expected[n]=Attr(f,"sha256");}
            }
            CheckUnchanged(root,expected);return expected;
        }
        public static Release Verify(byte[] envelope, string publicKey, string channel, DateTime now) {
            var outer = Xml(envelope).DocumentElement;
            Need(outer != null && outer.Name == "signedRelease", "Wrong envelope");
            var payload = Convert.FromBase64String(outer["payload"].InnerText);
            var sig = Convert.FromBase64String(outer["signature"].InnerText);
            using (var rsa = RSA.Create()) {
                rsa.FromXmlString(publicKey);
                Need(rsa.VerifyData(payload, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1), "Release signature is invalid");
            }
            var e = Xml(payload).DocumentElement;
            Need(e != null && e.Name == "release" && Attr(e,"schema") == "1", "Unsupported manifest schema");
            Need(Attr(e,"approved") == "true", "Release is not approved");
            var r = new Release(); r.Id=Attr(e,"id"); r.Channel=Attr(e,"channel");
            Version version; Need(Version.TryParse(r.Id, out version), "Invalid release version");
            Need((channel == "private" || channel == "stable") && r.Channel == channel, "Release channel mismatch");
            r.GameHash=Attr(e,"gameHash"); Need(r.GameHash == GameHash, "Unsupported game fingerprint");
            r.ExpiresUtc=XmlConvert.ToDateTime(Attr(e,"expiresUtc"), XmlDateTimeSerializationMode.Utc);
            Need(r.ExpiresUtc > now.ToUniversalTime(), "Release manifest has expired");
            var published=XmlConvert.ToDateTime(Attr(e,"publishedUtc"), XmlDateTimeSerializationMode.Utc);
            Need(published <= now.ToUniversalTime().AddMinutes(5) && published < r.ExpiresUtc, "Invalid release dates");
            r.Notes=e["notes"].InnerText; Need(r.Notes.Length <= 16000, "Release notes too long");
            var p=e["package"]; r.PackageHash=Attr(p,"sha256"); r.PackageUrl=Attr(p,"url"); r.PackageSize=Int64.Parse(Attr(p,"bytes"));
            Need(IsHash(r.PackageHash) && r.PackageSize > 0 && r.PackageSize <= MaxPackage, "Invalid package metadata");
            var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (XmlNode node in e["files"].ChildNodes) {
                var f=node as XmlElement; Need(f != null && f.Name == "file", "Unexpected file entry");
                var spec=new FileSpec(); spec.Name=Attr(f,"name"); spec.Hash=Attr(f,"sha256"); spec.Assembly=Attr(f,"assembly"); spec.Version=Attr(f,"version"); spec.Size=Int64.Parse(Attr(f,"bytes"));
                Need(Allowed.Contains(spec.Name) && seen.Add(spec.Name), "Unsafe or duplicate payload path");
                Need(spec.Assembly == Path.GetFileNameWithoutExtension(spec.Name) && IsHash(spec.Hash) && spec.Size > 0 && spec.Size <= MaxPackage, "Invalid file metadata");
                Need(Version.TryParse(spec.Version,out version), "Invalid assembly version"); r.Files.Add(spec);
            }
            Need(r.Files.Count > 0 && r.Files.Count <= Allowed.Count, "Invalid payload count");
            Need(e["inventory"]!=null,"Missing complete release inventory");
            foreach(XmlElement f in e["inventory"].GetElementsByTagName("file")){string n=Attr(f,"name"),hash=Attr(f,"sha256");Need(Allowed.Contains(n) && IsHash(hash),"Unsafe inventory entry");r.Inventory.Add(n,hash);}
            Need(r.Inventory.Count==Allowed.Count,"Incomplete release inventory");
            foreach(var f in r.Files)Need(r.Inventory[f.Name]==f.Hash,"Payload and inventory disagree");return r;
        }
        public static bool IsNewer(Release release, string installed) { return new Version(release.Id) > new Version(installed); }
        public static Uri ValidateUrl(string url, string channel, Uri feed) {
            Uri uri; Need(Uri.TryCreate(url,UriKind.Absolute,out uri), "Invalid update URL");
            Need(String.IsNullOrEmpty(uri.UserInfo) && String.IsNullOrEmpty(uri.Fragment), "Unsafe update URL");
            bool local = channel == "private" && uri.IsLoopback && uri.Scheme == "http";
            Need(uri.Scheme == "https" || local, "Updates require HTTPS (private loopback excepted)");
            if (feed != null) Need((uri.Scheme == feed.Scheme && uri.Host == feed.Host && uri.Port == feed.Port) || (channel=="stable" && IsOfficialFeed(feed) && IsOfficialPackage(uri)), "Package must use the approved feed origin or official release repository");
            return uri;
        }
        public static bool IsOfficialFeed(Uri uri) {
            return uri!=null && uri.AbsoluteUri=="https://raw.githubusercontent.com/asbeklfcp/pnc-framework/main/updates/stable/latest.xml";
        }
        public static bool IsOfficialPackage(Uri uri) {
            if(uri==null || uri.Scheme!="https" || uri.Host!="github.com" || uri.Port!=443 || uri.UserInfo!="" || uri.Query!="" || uri.Fragment!="")return false;
            var parts=uri.AbsolutePath.Trim('/').Split('/');Version version;
            if(parts.Length!=6 || parts[0]!="asbeklfcp" || parts[1]!="pnc-framework" || parts[2]!="releases" || parts[3]!="download" || !parts[4].StartsWith("v",StringComparison.Ordinal) || !Version.TryParse(parts[4].Substring(1),out version))return false;
            return parts[5]=="PNC-Framework-"+parts[4]+"-PNC0.4.2-win64.update.zip";
        }
        public static Uri ValidateGithubRedirect(Uri original,string location) {
            Uri target;Need(IsOfficialPackage(original) && Uri.TryCreate(location,UriKind.Absolute,out target),"Unexpected release redirect");
            target=new Uri(location);
            Need(target.Scheme=="https" && target.Port==443 && target.UserInfo=="" && target.Fragment=="" && (target.Host=="release-assets.githubusercontent.com" || target.Host=="objects.githubusercontent.com"),"Unapproved release asset host");
            return target;
        }
        public static byte[] Download(Uri uri, int maximum, long exact) { return Download(uri,maximum,exact,null,null); }
        public static byte[] Download(Uri uri,int maximum,long exact,Action<long,long> progress,Func<bool> cancelled) {
            Uri original=uri;bool github=IsOfficialPackage(original);int redirects=0;
            while(true) {
                if(cancelled!=null && cancelled())throw new OperationCanceledException("Download cancelled");
                var request=(HttpWebRequest)WebRequest.Create(uri);request.AllowAutoRedirect=false;request.Timeout=15000;request.ReadWriteTimeout=15000;request.UserAgent="PNC-Framework-Updater";
                using(var response=(HttpWebResponse)request.GetResponse()) {
                    int status=(int)response.StatusCode;
                    if(status==301 || status==302 || status==303 || status==307 || status==308) {
                        Need(github && redirects++<2,"Unexpected download redirect");
                        uri=ValidateGithubRedirect(original,response.Headers["Location"]);continue;
                    }
                    Need(response.StatusCode==HttpStatusCode.OK,"Update server returned an unexpected response");
                    Need(response.ContentLength<=maximum,"Download exceeds size limit");
                    using(var input=response.GetResponseStream())using(var output=new MemoryStream()) {
                        var buffer=new byte[32768];int count;
                        while((count=input.Read(buffer,0,buffer.Length))>0){if(cancelled!=null && cancelled())throw new OperationCanceledException("Download cancelled");Need(output.Length+count<=maximum,"Download exceeds size limit");output.Write(buffer,0,count);if(progress!=null)progress(output.Length,exact>=0?exact:response.ContentLength);}
                        Need(exact<0 || output.Length==exact,"Download length mismatch");return output.ToArray();
                    }
                }
            }
        }
        public static void NoLinks(string path) {
            var current = new DirectoryInfo(Path.GetFullPath(path));
            while (current != null) { if(current.Exists) Need((current.Attributes & FileAttributes.ReparsePoint)==0,"Linked directories are not update targets"); current=current.Parent; }
        }
        public static void ValidateGame(string root) {
            NoLinks(root);
            Need(HashFile(Path.Combine(root,"Post Nut Calamity_Data","Managed","Assembly-CSharp.dll")) == GameHash,"Wrong PNC game build");
            NoLinks(Path.Combine(root,"BepInEx","plugins"));
        }
        public static void ValidateAssembly(string path, FileSpec spec) {
            Need((File.GetAttributes(path)&FileAttributes.ReparsePoint)==0,"Linked payload file");
            Need(new FileInfo(path).Length==spec.Size && HashFile(path)==spec.Hash,"Payload file hash/length mismatch");
            var name=AssemblyName.GetAssemblyName(path);
            Need(name.Name==spec.Assembly && name.Version.ToString()==spec.Version,"Payload assembly identity mismatch");
        }
        public static void Extract(byte[] archive, Release release, string staging) {
            Need(archive.LongLength == release.PackageSize && Hash(archive) == release.PackageHash,"Package integrity mismatch");
            Need(!Directory.Exists(staging),"Staging directory already exists"); NoLinks(Path.GetDirectoryName(staging)); Directory.CreateDirectory(staging);
            var specs=new Dictionary<string,FileSpec>(StringComparer.Ordinal); foreach(var f in release.Files)specs.Add(f.Name,f);
            var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using(var bytes=new MemoryStream(archive)) using(var zip=new ZipArchive(bytes,ZipArchiveMode.Read)) {
                foreach(var entry in zip.Entries) {
                    FileSpec spec; Need(specs.TryGetValue(entry.FullName,out spec) && seen.Add(entry.FullName),"Unapproved or duplicate archive entry");
                    Need(entry.Length==spec.Size && (entry.ExternalAttributes & unchecked((int)0xF0000000)) != unchecked((int)0xA0000000),"Invalid archive file");
                    var target=Path.Combine(staging,spec.Name);
                    using(var input=entry.Open()) using(var output=new FileStream(target,FileMode.CreateNew)) {
                        var buffer=new byte[32768];int count;long written=0;
                        while((count=input.Read(buffer,0,buffer.Length))>0){written+=count;Need(written<=spec.Size,"Expanded file exceeds size limit");output.Write(buffer,0,count);}
                    }
                    ValidateAssembly(target,spec);
                }
            }
            Need(seen.Count == specs.Count,"Archive is incomplete");
        }
        static Dictionary<string,string> Snapshot(string root) {
            var result=new Dictionary<string,string>(StringComparer.Ordinal);
            string plugins=Path.Combine(root,"BepInEx","plugins"); NoLinks(plugins);
            foreach(var file in Directory.GetFiles(plugins,"*",SearchOption.TopDirectoryOnly)) {
                Need((File.GetAttributes(file)&FileAttributes.ReparsePoint)==0,"Linked plugin file");
                if(Allowed.Contains(Path.GetFileName(file)))result.Add(Path.GetFileName(file),HashFile(file));
            }
            CheckDuplicates(plugins,result); return result;
        }
        static void CheckDuplicates(string dir, Dictionary<string,string> expected) {
            NoLinks(dir);
            foreach(var child in Directory.GetDirectories(dir))CheckDuplicates(child,expected);
            foreach(var p in Directory.GetFiles(dir,"*.dll")) {
                Need((File.GetAttributes(p)&FileAttributes.ReparsePoint)==0,"Linked plugin file");
                string identity; try{identity=AssemblyName.GetAssemblyName(p).Name;}catch(BadImageFormatException){continue;}
                if(Allowed.Contains(identity+".dll")) {
                    Need(Path.GetFileName(p)==identity+".dll" && expected.ContainsKey(Path.GetFileName(p)) &&
                        Path.GetFullPath(p)==Path.GetFullPath(Path.Combine(_rootPlugins,identity+".dll")),"Duplicate or renamed framework assembly");
                }
            }
        }
        [ThreadStatic] static string _rootPlugins;
        public static Dictionary<string,string> CaptureInstalled(string root) {
            _rootPlugins=Path.Combine(root,"BepInEx","plugins"); try{return Snapshot(root);}finally{_rootPlugins=null;}
        }
        public static void CheckUnchanged(string root, Dictionary<string,string> expected) {
            var actual=CaptureInstalled(root);Need(actual.Count==expected.Count,"Installed framework files changed");
            foreach(var pair in expected)Need(actual.ContainsKey(pair.Key) && actual[pair.Key]==pair.Value,"Installed framework file modified");
        }
        public static byte[] Request(string root,string installed,string channel,byte[] envelope,Dictionary<string,string> hashes) {
            var doc=new XmlDocument();var e=doc.CreateElement("updateRequest");doc.AppendChild(e);
            e.SetAttribute("root",Path.GetFullPath(root));e.SetAttribute("installed",installed);e.SetAttribute("channel",channel);
            var payload=doc.CreateElement("envelope");payload.InnerText=Convert.ToBase64String(envelope);e.AppendChild(payload);
            foreach(var p in hashes){var f=doc.CreateElement("old");f.SetAttribute("name",p.Key);f.SetAttribute("sha256",p.Value);e.AppendChild(f);}
            return Encoding.UTF8.GetBytes(doc.OuterXml);
        }
        public static void ReadRequest(byte[] bytes,out string root,out string installed,out string channel,out byte[] envelope,out Dictionary<string,string> old) {
            var e=Xml(bytes).DocumentElement;Need(e.Name=="updateRequest","Invalid update request");
            root=Attr(e,"root");installed=Attr(e,"installed");channel=Attr(e,"channel");envelope=Convert.FromBase64String(e["envelope"].InnerText);
            old=new Dictionary<string,string>(StringComparer.Ordinal);
            foreach(XmlNode n in e.GetElementsByTagName("old")){var f=(XmlElement)n;string name=Attr(f,"name");Need(Allowed.Contains(name),"Unsafe owned path");old.Add(name,Attr(f,"sha256"));}
            Need(old.ContainsKey("PncModManager.dll"),"Baseline manager is not recorded");
        }
        static XmlDocument Journal(string root,string backup,Release release,Dictionary<string,string> old) {
            var d=new XmlDocument();var e=d.CreateElement("transaction");d.AppendChild(e);e.SetAttribute("root",Path.GetFullPath(root));e.SetAttribute("backup",backup);e.SetAttribute("state","prepared");e.SetAttribute("release",release.Id);
            foreach(var f in release.Files){var n=d.CreateElement("file");n.SetAttribute("name",f.Name);n.SetAttribute("new",f.Hash);n.SetAttribute("old",old.ContainsKey(f.Name)?old[f.Name]:"");e.AppendChild(n);} return d;
        }
        static void AtomicSave(XmlDocument doc,string path) {
            string temp=path+".tmp";if(File.Exists(temp))File.Delete(temp);doc.Save(temp);
            if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);
        }
        public static void Install(string root,Release release,string staging,Dictionary<string,string> old,Func<bool> gameRunning,Action<int> afterWrite,byte[] receipt=null) {
            ValidateGame(root);Need(!gameRunning(),"Close the game before installing");CheckUnchanged(root,old);
            foreach(var pair in rInventory(release)){
                bool changed=release.Files.Exists(f=>f.Name==pair.Key);
                Need(changed || (old.ContainsKey(pair.Key) && old[pair.Key]==pair.Value),"Release requires different companion components");
            }
            foreach(var f in release.Files)ValidateAssembly(Path.Combine(staging,f.Name),f);
            string updates=Path.Combine(root,"BepInEx","framework-updates");Directory.CreateDirectory(updates);NoLinks(updates);
            using(var mutex=new FileStream(Path.Combine(updates,"install.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None)) {
                Need(!gameRunning(),"Close the game before installing");CheckUnchanged(root,old);
                string journal=Path.Combine(updates,"transaction.xml");
                Need(!File.Exists(journal) || Xml(File.ReadAllBytes(journal)).DocumentElement.GetAttribute("state")=="complete" || Xml(File.ReadAllBytes(journal)).DocumentElement.GetAttribute("state")=="rolledback","Unfinished update requires rollback");
                string backup=Path.Combine(updates,"backup-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(backup);
                foreach(var f in release.Files)if(old.ContainsKey(f.Name)){string source=Path.Combine(root,"BepInEx","plugins",f.Name);File.Copy(source,Path.Combine(backup,f.Name));Need(HashFile(Path.Combine(backup,f.Name))==old[f.Name],"Backup failed verification");}
                var tx=Journal(root,backup,release,old);AtomicSave(tx,journal);
                string receiptPath=Path.Combine(updates,"installed-release.xml");
                if(receipt!=null){
                    tx.DocumentElement.SetAttribute("receipt-new",Hash(receipt));
                    tx.DocumentElement.SetAttribute("receipt-old",File.Exists(receiptPath)?HashFile(receiptPath):"");
                    if(File.Exists(receiptPath))File.Copy(receiptPath,Path.Combine(backup,"installed-release.xml"));
                    AtomicSave(tx,journal);
                }
                try {
                    int count=0;
                    foreach(var f in release.Files){Need(!gameRunning(),"Game restarted during update");string target=Path.Combine(root,"BepInEx","plugins",f.Name);NoLinks(Path.GetDirectoryName(target));string temp=target+".pnc-update-"+Guid.NewGuid().ToString("N");File.Copy(Path.Combine(staging,f.Name),temp);Need(HashFile(temp)==f.Hash,"Staged copy hash mismatch");if(File.Exists(target))File.Replace(temp,target,null);else File.Move(temp,target);Need(HashFile(target)==f.Hash,"Installed hash mismatch");if(afterWrite!=null)afterWrite(++count);}
                    if(receipt!=null){string temp=receiptPath+".tmp";File.WriteAllBytes(temp,receipt);if(File.Exists(receiptPath))File.Replace(temp,receiptPath,null);else File.Move(temp,receiptPath);}
                    tx.DocumentElement.SetAttribute("state","complete");AtomicSave(tx,journal);
                } catch { RollbackCore(root,gameRunning);throw; }
            }
        }
        static Dictionary<string,string> rInventory(Release release){Need(release.Inventory.Count==Allowed.Count,"Incomplete release inventory");return release.Inventory;}
        public static void Rollback(string root,Func<bool> gameRunning) {
            ValidateGame(root);string updates=Path.Combine(root,"BepInEx","framework-updates");NoLinks(updates);
            using(var mutex=new FileStream(Path.Combine(updates,"install.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))RollbackCore(root,gameRunning);
        }
        static void RollbackCore(string root,Func<bool> gameRunning) {
            Need(!gameRunning(),"Close the game before rollback");ValidateGame(root);
            string updates=Path.Combine(root,"BepInEx","framework-updates");NoLinks(updates);
            string path=Path.Combine(updates,"transaction.xml");var tx=Xml(File.ReadAllBytes(path));var e=tx.DocumentElement;
            Need(Path.GetFullPath(Attr(e,"root"))==Path.GetFullPath(root),"Rollback game mismatch");
            string backup=Path.GetFullPath(Attr(e,"backup"));
            Need(Path.GetDirectoryName(backup)==Path.GetFullPath(updates) && Path.GetFileName(backup).StartsWith("backup-",StringComparison.Ordinal),"Unsafe backup location");NoLinks(backup);
            var names=new HashSet<string>(StringComparer.Ordinal);
            foreach(XmlElement f in e.GetElementsByTagName("file")){
                string n=Attr(f,"name");Need(Allowed.Contains(n) && names.Add(n),"Unsafe rollback entry");string old=f.GetAttribute("old");string newer=Attr(f,"new");string target=Path.Combine(root,"BepInEx","plugins",n);
                if(File.Exists(target)){Need((File.GetAttributes(target)&FileAttributes.ReparsePoint)==0,"Linked rollback target");string h=HashFile(target);Need(h==old || h==newer,"Modified file requires manual recovery");}
                if(old!="")Need(HashFile(Path.Combine(backup,n))==old,"Backup hash mismatch");
            }
            if(e.HasAttribute("receipt-new")){
                string receiptPath=Path.Combine(updates,"installed-release.xml");string previous=e.GetAttribute("receipt-old");
                if(File.Exists(receiptPath)){Need((File.GetAttributes(receiptPath)&FileAttributes.ReparsePoint)==0,"Linked receipt");string hash=HashFile(receiptPath);Need(hash==previous || hash==e.GetAttribute("receipt-new"),"Installed receipt modified");}
                if(previous!="")Need(HashFile(Path.Combine(backup,"installed-release.xml"))==previous,"Receipt backup mismatch");
            }
            foreach(XmlElement f in e.GetElementsByTagName("file")){
                Need(!gameRunning(),"Game restarted during rollback");string n=f.GetAttribute("name");string target=Path.Combine(root,"BepInEx","plugins",n);
                if(f.GetAttribute("old")!="")File.Copy(Path.Combine(backup,n),target,true);else if(File.Exists(target))File.Delete(target);
            }
            if(e.HasAttribute("receipt-new")){
                string receiptPath=Path.Combine(updates,"installed-release.xml");
                if(e.GetAttribute("receipt-old")!="")File.Copy(Path.Combine(backup,"installed-release.xml"),receiptPath,true);else if(File.Exists(receiptPath))File.Delete(receiptPath);
            }
            e.SetAttribute("state","rolledback");AtomicSave(tx,path);
        }
    }
}

