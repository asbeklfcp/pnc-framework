namespace PncModManager {
    internal static class OverlayInputPolicy {
        internal static bool BlockNativeDispatch(bool overlayOpen,bool closingInputPending){
            return overlayOpen || closingInputPending;
        }
        // A full quiet frame separates dismissal from native UI reactivation.
        internal static bool CanRestore(int closedFrame,int currentFrame,bool activationHeld){
            return closedFrame>=0 && !activationHeld && currentFrame>closedFrame+1;
        }
    }
}
