namespace PncCustomEnemies;

internal enum CleanupVerification { Pending, Complete, Failed }
internal static class CleanupVerificationPolicy
{
    internal static CleanupVerification Evaluate(bool errors,int remaining,int pending,bool stuckCapture,
        bool sceneHeld,float activeSeconds)
    {
        if(errors) return CleanupVerification.Failed;
        if(remaining==0 && pending==0 && !stuckCapture) return CleanupVerification.Complete;
        return !sceneHeld && activeSeconds>=5f ? CleanupVerification.Failed : CleanupVerification.Pending;
    }
}
