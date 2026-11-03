using System;
using System.Collections.Generic;
namespace PncEdi;
internal static class IdentityPolicy {
    static readonly HashSet<string> Known=new HashSet<string>(StringComparer.OrdinalIgnoreCase){"zombie","nun","gargoyle","gooper","plantasha","gravy","baphomet","dragon","wendigo","blinded_beast","serpent","goonshroom","imp","mimic"};
    internal static string Resolve(string prefab,string gallery) {
        if(!string.IsNullOrWhiteSpace(prefab)&&Known.Contains(prefab))return prefab;
        if(!string.IsNullOrWhiteSpace(gallery)&&Known.Contains(gallery))return gallery;
        if(!string.IsNullOrWhiteSpace(prefab)&&!int.TryParse(prefab,out _))return prefab;
        return !string.IsNullOrWhiteSpace(gallery)&&!int.TryParse(gallery,out _) ? gallery : "";
    }
}
