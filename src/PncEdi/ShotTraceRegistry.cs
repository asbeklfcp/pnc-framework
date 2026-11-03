using System.Runtime.CompilerServices;
namespace PncEdi;
// Weak projectile identity, with explicit replacement on pooled/native reuse.
internal sealed class ShotTraceRegistry<T> where T:class {
    internal sealed class Record {internal T Actor;internal string Kind;}
    readonly ConditionalWeakTable<object,Record> records=new ConditionalWeakTable<object,Record>();
    internal void Register(object projectile,T actor,string kind) {
        if(projectile==null)return;
        records.Remove(projectile);
        if(actor!=null)records.Add(projectile,new Record{Actor=actor,Kind=kind});
    }
    internal bool TryGet(object projectile,out Record record) {
        record=null;return projectile!=null && records.TryGetValue(projectile,out record);
    }
}
