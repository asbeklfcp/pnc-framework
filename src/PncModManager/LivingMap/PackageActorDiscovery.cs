using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;
using HarmonyLib;
using DunGen;
using Object=UnityEngine.Object;

namespace PncPrivateLivingMap
{
    internal sealed partial class MapModel
    {
        Type packageOwnerType, runtimeVisualType;
        PropertyInfo ownerIdProperty;
        float nextPackageScan,nextPackageLookup;
        void ResolvePackageTypes()
        {
            if(Time.unscaledTime<nextPackageLookup)return;
            nextPackageLookup=Time.unscaledTime+5;
            packageOwnerType=packageOwnerType??AccessTools.TypeByName("PncCustomEnemies.PackageOwnedObject");
            runtimeVisualType=runtimeVisualType??AccessTools.TypeByName("PncEdi.RuntimeSpriteVisual");
            if(packageOwnerType!=null)ownerIdProperty=packageOwnerType.GetProperty("OwnerId",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
            if(runtimeVisualType!=null)MapPause.GuardCustom(runtimeVisualType);
        }
        MonoBehaviour FindPackageOwner(Transform root)
        {
            ResolvePackageTypes();
            return packageOwnerType==null||root==null?null:root.GetComponentInParent(packageOwnerType,true) as MonoBehaviour;
        }
        string PackageName(MonoBehaviour marker)
        {
            try{return ownerIdProperty?.GetValue(marker) as string;}catch{return null;}
        }
        bool IsPackageActor(MonoBehaviour marker)
        {
            if(marker==null||!marker.gameObject.activeInHierarchy||marker.transform is RectTransform||
               marker.gameObject.scene!=SceneManager.GetActiveScene()||string.IsNullOrEmpty(PackageName(marker))||IsPooled(marker.transform))return false;
            if(marker.GetComponentInParent<Canvas>(true)!=null)return false;
            bool art=false;
            foreach(var r in marker.GetComponentsInChildren<Renderer>(true))
                if(r is SpriteRenderer || r is MeshRenderer || r is SkinnedMeshRenderer){art=true;break;}
            if(!art)return false;
            if(runtimeVisualType!=null&&marker.GetComponentInChildren(runtimeVisualType,true)!=null)return true;
            foreach(var t in NativeActorComponents)if(marker.GetComponentInChildren(t,true)!=null)return true;
            if(marker.GetComponentInChildren<Collider>(true)!=null)return true;
            foreach(var component in marker.GetComponents<MonoBehaviour>())
                if(component!=null && component.GetType().GetMethod("TakeDamage",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,new[]{typeof(int)},null)!=null)return true;
            return false;
        }
        MonoBehaviour PackageController(MonoBehaviour marker)
        {
            MonoBehaviour best=null;
            foreach(var component in marker.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if(component==null||!component.isActiveAndEnabled)continue;
                var t=component.GetType();
                if(t==packageOwnerType||t==runtimeVisualType)continue;
                if(Array.IndexOf(NativeActorComponents,t)>=0){if(best==null)best=component;continue;}
                if(t.GetMethod("TakeDamage",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,new[]{typeof(int)},null)!=null)
                {
                    MapPause.GuardCustom(t);
                    if(best==null)best=component;
                }
                else if(t.Assembly!=typeof(MapModel).Assembly && t.Assembly!=typeof(EnemyAI).Assembly &&
                         t.Assembly!=packageOwnerType.Assembly && component.transform==marker.transform)
                    MapPause.GuardCustom(t);
            }
            return best??marker;
        }
        void ScanPackageActors(PortraitLibrary view)
        {
            ResolvePackageTypes();
            if(packageOwnerType==null||Time.unscaledTime<nextPackageScan)return;
            nextPackageScan=Time.unscaledTime+.15f;
            foreach(var obj in Object.FindObjectsByType(packageOwnerType,FindObjectsSortMode.None))
                if(obj is MonoBehaviour marker && IsPackageActor(marker))
                    AddActor(PackageController(marker),view,PackageName(marker));
        }
        Room ActorRoom(Transform root)
        {
            var tile=root.GetComponentInParent<Tile>();
            if(tile!=null&&_rooms.TryGetValue(tile.GetInstanceID(),out var parent))return parent;
            var direct=LocateRoom(root.position);
            if(direct!=null)return direct;
            Room closest=null;float best=float.MaxValue;bool tie=false;
            foreach(var room in _rooms.Values)
            {
                if(room.Tile==null)continue;
                float d=room.WorldBounds.SqrDistance(root.position);
                if(d>.36f)continue;
                if(d<best-.0001f){closest=room;best=d;tie=false;}
                else if(Mathf.Abs(d-best)<.0001f)tie=true;
            }
            return tie?null:closest;
        }
    }
}