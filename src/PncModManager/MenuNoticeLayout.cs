using System;
namespace PncFramework.UpdateUI {
    public struct NoticeRect {
        public float X,Y,W,H;
        public NoticeRect(float x,float y,float w,float h){X=x;Y=y;W=w;H=h;}
    }
    public static class MenuNoticeLayout {
        public static bool Visible(bool mainMenu,bool managerOpen,bool versionVisible,bool approvedOffer){return mainMenu && !managerOpen && versionVisible;}
        public static NoticeRect Place(NoticeRect version,NoticeRect safe) {
            float size=Math.Min(Math.Min(safe.W,safe.H),Math.Max(32f,Math.Min(72f,version.H*1.2f)));
            float gap=Math.Max(6f,version.H*.2f);
            float x=version.X-gap-size;
            float y=version.Y+(version.H-size)*.5f-Math.Max(4f,version.H*.12f);
            x=Math.Max(safe.X,Math.Min(x,safe.X+safe.W-size));
            y=Math.Max(safe.Y,Math.Min(y,safe.Y+safe.H-size-Math.Max(8f,version.H*.2f)));
            return new NoticeRect(x,y,size,size);
        }
    }
}
