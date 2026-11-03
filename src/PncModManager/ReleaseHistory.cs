using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Xml;

namespace PncFramework.UpdateUI {
    public sealed class NoteEntry { public string Title,Text,Character,Icon,PortraitResource,PortraitSprite,PortraitCrop; }
    public sealed class NoteCategory { public string Id,Title; public List<NoteEntry> Entries=new List<NoteEntry>(); }
    public sealed class NoteRelease { public string Version,Title;public DateTime Date;public List<NoteCategory> Categories=new List<NoteCategory>(); }
    public sealed class ReleaseHistory {
        public const int MaxRecent=8;
        public string ArchiveUrl;
        public List<NoteRelease> Releases=new List<NoteRelease>();
        static readonly HashSet<string> Categories=new HashSet<string>{"content","characters","character-updates","balance","fixes","framework","compatibility"};
        static string Text(XmlElement node,string attribute,int max) {string s=node.GetAttribute(attribute);if(s.Length>max)throw new InvalidDataException("History field too long");return s;}
        public static ReleaseHistory Parse(byte[] bytes,string channel) {
            if(bytes.Length>256*1024)throw new InvalidDataException("History too large");
            var settings=new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=256*1024};
            var doc=new XmlDocument();doc.XmlResolver=null;
            using(var stream=new MemoryStream(bytes))using(var reader=XmlReader.Create(stream,settings))doc.Load(reader);
            var root=doc.DocumentElement;
            if(root==null || root.Name!="releaseHistory" || root.GetAttribute("schema")!="1" || root.GetAttribute("channel")!=channel || (channel!="stable" && channel!="private"))throw new InvalidDataException("History channel/schema mismatch");
            var result=new ReleaseHistory();result.ArchiveUrl=Text(root,"archiveUrl",2048);
            var ids=new HashSet<string>(StringComparer.Ordinal);
            int count=0;
            foreach(XmlNode n in root.ChildNodes){
                var r=n as XmlElement;if(r==null || r.Name!="release")continue;
                if(++count>64)throw new InvalidDataException("History release limit exceeded");
                if(r.GetAttribute("approved")!="true" || r.GetAttribute("channel")!=channel)continue;
                if(channel=="stable" && r.GetAttribute("kind")!="public-stable")continue;
                Version parsed;string version=Text(r,"version",32);
                if(!Version.TryParse(version,out parsed) || !ids.Add(version))continue;
                DateTime date;if(!DateTime.TryParseExact(r.GetAttribute("date"),"yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out date))continue;
                var release=new NoteRelease{Version=version,Title=Text(r,"title",140),Date=date};int entries=0;
                foreach(XmlNode categoryNode in r.ChildNodes){
                    var c=categoryNode as XmlElement;if(c==null || c.Name!="category" || !Categories.Contains(c.GetAttribute("id")))continue;
                    var category=new NoteCategory{Id=c.GetAttribute("id"),Title=Text(c,"title",80)};
                    foreach(XmlNode entryNode in c.ChildNodes){
                        var e=entryNode as XmlElement;if(e==null || e.Name!="entry")continue;
                        if(++entries>64)throw new InvalidDataException("History entry limit exceeded");
                        var text=e["text"];if(text==null || String.IsNullOrWhiteSpace(text.InnerText))continue;
                        if(text.InnerText.Length>3000)throw new InvalidDataException("History text too long");
                        string character=Text(e,"character",64);
                        if(character.Any(ch=>!(Char.IsLetterOrDigit(ch) || ch=='_' || ch=='-')))throw new InvalidDataException("Unsafe native portrait identifier");
                        var entry=new NoteEntry{Title=Text(e,"title",140),Text=text.InnerText,Character=character,Icon=Text(e,"icon",48)};
                        var portrait=e["portrait"];if(portrait!=null){
                            entry.PortraitResource=Text(portrait,"resource",180);entry.PortraitSprite=Text(portrait,"sprite",128);entry.PortraitCrop=Text(portrait,"crop",100);
                            if(String.IsNullOrWhiteSpace(entry.PortraitSprite) || !entry.PortraitResource.StartsWith("gallerydata/enemygallerydata/",StringComparison.Ordinal) || entry.PortraitResource.Any(ch=>!(Char.IsLetterOrDigit(ch) || ch=='/' || ch=='_')))throw new InvalidDataException("Unsafe native gallery reference");
                            var values=entry.PortraitCrop.Split(',').Select(x=>float.Parse(x,System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                            if(values.Length!=4 || values.Any(x=>float.IsNaN(x) || float.IsInfinity(x)) || values[0]<0 || values[1]<0 || values[2]<=0 || values[3]<=0 || values[0]+values[2]>1.001 || values[1]+values[3]>1.001)throw new InvalidDataException("Unsafe portrait crop");
                        }
                        category.Entries.Add(entry);
                    }
                    if(category.Entries.Count>0)release.Categories.Add(category);
                }
                if(release.Categories.Count>0)result.Releases.Add(release);
            }
            result.Releases=result.Releases.OrderByDescending(r=>r.Date).ThenByDescending(r=>new Version(r.Version)).Take(MaxRecent).ToList();return result;
        }
    }
}
