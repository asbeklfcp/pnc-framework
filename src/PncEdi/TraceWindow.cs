namespace PncEdi;
// Hard bounds: gameplay clock excludes pauses; expiry never restarts itself.
internal sealed class TraceWindow {
    readonly float deadline;int remaining;bool stopped;
    internal TraceWindow(float now,float seconds,int lines){deadline=now+seconds;remaining=lines;}
    internal bool Active(float now){if(stopped)return false;if(remaining<=0 || now>deadline){stopped=true;return false;}return true;}
    internal bool Take(float now){if(!Active(now))return false;remaining--;return true;}
}
