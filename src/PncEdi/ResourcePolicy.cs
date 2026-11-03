using System;
namespace PncEdi;
internal static class ResourcePolicy
{
    internal static bool CurrentLease(object current,object releasing)=>ReferenceEquals(current,releasing);
    internal static bool Allows(GameplayProfile profile,bool gameplay,bool feature)
        => profile==GameplayProfile.Custom && gameplay && feature;
    internal static float RecoveryEnd(float previous,float now,float heat)
    {
        if(float.IsNaN(heat)||float.IsInfinity(heat)||heat<=0) return previous;
        float t=Math.Max(0,Math.Min(1,heat/100f));
        return now+Math.Max(0,previous-now)+(.5f+.5f*t);
    }
    internal static float HorizontalScale(float x,float z,float limit=1.25f)
    {
        float magnitude=(float)Math.Sqrt(x*x+z*z);
        return magnitude>limit ? limit/magnitude : 1;
    }
}
