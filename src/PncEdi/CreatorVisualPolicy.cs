using System;
namespace PncEdi;
internal static class CreatorVisualPolicy {
    internal static bool ShouldRestage(bool managedSlot,bool nativeChaser,bool fodder)
        => !fodder && (managedSlot || nativeChaser);
    // Shared presentation order: character art below the universal escape prompt.
    internal const int SceneSortingOrder=4900;
    internal const int PausedSceneSortingOrder=-100;
    internal const int EscapeHintSortingOrder=5000;
    internal const int CaptureHudSortingOrder=4950;
    internal static float FaceHeightOffset(float minimumY,float height,float actorY,float fraction)
        => minimumY+Math.Max(0,height)*Math.Max(0,Math.Min(1,fraction))-actorY;
    // Verified 0.4.2 clip binding: path=0, script=UI.Image, attribute=CRC32(m_Sprite).
    internal const uint SpriteAttribute=2015549526;
    internal static bool RootImageBinding(uint path,uint attribute,int type,bool pointer)
        =>path==0&&attribute==SpriteAttribute&&type==114&&pointer;
    internal static bool UsableFrame(string name,float width,float height) {
        if(string.IsNullOrWhiteSpace(name)||width<=0||height<=0)return false;
        string n=name.ToLowerInvariant();
        return !n.Contains("healthbar")&&!n.Contains("health_bar")&&!n.Contains("enemy_grab_health")&&!n.Contains("cursor")&&!n.Contains("button")&&!n.Contains("mask");
    }
}
