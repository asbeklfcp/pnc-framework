using System;
namespace PncModManager {
 internal struct BorderStrip {internal double X,Y,W,H;internal BorderStrip(double x,double y,double w,double h){X=x;Y=y;W=w;H=h;}}
 internal static class UpdateBorderLayout {
  internal static double Thickness(double w,double h){return Math.Max(2,Math.Min(14,Math.Min(w,h)*.0075));}
  internal static BorderStrip[] Strips(double w,double h){double t=Thickness(w,h);return new[]{new BorderStrip(0,0,w,t),new BorderStrip(0,h-t,w,t),new BorderStrip(0,t,t,h-2*t),new BorderStrip(w-t,t,t,h-2*t)};}
 }
}
