using System.Collections.Generic;
namespace PncModManager { internal static class UpdateBuild {
internal const string ReleaseId="0.1.2";
internal const string Channel="stable";
internal const string Feed="https://raw.githubusercontent.com/asbeklfcp/pnc-framework/main/updates/stable/latest.xml";
internal const string Trust=@"<RSAKeyValue><Modulus>0X1WeU7S/lTcsUiYPNeztX4g+PZovbizx6ZVIdRjzY/xqsOtRGn0c5Te/8heME6ioQVoyKlpJKXKCzy58Lk/GYka4BN/MeYzOQWMb2y0ujJkqC1vNsIRmEWO8ADILe05dQPUbK8iTYbF28dFfZLKHoE6OlisXWnznB4IiYKPfUd5LPZU2q1Dx2dw8KPBlS+6OyBUA2gWo2+pwr6LJ0XgMy7KzK3V4RvOgWSqRf2ExaeSh3lC5wN4HOG2x7XUKMMk+0nbNJ1Q4+9trPs3cDyltc4sD9hdKxagACzFSNONF0Mtmqp6L+niPbdZHjeP/2vtgFIBtcGr+gwppamGNM31T8rKQZ7b+Ru0Jsw9kV478d99JZQw9lw7F2crG9IksSOrPLuaJ38jiRoIZ4YFMiO+7fqDNlm0z5Bv26PVBi2b5sTZca0iMRurCh5zZs2+f8OKgxa0L3sbTMHZmlnDaDoXVIwNIJB7Ido5zT6sp2/Dnnz1ocIwNWzheECacgMDg6lqsn/TKLBLui/PJ1/DevnI+BFohr8UYfXopHZdl3yKSGNDV/hGUPh3MdUSMOm+B7lzJxlj3HuhNfQhrTMyMgMpWSbP4vqDPnBkpGUuEQlAKKFPjLjyKe3LuR1SywGWhRauIPXwaLsIDUCbV6HSL5TN9SCF+jN63UKf6DAEGwUXhLk=</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";
internal static readonly Dictionary<string,string> HelperHashes=new Dictionary<string,string> {
{"UpdatePolicy.cs","f956375a7da5402315c89d3dcaafbca452cfab6b0fcb25a8481cdad4ef873d71"},
{"UpdateHelper.ps1","667f539784a65c2a5890c11249670857865283e2a93aa3bd83b982c6ca997381"},
{"Trust.xml","5b4779511e1d1277018b381a28ea5ae2cb023703dba1f78313c7e26b800e239f"},
{"Show-ParchmentNotification.ps1","620332c202ea56bb440cbf0076e5d5a783f069b67315cbac707cf32b01114edc"},
{"NativeFontRebuilder.cs","19295d374919faf67e646b6d25792387fa3fc2d59e31ae2766cfed7ce6a92d2a"},
{"NativeFontCache.cs","8b8fe0eae565d51d375cbd36d83f0ca810b41e3e8df1d7200558263a6536866f"},
{"RestartLaunch.ps1","9bf9a7f8fe63e9dfc9b6a5fb7cc17ed30d0b7f4ea569155bd7558b861453c750"},
{"art/PNC_Updater_Notification.png","553ece7215acf365568d9f84cca2d0aec53bee5ccd076d9dc01c7a256fb18640"},
}; }}
