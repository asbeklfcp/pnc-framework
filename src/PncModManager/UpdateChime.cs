using System;
namespace PncModManager {
    internal static class UpdateChime {
        internal const int Rate=24000;
        internal static float[] Samples(){
            var samples=new float[Rate*2];double peak=0;
            double[] notes={523.251,659.255,783.991,1046.502};
            for(int i=0;i<samples.Length;i++){
                double t=(double)i/Rate,value=0;
                for(int n=0;n<notes.Length;n++){
                    double age=t-n*.16;if(age<0)continue;
                    double attack=Math.Min(1,age/.018),decay=Math.Exp(-3.1*age);
                    double phase=2*Math.PI*notes[n]*age;
                    value+=attack*decay*(Math.Sin(phase)+.22*Math.Sin(phase*2)+.08*Math.Sin(phase*3));
                }
                double tail=Math.Min(1,(2-t)/.16);value*=tail;
                samples[i]=(float)value;peak=Math.Max(peak,Math.Abs(value));
            }
            if(peak>0)for(int i=0;i<samples.Length;i++)samples[i]=(float)(samples[i]/peak*.7);
            return samples;
        }
    }
}
