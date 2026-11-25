using System;
namespace PncPrivateLivingMap {
    internal static class CameraMotion {
        internal static float SpeedMultiplier(bool fast,bool precise)=>precise?.25f:fast?3f:1f;
        internal static void Weights(float dt,float rate,out float decay,out float oldWeight){
            decay=(float)Math.Exp(-rate*dt);
            oldWeight=(1f-decay)/rate;
        }
    }
}