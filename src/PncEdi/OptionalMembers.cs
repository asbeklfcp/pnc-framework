using System;
using System.Collections.Generic;
using System.Reflection;
namespace PncEdi;

// Optional schema probing is normal across native AI types. Cache misses silently;
// Harmony AccessTools logs every miss, including successful property fallbacks.
internal static class OptionalMembers {
    internal static object Read(object target,string name) {
        if(target==null)return null;
        var field=Field(target.GetType(),name);if(field!=null)return field.GetValue(target);
        var property=Property(target.GetType(),name);
        return property!=null&&property.CanRead&&property.GetIndexParameters().Length==0 ? property.GetValue(target,null) : null;
    }
    internal static bool Write(object target,string name,object value) {
        if(target==null)return false;
        var field=Field(target.GetType(),name);
        if(field!=null){if(field.IsInitOnly)return false;field.SetValue(target,value);return true;}
        var property=Property(target.GetType(),name);
        if(property==null||!property.CanWrite||property.GetIndexParameters().Length!=0)return false;
        property.SetValue(target,value,null);return true;
    }
    static readonly Dictionary<(Type,string),FieldInfo> Fields=new Dictionary<(Type,string),FieldInfo>();
    static readonly Dictionary<(Type,string),PropertyInfo> Properties=new Dictionary<(Type,string),PropertyInfo>();
    internal static FieldInfo Field(Type type,string name) {
        if(type==null)return null;
        var key=(type,name);if(Fields.TryGetValue(key,out var cached))return cached;
        FieldInfo found=null;
        for(var t=type;t!=null&&found==null;t=t.BaseType)found=t.GetField(name,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static|BindingFlags.DeclaredOnly);
        Fields[key]=found;return found;
    }
    internal static PropertyInfo Property(Type type,string name) {
        if(type==null)return null;
        var key=(type,name);if(Properties.TryGetValue(key,out var cached))return cached;
        PropertyInfo found=null;
        for(var t=type;t!=null&&found==null;t=t.BaseType)found=t.GetProperty(name,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static|BindingFlags.DeclaredOnly);
        Properties[key]=found;return found;
    }
}
