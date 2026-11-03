// Generic, package-free local sfnt cache writer. No native font data is embedded here.
// C#5/.NET Framework compatible. Alpha bytes use Unity's bottom-up row order.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PncFramework.Typography {
    public sealed class NativeFontGlyph {
        public int Unicode, RectX, RectY, RectW, RectH;
        public float Width, Height, BearingX, BearingY, Advance;
    }
    public sealed class NativeFontFace {
        public float PointSize=75, Ascent=56.25f, Descent=-18.75f, LineHeight=81.73828125f;
        public int UnitsPerEm=1024, PixelCellUnits=64;
    }
    public static class NativeFontRebuilder {
        public const string FamilyName="PNC Native Alkhemikal Cache";
        sealed class GlyphData {
            public byte[] Bytes;public int Advance,Lsb,MinX,MinY,MaxX,MaxY,Points,Contours;
        }
        sealed class Writer {
            public MemoryStream Stream=new MemoryStream();
            public long Length{get{return Stream.Length;}}
            public void U16(int n){Stream.WriteByte((byte)(n>>8));Stream.WriteByte((byte)n);}
            public void U32(uint n){Stream.WriteByte((byte)(n>>24));Stream.WriteByte((byte)(n>>16));Stream.WriteByte((byte)(n>>8));Stream.WriteByte((byte)n);}
            public void Z(int n){for(int i=0;i<n;i++)Stream.WriteByte(0);}
            public void Put(byte[] b){Stream.Write(b,0,b.Length);}
            public byte[] Bytes(){return Stream.ToArray();}
        }
        static void Need(bool ok,string message){if(!ok)throw new InvalidDataException(message);}
        static int Round(double d){return checked((int)Math.Round(d));}
        static uint Checksum(byte[] bytes){uint sum=0;unchecked{for(int i=0;i<bytes.Length;i+=4){uint n=0;for(int j=0;j<4;j++)n=(n<<8)|(i+j<bytes.Length?bytes[i+j]:(uint)0);sum+=n;}}return sum;}
        static void Set32(byte[] b,int at,uint n){b[at]=(byte)(n>>24);b[at+1]=(byte)(n>>16);b[at+2]=(byte)(n>>8);b[at+3]=(byte)n;}
        static int Power2(int value){int n=1;while(n*2<=value)n*=2;return n;}
        static int Log2(int n){int result=0;while(n>1){result++;n>>=1;}return result;}

        public static byte[] Build(IEnumerable<NativeFontGlyph> source,byte[] alpha,int atlasWidth,int atlasHeight,NativeFontFace face) {
            Need(face!=null && face.PointSize>0 && face.UnitsPerEm>=16 && face.UnitsPerEm<=16384 && face.PixelCellUnits>0,"Invalid font face");
            Need(alpha!=null && atlasWidth>0 && atlasHeight>0 && alpha.Length==(long)atlasWidth*atlasHeight,"Invalid atlas");
            var glyphs=new List<NativeFontGlyph>(source);glyphs.Sort((a,b)=>a.Unicode.CompareTo(b.Unicode));
            Need(glyphs.Count>0 && glyphs.Count<4095,"Invalid glyph count");
            var seen=new HashSet<int>();foreach(var g in glyphs){Need(g.Unicode>=0 && g.Unicode<65535 && seen.Add(g.Unicode),"Duplicate/out-of-range codepoint");}
            var built=new List<GlyphData>();built.Add(EmptyGlyph());
            foreach(var g in glyphs)built.Add(MakeGlyph(g,alpha,atlasWidth,atlasHeight,face));
            int maxPoints=0,maxContours=0,minX=0,minY=0,maxX=0,maxY=0,maxAdvance=0,minLsb=0,minRsb=0,maxExtent=0;
            var glyf=new Writer();var loca=new Writer();var hmtx=new Writer();
            foreach(var g in built){
                loca.U32((uint)glyf.Length);glyf.Put(g.Bytes);while(glyf.Length%4!=0)glyf.Z(1);
                hmtx.U16(g.Advance);hmtx.U16(g.Lsb);
                maxPoints=Math.Max(maxPoints,g.Points);maxContours=Math.Max(maxContours,g.Contours);
                minX=Math.Min(minX,g.MinX);minY=Math.Min(minY,g.MinY);maxX=Math.Max(maxX,g.MaxX);maxY=Math.Max(maxY,g.MaxY);
                maxAdvance=Math.Max(maxAdvance,g.Advance);minLsb=Math.Min(minLsb,g.Lsb);minRsb=Math.Min(minRsb,g.Advance-g.Lsb-(g.MaxX-g.MinX));maxExtent=Math.Max(maxExtent,g.Lsb+g.MaxX-g.MinX);
            }
            loca.U32((uint)glyf.Length);
            double scale=face.UnitsPerEm/face.PointSize;
            int ascent=Round(face.Ascent*scale),descent=Round(face.Descent*scale),gap=Round(face.LineHeight*scale-(ascent-descent));
            var head=new Writer();head.U32(0x00010000);head.U32(0x00010000);head.U32(0);head.U32(0x5f0f3cf5);head.U16(3);head.U16(face.UnitsPerEm);head.U32(0);head.U32(2082844800);head.U32(0);head.U32(2082844800);head.U16(minX);head.U16(minY);head.U16(maxX);head.U16(maxY);head.U16(0);head.U16(16);head.U16(2);head.U16(1);head.U16(0);
            var hhea=new Writer();hhea.U32(0x00010000);hhea.U16(ascent);hhea.U16(descent);hhea.U16(gap);hhea.U16(maxAdvance);hhea.U16(minLsb);hhea.U16(minRsb);hhea.U16(maxExtent);hhea.U16(1);hhea.U16(0);hhea.U16(0);hhea.Z(8);hhea.U16(0);hhea.U16(built.Count);
            var maxp=new Writer();maxp.U32(0x00010000);maxp.U16(built.Count);maxp.U16(maxPoints);maxp.U16(maxContours);maxp.U16(0);maxp.U16(0);maxp.U16(2);maxp.Z(16);
            var post=new Writer();post.U32(0x00030000);post.U32(0);post.U16(-153);post.U16(51);post.Z(20);
            var os2=new Writer();os2.U16(0);os2.U16(face.UnitsPerEm/2);os2.U16(400);os2.U16(5);os2.U16(2);
            os2.U16(512);os2.U16(512);os2.U16(0);os2.U16(128);os2.U16(512);os2.U16(512);os2.U16(0);os2.U16(384);os2.U16(51);os2.U16(256);os2.U16(0);
            os2.Z(10);os2.U32(1);os2.U32(0);os2.U32(0);os2.U32(0);os2.Put(Encoding.ASCII.GetBytes("PNC "));os2.U16(0x40);os2.U16(glyphs[0].Unicode);os2.U16(glyphs[glyphs.Count-1].Unicode);os2.U16(ascent);os2.U16(descent);os2.U16(gap);os2.U16(ascent);os2.U16(-descent);
            var tables=new SortedDictionary<string,byte[]>(StringComparer.Ordinal);
            tables.Add("OS/2",os2.Bytes());tables.Add("cmap",MakeCmap(glyphs));tables.Add("glyf",glyf.Bytes());tables.Add("head",head.Bytes());tables.Add("hhea",hhea.Bytes());tables.Add("hmtx",hmtx.Bytes());tables.Add("loca",loca.Bytes());tables.Add("maxp",maxp.Bytes());tables.Add("name",MakeName());tables.Add("post",post.Bytes());
            var output=new Writer();int count=tables.Count,power=Power2(count);output.U32(0x00010000);output.U16(count);output.U16(power*16);output.U16(Log2(power));output.U16(count*16-power*16);
            int offset=12+16*count,headOffset=0;
            foreach(var pair in tables){output.Put(Encoding.ASCII.GetBytes(pair.Key));output.U32(Checksum(pair.Value));output.U32((uint)offset);output.U32((uint)pair.Value.Length);if(pair.Key=="head")headOffset=offset;offset+=(pair.Value.Length+3)&~3;}
            foreach(var pair in tables){output.Put(pair.Value);while(output.Length%4!=0)output.Z(1);}
            byte[] result=output.Bytes();unchecked{Set32(result,headOffset+8,0xb1b0afba-Checksum(result));}return result;
        }
        static GlyphData EmptyGlyph(){var w=new Writer();w.Z(10);return new GlyphData{Bytes=w.Bytes(),Advance=512};}
        static GlyphData MakeGlyph(NativeFontGlyph glyph,byte[] alpha,int atlasW,int atlasH,NativeFontFace face) {
            Need(glyph.RectX>=0 && glyph.RectY>=0 && glyph.RectW>=0 && glyph.RectH>=0 && glyph.RectX+glyph.RectW<=atlasW && glyph.RectY+glyph.RectH<=atlasH,"Glyph outside atlas");
            double scale=face.UnitsPerEm/face.PointSize;int cell=face.PixelCellUnits;
            int w=Round(glyph.Width*scale/cell),h=Round(glyph.Height*scale/cell),left=Round(glyph.BearingX*scale),top=Round(glyph.BearingY*scale);
            Need(w>=0 && h>=0 && w<=512 && h<=512,"Invalid glyph dimensions");
            var xs=new List<int>();var ys=new List<int>();int contours=0;
            for(int y=0;y<h;y++){
                int x=0;
                while(x<w){
                    if(!Pixel(alpha,atlasW,glyph,x,y,w,h)){x++;continue;}
                    int start=x;while(x<w && Pixel(alpha,atlasW,glyph,x,y,w,h))x++;
                    int lo=left+start*cell,hi=left+x*cell,bottom=top-(y+1)*cell;
                    xs.Add(lo);ys.Add(bottom);xs.Add(lo);ys.Add(bottom+cell);xs.Add(hi);ys.Add(bottom+cell);xs.Add(hi);ys.Add(bottom);contours++;
                }
            }
            Need(contours<8192,"Excessive glyph complexity");
            int minX=0,minY=0,maxX=0,maxY=0;
            if(xs.Count>0){minX=maxX=xs[0];minY=maxY=ys[0];foreach(int x in xs){minX=Math.Min(minX,x);maxX=Math.Max(maxX,x);}foreach(int y in ys){minY=Math.Min(minY,y);maxY=Math.Max(maxY,y);}}
            Need(minX>=short.MinValue && maxX<=short.MaxValue && minY>=short.MinValue && maxY<=short.MaxValue,"Glyph coordinate overflow");
            var data=new Writer();data.U16(contours);data.U16(minX);data.U16(minY);data.U16(maxX);data.U16(maxY);
            for(int c=0;c<contours;c++)data.U16(c*4+3);data.U16(0);
            for(int i=0;i<xs.Count;i++)data.Stream.WriteByte(1);
            int previous=0;foreach(int x in xs){data.U16(x-previous);previous=x;}previous=0;foreach(int y in ys){data.U16(y-previous);previous=y;}
            int advance=Round(glyph.Advance*scale);Need(advance>=0 && advance<=65535 && left>=short.MinValue && left<=short.MaxValue,"Invalid horizontal metric");
            return new GlyphData{Bytes=data.Bytes(),Advance=advance,Lsb=left,MinX=minX,MinY=minY,MaxX=maxX,MaxY=maxY,Points=xs.Count,Contours=contours};
        }
        static bool Pixel(byte[] alpha,int width,NativeFontGlyph g,int x,int y,int cellsW,int cellsH) {
            if(g.RectW==0 || g.RectH==0)return false;
            int sampleX=Math.Min(g.RectW-1,(int)((x+.5)*g.RectW/cellsW));int sampleY=Math.Min(g.RectH-1,(int)((y+.5)*g.RectH/cellsH));
            return alpha[(g.RectY+g.RectH-1-sampleY)*width+g.RectX+sampleX]>=128;
        }
        static byte[] MakeCmap(List<NativeFontGlyph> glyphs) {
            var starts=new List<int>();var ends=new List<int>();var deltas=new List<int>();
            for(int i=0;i<glyphs.Count;i++){
                int cp=glyphs[i].Unicode,delta=i+1-cp;
                if(ends.Count>0 && ends[ends.Count-1]+1==cp && deltas[deltas.Count-1]==delta)ends[ends.Count-1]=cp;
                else{starts.Add(cp);ends.Add(cp);deltas.Add(delta);}
            }
            starts.Add(65535);ends.Add(65535);deltas.Add(1);int count=starts.Count,power=Power2(count);
            var sub=new Writer();sub.U16(4);sub.U16(16+8*count);sub.U16(0);sub.U16(count*2);sub.U16(power*2);sub.U16(Log2(power));sub.U16(count*2-power*2);
            foreach(int n in ends)sub.U16(n);sub.U16(0);foreach(int n in starts)sub.U16(n);foreach(int n in deltas)sub.U16(n);sub.Z(count*2);
            var result=new Writer();result.U16(0);result.U16(2);result.U16(0);result.U16(3);result.U32(20);result.U16(3);result.U16(1);result.U32(20);result.Put(sub.Bytes());return result.Bytes();
        }
        static byte[] MakeName() {
            var names=new SortedDictionary<int,string>();names.Add(0,"Locally reconstructed from installed native font atlas. Rights remain with original author.");names.Add(1,FamilyName);names.Add(2,"Regular");names.Add(3,"PNC-local-native-Alkhemikal-cache-v1");names.Add(4,FamilyName);names.Add(5,"Version 1.0");names.Add(6,"PNCNativeAlkhemikalCache");names.Add(13,"Private local runtime cache only. Not for redistribution.");
            var header=new Writer();var strings=new Writer();header.U16(0);header.U16(names.Count);header.U16(6+12*names.Count);
            foreach(var pair in names){byte[] value=Encoding.BigEndianUnicode.GetBytes(pair.Value);header.U16(3);header.U16(1);header.U16(0x409);header.U16(pair.Key);header.U16(value.Length);header.U16((int)strings.Length);strings.Put(value);}header.Put(strings.Bytes());return header.Bytes();
        }
    }
}

