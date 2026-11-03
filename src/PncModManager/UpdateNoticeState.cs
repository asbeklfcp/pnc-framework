namespace PncModManager {
    internal sealed class UpdateNoticeState {
        internal string LastRelease {get;private set;}
        double started=-100;
        double attentionStarted=-100;
        internal void Observe(string release,bool gameplay,double now){
            if(string.IsNullOrEmpty(release) || release==LastRelease)return;
            LastRelease=release;attentionStarted=now;if(gameplay)started=now;
        }
        internal float BorderAlpha(double now){
            double age=now-attentionStarted;if(age<0 || age>=5)return 0;
            return age<=4.5?1:(float)((5-age)/0.5);
        }
        internal float Alpha(double now){
            double age=now-started;if(age<0 || age>=20.6)return 0;
            return age<=20?1:(float)(1-(age-20)/0.6);
        }
    }
}
