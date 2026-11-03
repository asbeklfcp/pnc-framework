using System;
using System.IO;
using System.Threading;
using System.Linq;
using BepInEx;
using PncFramework.Update;
using UnityEngine;
using UnityEngine.InputSystem;
namespace PncModManager;
public sealed partial class Plugin {
    bool _patchNotesOpen;
    int _notesFocus;
    GUIStyle _notesHeading,_notesBody,_notesCategory,_notesEntry,_notesDate;
    void OpenPatchNotes(){
        if(_patchNotesOpen)return;
        SetVisible(true);_patchNotesOpen=true;_visible=false;_notesFocus=0;
    }
    void ClosePatchNotes(){_patchNotesOpen=false;_visible=true;SetVisible(false);}
    string[] NotesActions(){
        if(_updatePrepared)return new[]{"Exit game and install","Cancel update","Back"};
        if(_updatePreparation!=null)return new[]{"Cancel download","Back"};
        return _updateOffer!=null?new[]{"Download update","Check again","Back"}:new[]{"Check again","Back"};
    }
    void NotesAction(string action){
        switch(action){
            case "Back":ClosePatchNotes();break;
            case "Check again":CheckUpdates();LoadNotesHistory();break;
            case "Download update":PrepareUpdate();break;
            case "Cancel download":_downloadCancel?.Cancel();string home=Path.Combine(Paths.GameRootPath,"BepInEx","framework-updates");if(Directory.Exists(home))File.WriteAllText(Path.Combine(home,"cancel"),"cancel");_downloadStage="Cancelling...";break;
            case "Cancel update":File.WriteAllText(Path.Combine(Paths.GameRootPath,"BepInEx","framework-updates","cancel"),"cancel");_updatePrepared=false;_downloadStage="";_updateMessage="Update cancelled. Your game has not been changed.";break;
            case "Exit game and install":if(_updatePrepared)Application.Quit();break;
        }
    }
    bool TickStandaloneNotesInput(){
        if(!_patchNotesOpen)return false;
        var keyboard=Keyboard.current;var pad=Gamepad.current;
        if((keyboard!=null && keyboard.escapeKey.wasPressedThisFrame) || (pad!=null && pad.buttonEast.wasPressedThisFrame)){ClosePatchNotes();return true;}
        var actions=NotesActions();_notesFocus=Mathf.Clamp(_notesFocus,0,actions.Length-1);
        bool next=keyboard!=null && keyboard.tabKey.wasPressedThisFrame;
        bool previous=false;
        if(next && keyboard.shiftKey.isPressed){next=false;previous=true;}
        if(pad!=null){
            if(pad.dpad.right.wasPressedThisFrame)next=true;if(pad.dpad.left.wasPressedThisFrame)previous=true;
            float stick=pad.leftStick.ReadValue().y;
            if(Mathf.Abs(stick)>.4f){_notesScroll.y-=stick*12f;}
            if(pad.dpad.up.isPressed)_notesScroll.y-=8;if(pad.dpad.down.isPressed)_notesScroll.y+=8;
        }
        if(next)_notesFocus=(_notesFocus+1)%actions.Length;if(previous)_notesFocus=(_notesFocus+actions.Length-1)%actions.Length;
        if((keyboard!=null && keyboard.enterKey.wasPressedThisFrame) || (pad!=null && pad.buttonSouth.wasPressedThisFrame))NotesAction(actions[_notesFocus]);
        return true;
    }
    void NotesStyles(){
        if(_notesHeading!=null)return;
        _notesHeading=new GUIStyle(GUI.skin.label){fontSize=28,wordWrap=true,alignment=TextAnchor.MiddleCenter};
        _notesBody=new GUIStyle(GUI.skin.label){fontSize=18,wordWrap=true,richText=false};
        _notesCategory=new GUIStyle(_notesBody){fontSize=22,padding=new RectOffset(0,0,12,5)};
        _notesEntry=new GUIStyle(_notesBody){fontSize=21};_notesDate=new GUIStyle(_notesBody){fontSize=16};
    }
    void DrawPatchNotesWindow(){
        EnsureDashboardTheme();var skin=GUI.skin;var matrix=GUI.matrix;GUI.skin=_dashboardSkin;
        float scale=Mathf.Clamp(Screen.height/1080f,.75f,2f);GUI.matrix=Matrix4x4.Scale(new Vector3(scale,scale,1));NotesStyles();
        try {
            float height=Mathf.Min(970,Screen.height/scale-40);float width=Mathf.Min(700,height*.72f);
            Rect outer=new Rect((Screen.width/scale-width)*.5f,(Screen.height/scale-height)*.5f,width,height);
            ParchmentArt.Slice("PNC_PatchNotes_Scroll.png",outer,width/1719f);
            Rect safe=new Rect(outer.x+width*.14f,outer.y+height*.24f,width*.72f,height*.61f);
            Color paperColor=GUI.color;GUI.color=new Color(1f,.88f,.62f,.24f);GUI.DrawTexture(safe,Texture2D.whiteTexture);GUI.color=paperColor;
            GUILayout.BeginArea(safe);
            GUILayout.Label("PATCH NOTES",_notesHeading);
            
            if(_updatePrepared){
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.Label("UPDATE READY!",_notesCategory);
                GUILayout.Label("Please close Post Nut Calamity to install the new version.",_notesEntry);
                GUILayout.Label("Your settings, controls and other mods will be preserved.",_notesBody);
                GUILayout.EndVertical();
            } else {
                string state=_downloadStage!=""?_downloadStage:NativeTypeface.Clean(_updateMessage);
                GUILayout.Label(NativeTypeface.Clean(state),_notesDate);
                if(_updatePreparation!=null){
                    long total=Interlocked.Read(ref _downloadTotal),done=Interlocked.Read(ref _downloadBytes);
                    float fraction=total>0?Mathf.Clamp01((float)done/total):0;
                    Rect progress=GUILayoutUtility.GetRect(10,16,GUILayout.ExpandWidth(true));GUI.Box(progress,"");
                    if(fraction>0)GUI.DrawTexture(new Rect(progress.x+2,progress.y+2,(progress.width-4)*fraction,progress.height-4),Texture2D.whiteTexture);
                    GUILayout.Label(total>0?Mathf.RoundToInt(fraction*100)+"%":"Preparing...",_notesDate);
                }
            }
            _notesScroll=GUILayout.BeginScrollView(_notesScroll,GUILayout.ExpandHeight(true));
            if(_updateOffer!=null){GUILayout.Label("Available: v"+_updateOffer.Id,_notesCategory);if(!_notesHistory.Releases.Any(r=>r.Version==_updateOffer.Id))GUILayout.Label(NativeTypeface.Clean(_updateOffer.Notes),_notesBody);}
            if(_notesHistory.Releases.Count==0)GUILayout.Label("No published announcements are stored yet.",_notesBody);
            foreach(var release in _notesHistory.Releases){
                GUILayout.Space(12);GUILayout.Label("v"+release.Version+"  |  "+release.Date.ToString("dd MMM yyyy",System.Globalization.CultureInfo.InvariantCulture),_notesDate);
                GUILayout.Label(NativeTypeface.Clean(release.Title),_notesCategory);
                foreach(var category in release.Categories){
                    GUILayout.Label(NativeTypeface.Clean(category.Title),_notesCategory);
                    foreach(var entry in category.Entries){
                        GUILayout.BeginHorizontal();var portrait=NativePortraits.Resolve(entry.Character,entry.PortraitResource,entry.PortraitSprite,entry.PortraitCrop);
                        if(portrait!=null){Rect image=GUILayoutUtility.GetRect(64,64,GUILayout.Width(64),GUILayout.Height(64));GUI.DrawTextureWithTexCoords(image,portrait.Sprite.texture,portrait.UV,true);GUILayout.Space(8);}
                        GUILayout.BeginVertical();if(!String.IsNullOrEmpty(entry.Title))GUILayout.Label(NativeTypeface.Clean(entry.Title),_notesEntry);
                        GUILayout.Label(NativeTypeface.Clean(entry.Text),_notesBody);GUILayout.EndVertical();GUILayout.EndHorizontal();GUILayout.Space(8);
                    }
                }
            }
            if(!String.IsNullOrEmpty(_historyStatus))GUILayout.Label(_historyStatus,_notesDate);
            if(!String.IsNullOrEmpty(_notesHistory.ArchiveUrl) && GUILayout.Button("Earlier announcements")){
                try{var feed=Policy.ValidateUrl(UpdateBuild.Feed,UpdateBuild.Channel,null);var archive=Policy.ValidateUrl(_notesHistory.ArchiveUrl,UpdateBuild.Channel,feed);Application.OpenURL(archive.ToString());}catch{_historyStatus="The archive is unavailable.";}
            }
            GUILayout.EndScrollView();
            string[] actions=NotesActions();
            for(int i=0;i<actions.Length;i++){
                Color old=GUI.color;if(i==_notesFocus)GUI.color=new Color(1f,.89f,.54f);
                if(GUILayout.Button(actions[i],GUILayout.MinHeight(34)))NotesAction(actions[i]);GUI.color=old;
            }
            GUILayout.EndArea();
        } finally {GUI.skin=skin;GUI.matrix=matrix;}
    }
}

