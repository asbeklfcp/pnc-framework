using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;

namespace PncPrivateLivingMap
{
    // For optional components: cache hits and misses, and retry misses only
    // after an assembly load. This never loads or enables a package.
    internal static class OptionalActorTypes
    {
        sealed class Entry { internal Type Type; internal int Generation; }
        static int generation;
        static readonly Dictionary<string,Entry> entries=new Dictionary<string,Entry>(StringComparer.Ordinal);
        static readonly object gate=new object();
        static OptionalActorTypes()
        {
            AppDomain.CurrentDomain.AssemblyLoad += (_,__) => Interlocked.Increment(ref generation);
        }
        internal static Type Resolve(string fullName)
        {
            if(string.IsNullOrEmpty(fullName))return null;
            lock(gate)
            {
                int current=Volatile.Read(ref generation);
                if(entries.TryGetValue(fullName,out var entry) && (entry.Type!=null || entry.Generation==current))
                    return entry.Type;
                Type found=null;
                foreach(var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    found=Find(assembly,fullName);
                    if(found!=null)break;
                }
                entries[fullName]=new Entry{Type=found,Generation=current};
                return found;
            }
        }
        static Type Find(Assembly assembly,string name)
        {
            try{return assembly.GetType(name,false);}
            catch(TypeLoadException){return null;}
            catch(System.IO.FileNotFoundException){return null;}
            catch(System.IO.FileLoadException){return null;}
            catch(BadImageFormatException){return null;}
            catch(NotSupportedException){return null;}
        }
    }
}

