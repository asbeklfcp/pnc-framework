using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object=UnityEngine.Object;

namespace PncPrivateLivingMap
{
    // Keep ownership of only the Witch audio we actually paused. Do not
    // modify global volume, the audio listener, or already paused sources.
    internal sealed class MapWitchAudio
    {
        readonly HashSet<AudioSource> paused=new HashSet<AudioSource>();
        readonly HashSet<AudioSource> candidates=new HashSet<AudioSource>();
        Type controller;
        FieldInfo[] fields;
        float nextScan;
        internal void Open()
        {
            paused.Clear();nextScan=0;
            Refresh();
        }
        internal void Refresh()
        {
            if(Time.unscaledTime<nextScan)return;
            nextScan=Time.unscaledTime+.15f;
            try
            {
                controller=OptionalActorTypes.Resolve("CharmWitch.CharmWitchController");
                if(controller==null)return;
                MapPause.GuardCustom(controller);
                if(fields==null)
                {
                    var list=new List<FieldInfo>();
                    foreach(var field in controller.GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic))
                        if(typeof(AudioSource).IsAssignableFrom(field.FieldType))list.Add(field);
                    fields=list.ToArray();
                }
                candidates.Clear();
                foreach(var item in Object.FindObjectsByType(controller,FindObjectsSortMode.None))
                {
                    if(!(item is MonoBehaviour witch)||witch==null||!witch.gameObject.activeInHierarchy||
                       witch.gameObject.scene!=SceneManager.GetActiveScene())continue;
                    foreach(var audio in witch.GetComponentsInChildren<AudioSource>(false))
                        if(audio!=null)candidates.Add(audio);
                    foreach(var field in fields)
                        if(field.GetValue(witch) is AudioSource audio&&audio!=null)candidates.Add(audio);
                }
                foreach(var audio in candidates)
                    if(audio!=null&&audio.isActiveAndEnabled&&audio.isPlaying)
                    {
                        audio.Pause();
                        paused.Add(audio);
                    }
            }
            catch(Exception ex){Debug.LogWarning("[PNC MAP] Witch audio pause: "+ex.Message);}
        }
        internal void Close()
        {
            foreach(var audio in paused)
                if(audio!=null&&audio.isActiveAndEnabled)
                    try{audio.UnPause();}catch(Exception ex){Debug.LogWarning("[PNC MAP] Witch audio resume: "+ex.Message);}
            paused.Clear();candidates.Clear();nextScan=0;
        }
    }
}