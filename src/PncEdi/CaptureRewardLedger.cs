using System;
using System.Runtime.CompilerServices;
namespace PncEdi;
// A native actor's reward history survives profile toggles, but does not retain destroyed actors.
internal static class CaptureRewardLedger {
    sealed class History {internal int Releases;}
    static readonly ConditionalWeakTable<object,History> Histories=new ConditionalWeakTable<object,History>();
    internal static int Next(object actor) {
        var history=Histories.GetOrCreateValue(actor);
        history.Releases=Math.Min(4,history.Releases+1);return history.Releases;
    }
    internal static int Target(int current,int maximum,int release) {
        if(current<=0 || maximum<=0 || current>=maximum)return current;
        long amount=release<=1?maximum:release==2?((long)maximum+1)/2:release==3?((long)maximum+3)/4:25;
        return (int)Math.Min(maximum,(long)current+amount);
    }
}
