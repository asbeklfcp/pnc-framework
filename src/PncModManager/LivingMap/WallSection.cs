using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace PncPrivateLivingMap
{
    // Clip source triangles in world height while preserving local positions, UVs and source colors.
    internal static class WallSection
    {
        struct Vertex
        {
            internal Vector3 Position;
            internal Vector2 UV;
            internal Color Tint;
            internal float Height;
            internal static Vertex Lerp(Vertex a,Vertex b,float h)
            {
                float t=(h-a.Height)/(b.Height-a.Height);
                return new Vertex{Position=Vector3.Lerp(a.Position,b.Position,t),UV=Vector2.Lerp(a.UV,b.UV,t),Tint=Color.Lerp(a.Tint,b.Tint,t),Height=h};
            }
        }
        static void Clip(List<Vertex> input,List<Vertex> output,float h,bool above)
        {
            output.Clear();
            if(input.Count==0)return;
            var prev=input[input.Count-1];
            bool was=above?prev.Height>=h:prev.Height<=h;
            foreach(var next in input)
            {
                bool inside=above?next.Height>=h:next.Height<=h;
                if(was!=inside)output.Add(Vertex.Lerp(prev,next,h));
                if(inside)output.Add(next);
                prev=next;was=inside;
            }
        }
        internal static Mesh Build(Mesh source,Matrix4x4 matrix,float low,float high)
        {
            var pos=source.vertices;var uv=source.uv;var colors=source.colors32;
            var verts=new Vertex[pos.Length];
            for(int i=0;i<pos.Length;i++)verts[i]=new Vertex{Position=pos[i],UV=uv.Length==pos.Length?uv[i]:Vector2.zero,Tint=colors.Length==pos.Length?(Color)colors[i]:Color.white,Height=matrix.MultiplyPoint3x4(pos[i]).y};
            var points=new List<Vector3>();var tex=new List<Vector2>();var tint=new List<Color32>();
            var subs=new List<int>[source.subMeshCount];
            var a=new List<Vertex>(6);var b=new List<Vertex>(6);
            for(int j=0;j<subs.Length;j++)
            {
                var indices=source.GetTriangles(j);var output=subs[j]=new List<int>();
                for(int i=0;i+2<indices.Length;i+=3)
                {
                    a.Clear();a.Add(verts[indices[i]]);a.Add(verts[indices[i+1]]);a.Add(verts[indices[i+2]]);
                    Clip(a,b,low,true);Clip(b,a,high,false);
                    if(a.Count<3)continue;
                    int start=points.Count;
                    foreach(var v in a){points.Add(v.Position);tex.Add(v.UV);tint.Add(v.Tint);}
                    for(int k=2;k<a.Count;k++){output.Add(start);output.Add(start+k-1);output.Add(start+k);}
                }
            }
            var mesh=new Mesh{name="Map wall section",indexFormat=points.Count>65535?IndexFormat.UInt32:IndexFormat.UInt16};
            mesh.SetVertices(points);mesh.SetUVs(0,tex);mesh.SetColors(tint);mesh.subMeshCount=subs.Length;
            for(int j=0;j<subs.Length;j++)mesh.SetTriangles(subs[j],j,false);
            mesh.RecalculateBounds();return mesh;
        }
    }
}