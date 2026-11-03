using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using PncFramework.Controls;
namespace PncModManager;
public sealed partial class Plugin {
    struct FocusNode {internal int Index;internal Rect Rect;}
    readonly List<FocusNode> _managerFocusNodes=new List<FocusNode>();
    int _managerOrdinal,_managerFocus=-1,_managerActivate=-1;
    bool _managerPadFocus;
    void TickManagerNavigation(){
        var pad=Gamepad.current;if(!_visible || pad==null || ControlService.IsCapturing)return;
        if(pad.leftShoulder.wasPressedThisFrame || pad.rightShoulder.wasPressedThisFrame){_showControls=!_showControls;_managerFocus=-1;_managerPadFocus=true;return;}
        if(_showControls)return; // Existing Controls page retains its proven action-list/capture navigation.
        if(pad.buttonEast.wasPressedThisFrame){SetVisible(false);return;}
        Vector2 direction=Vector2.zero;if(pad.dpad.up.wasPressedThisFrame)direction=Vector2.down;if(pad.dpad.down.wasPressedThisFrame)direction=Vector2.up;if(pad.dpad.left.wasPressedThisFrame)direction=Vector2.left;if(pad.dpad.right.wasPressedThisFrame)direction=Vector2.right;
        if(_managerFocusNodes.Count==0)return;
        if(direction!=Vector2.zero){
            _managerPadFocus=true;int selected=_managerFocusNodes.FindIndex(n=>n.Index==_managerFocus);
            if(selected<0)_managerFocus=_managerFocusNodes[0].Index;
            else {
                var origin=_managerFocusNodes[selected].Rect.center;float best=float.MaxValue;int next=_managerFocus;
                foreach(var n in _managerFocusNodes){Vector2 delta=n.Rect.center-origin;float forward=Vector2.Dot(delta,direction);if(forward<=1)continue;float sideways=Mathf.Abs(delta.x*direction.y-delta.y*direction.x);float score=forward+sideways*3;if(score<best){best=score;next=n.Index;}}
                _managerFocus=next;
            }
        }
        if(pad.buttonSouth.wasPressedThisFrame && _managerFocus>=0)_managerActivate=_managerFocus;
        _settingsScroll.y=Mathf.Max(0,_settingsScroll.y-pad.rightStick.ReadValue().y*13);
    }
    void BeginManagerNavigation(){_managerOrdinal=0;if(Event.current.type==EventType.Repaint)_managerFocusNodes.Clear();}
    bool Focused(int index){return _managerPadFocus && _managerFocus==index && !_showControls;}
    void RecordManagerControl(int index){
        if(Event.current.type!=EventType.Repaint || !GUI.enabled)return;
        Rect local=GUILayoutUtility.GetLastRect();Vector2 a=GUIUtility.GUIToScreenPoint(local.min),b=GUIUtility.GUIToScreenPoint(local.max);
        Rect rect=Rect.MinMaxRect(a.x,a.y,b.x,b.y);if(rect.width<=0 || rect.height<=0 || rect.center.x<0 || rect.center.x>Screen.width || rect.center.y<0 || rect.center.y>Screen.height)return;
        _managerFocusNodes.Add(new FocusNode{Index=index,Rect=rect});
    }
    bool ConsumeManagerControl(int index){if(_managerActivate!=index || Event.current.type!=EventType.Layout || !GUI.enabled)return false;_managerActivate=-1;return true;}
    bool NavButton(string text,params GUILayoutOption[] options){return NavButton(text,GUI.skin.button,options);}
    bool NavButton(string text,GUIStyle style,params GUILayoutOption[] options){
        int index=_managerOrdinal++;GUI.SetNextControlName("pnc-button-"+index);Color old=GUI.backgroundColor;if(Focused(index))GUI.backgroundColor=new Color(1f,.83f,.34f);
        bool clicked=GUILayout.Button(NativeTypeface.Clean(text),style,options);GUI.backgroundColor=old;RecordManagerControl(index);return clicked || ConsumeManagerControl(index);
    }
    bool NavToggle(bool value,string label,params GUILayoutOption[] options){
        int index=_managerOrdinal++;GUI.SetNextControlName("pnc-toggle-"+index);bool result=GUILayout.Toggle(value,NativeTypeface.Clean(label),options);RecordManagerControl(index);return ConsumeManagerControl(index)?!value:result;
    }
}
