// Reads only fingerprinted native resources; writes only the caller-selected local cache.
// This file embeds offsets/hashes/format knowledge, never original font artwork or outlines.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace PncFramework.Typography {
    public static class NativeFontCache {
        const string GameHash="86b559f74066d0013f32cffd9e38f5a5749c356f5bc29f761c1656ffe39433ce";
        const string ObjectHash="550d62d06c99974a7af6a4d86fbf33fb7886aa003ae879ef17f5a8265f469860";
        const string AlphaHash="61e0ae1eccf167f2f2df3525cef9ae7ad9902a115c8f8fc0653cbe8f7a58b784";
        static string Hash(byte[] b){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(b)).Replace("-","").ToLowerInvariant();}
        static string FileHash(string path){using(var source=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(source)).Replace("-","").ToLowerInvariant();}
        static void Need(bool ok,string message){if(!ok)throw new InvalidDataException(message);}
        static byte[] ReadRange(string path,long offset,int count){
            using(var source=File.OpenRead(path)){
                Need(offset>=0 && offset+count<=source.Length,"Native font range is outside file");source.Position=offset;var bytes=new byte[count];int total=0;
                while(total<count){int read=source.Read(bytes,total,count-total);Need(read>0,"Native font data truncated");total+=read;}return bytes;
            }
        }
        public static byte[] BuildFromInstallation(string gameRoot){
            Need(BitConverter.IsLittleEndian,"Native PNC font reader requires little endian");
            string data=Path.Combine(Path.GetFullPath(gameRoot),"Post Nut Calamity_Data");
            Need(FileHash(Path.Combine(data,"Managed","Assembly-CSharp.dll"))==GameHash,"Unsupported game build for native typography");
            byte[] font=ReadRange(Path.Combine(data,"sharedassets0.assets"),26649600,9680);
            Need(Hash(font)==ObjectHash,"Native Alkhemikal font asset fingerprint mismatch");
            byte[] alpha=ReadRange(Path.Combine(data,"sharedassets0.assets.resS"),2163537760L,262144);
            Need(Hash(alpha)==AlphaHash,"Native Alkhemikal atlas fingerprint mismatch");
            Need(BitConverter.ToInt32(font,372)==97 && BitConverter.ToInt32(font,5420)==97,"Unexpected native font tables");
            var table=new Dictionary<uint,NativeFontGlyph>();
            for(int i=0;i<97;i++){
                int p=376+i*52;uint index=BitConverter.ToUInt32(font,p);
                Need(BitConverter.ToSingle(font,p+40)==1f && BitConverter.ToInt32(font,p+44)==0,"Unexpected native glyph atlas/scale");
                table.Add(index,new NativeFontGlyph{Width=BitConverter.ToSingle(font,p+4),Height=BitConverter.ToSingle(font,p+8),BearingX=BitConverter.ToSingle(font,p+12),BearingY=BitConverter.ToSingle(font,p+16),Advance=BitConverter.ToSingle(font,p+20),RectX=BitConverter.ToInt32(font,p+24),RectY=BitConverter.ToInt32(font,p+28),RectW=BitConverter.ToInt32(font,p+32),RectH=BitConverter.ToInt32(font,p+36)});
            }
            var chars=new List<NativeFontGlyph>();
            for(int i=0;i<97;i++){
                int p=5424+i*16;Need(BitConverter.ToInt32(font,p)==1 && BitConverter.ToSingle(font,p+12)==1f,"Unexpected native character format");
                uint index=BitConverter.ToUInt32(font,p+8);NativeFontGlyph g;Need(table.TryGetValue(index,out g),"Native character references missing glyph");
                chars.Add(new NativeFontGlyph{Unicode=BitConverter.ToInt32(font,p+4),Width=g.Width,Height=g.Height,BearingX=g.BearingX,BearingY=g.BearingY,Advance=g.Advance,RectX=g.RectX,RectY=g.RectY,RectW=g.RectW,RectH=g.RectH});
            }
            return NativeFontRebuilder.Build(chars,alpha,512,512,new NativeFontFace());
        }
        public static bool TryEnsure(string gameRoot,string cachePath,out string error){
            try{
                byte[] bytes=BuildFromInstallation(gameRoot);string expected=Hash(bytes);string target=Path.GetFullPath(cachePath);
                if(File.Exists(target) && FileHash(target)==expected){error=null;return true;}
                string directory=Path.GetDirectoryName(target);Directory.CreateDirectory(directory);
                if(File.Exists(target))Need((File.GetAttributes(target)&FileAttributes.ReparsePoint)==0,"Linked font cache is not a target");
                string temp=target+"."+Guid.NewGuid().ToString("N")+".tmp";
                using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))output.Write(bytes,0,bytes.Length);
                if(File.Exists(target))File.Replace(temp,target,null);else File.Move(temp,target);
                error=null;return true;
            }catch(Exception ex){error=ex.Message;return false;}
        }
    }
}
