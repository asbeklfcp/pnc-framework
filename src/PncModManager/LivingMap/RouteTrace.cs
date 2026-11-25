using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace PncPrivateLivingMap
{
    internal sealed class RouteTrace
    {
        sealed class Segment
        {
            internal readonly List<Vector3> Points=new List<Vector3>(512);
            internal readonly List<int> Indices=new List<int>(1024);
            internal readonly Mesh Mesh=new Mesh { name="Recorded player route" };
            internal bool Dirty;
            internal int Floor;
        }
        readonly List<Segment> segments=new List<Segment>();
        Material ink;
        readonly MaterialPropertyBlock routeStyle=new MaterialPropertyBlock();
        Vector3 last;
        float lastTime;
        bool sample;
        internal Vector3 Last=>last;
        internal int Count {get;private set;}
        internal int Breaks {get;private set;}
        internal void Record(Vector3 position,float time,int floor=-1)
        {
            float distance=Vector3.Distance(last,position);
            if(sample && distance<.001f){lastTime=time;return;}
            // A discontinuity has no observed route between its endpoints.
            bool connected=sample && time-lastTime<.5f && distance<=Mathf.Max(1f,(time-lastTime)*48);
            Segment tail=segments.Count==0?null:segments[segments.Count-1];
            if(tail==null || tail.Points.Count>=512 || tail.Floor!=floor || !connected)
            {
                if(sample&&!connected)Breaks++;
                tail=new Segment{Floor=floor};segments.Add(tail);
                if(connected)tail.Points.Add(last);
            }
            int i=tail.Points.Count;
            tail.Points.Add(position);
            if(i>0){tail.Indices.Add(i-1);tail.Indices.Add(i);}
            tail.Dirty=true;last=position;lastTime=time;sample=true;Count++;
        }
        internal void Draw(CommandBuffer commands,int floor,bool focus,float opacity)
        {
            if(ink==null)
            {
                var shader=Shader.Find("Hidden/Internal-Colored");
                if(shader==null)return;
                ink=new Material(shader);ink.SetColor("_Color",new Color(.95f,.66f,.22f,.95f));
                ink.SetInt("_ZTest",(int)CompareFunction.Always);ink.SetInt("_ZWrite",0);
                ink.SetInt("_SrcBlend",(int)BlendMode.SrcAlpha);ink.SetInt("_DstBlend",(int)BlendMode.OneMinusSrcAlpha);
            }
            foreach(var segment in segments)
            {
                bool background=floor>=0 && segment.Floor>=0 && segment.Floor!=floor;
                if(segment.Indices.Count==0 || (background && !focus))continue;
                if(segment.Dirty)
                {
                    segment.Mesh.SetVertices(segment.Points);
                    segment.Mesh.SetIndices(segment.Indices,MeshTopology.Lines,0,true);
                    segment.Dirty=false;
                }
                routeStyle.SetColor("_Color",new Color(.95f,.66f,.22f,background?opacity:.95f));
                commands.DrawMesh(segment.Mesh,Matrix4x4.identity,ink,0,0,routeStyle);
            }
        }
        #if MAP_QA
        internal void Export(string file)
        {
            using(var writer=new System.IO.StreamWriter(file))
            foreach(var segment in segments)
            {
                writer.WriteLine("# segment");
                foreach(var p in segment.Points)writer.WriteLine(p.x.ToString("R")+"\t"+p.y.ToString("R")+"\t"+p.z.ToString("R"));
            }
        }
#endif
        internal void Clear()
        {
            foreach(var segment in segments)Object.Destroy(segment.Mesh);
            if(ink!=null)Object.Destroy(ink);
            segments.Clear();sample=false;Count=0;Breaks=0;
        }
    }
}
