using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using PncFramework.UpdateUI;
namespace PncModManager;
public sealed partial class Plugin {
    RectTransform _versionAnchor,_noticeSelectableRect;
    Canvas _versionCanvas;
    TMP_Text _versionText;
    Button _noticeSelectable;
    GraphicRaycaster _ownedNoticeRaycaster;
    float _nextVersionLookup;
    readonly Vector3[] _versionCorners=new Vector3[4];
    void TickMainMenuUpdateNotice(){
        if(_mainMenu==null || !_mainMenu.isActiveAndEnabled){if(_noticeSelectable!=null)_noticeSelectable.gameObject.SetActive(false);_versionAnchor=null;_versionCanvas=null;_versionText=null;return;}
        if(_versionAnchor!=null && _versionAnchor.gameObject.activeInHierarchy){
            EnsureNoticeSelectable();_noticeSelectable.gameObject.SetActive(!_visible && !_patchNotesOpen && IsMainMenuVisible());return;
        }
        if(Time.unscaledTime<_nextVersionLookup)return;_nextVersionLookup=Time.unscaledTime+.5f;
        foreach(var text in UnityEngine.Object.FindObjectsByType<TMP_Text>(FindObjectsSortMode.None)){
            if(text.gameObject.scene!=_mainMenu.gameObject.scene || !text.isActiveAndEnabled)continue;
            if(text.transform.root.name!="Version Number" && (text.text??"").Trim().TrimStart('v','V')!="0.4.2")continue;
            _versionAnchor=text.rectTransform;_versionCanvas=text.GetComponentInParent<Canvas>();_versionText=text;
            Logger.LogInfo("[UPDATE-MENU] native version anchor found; current/available artwork states enabled");return;
        }
        foreach(var text in UnityEngine.Object.FindObjectsByType<Text>(FindObjectsSortMode.None)){
            if(text.gameObject.scene!=_mainMenu.gameObject.scene || !text.isActiveAndEnabled)continue;
            if(text.transform.root.name!="Version Number" && (text.text??"").Trim().TrimStart('v','V')!="0.4.2")continue;
            _versionAnchor=text.rectTransform;_versionCanvas=text.GetComponentInParent<Canvas>();_versionText=null;return;
        }
    }
    void EnsureNoticeSelectable(){
        if(_noticeSelectable!=null || _versionCanvas==null)return;
        if(_versionCanvas.GetComponent<GraphicRaycaster>()==null)_ownedNoticeRaycaster=_versionCanvas.gameObject.AddComponent<GraphicRaycaster>();
        var go=new GameObject("PNC Patch Notes Arrow",typeof(RectTransform),typeof(CanvasRenderer),typeof(Image),typeof(Button));
        go.transform.SetParent(_versionCanvas.transform,false);_noticeSelectableRect=(RectTransform)go.transform;
        go.GetComponent<Image>().color=Color.clear;_noticeSelectable=go.GetComponent<Button>();
        _noticeSelectable.targetGraphic=go.GetComponent<Image>();
        _noticeSelectable.onClick.AddListener(OpenPatchNotes);
        var navigation=_noticeSelectable.navigation;navigation.mode=Navigation.Mode.Automatic;_noticeSelectable.navigation=navigation;
    }
    void DrawMainMenuUpdateNotice(){
        bool versionVisible=_versionAnchor!=null && _versionCanvas!=null && _versionCanvas.isActiveAndEnabled && _versionAnchor.gameObject.activeInHierarchy;
        if(!MenuNoticeLayout.Visible(_mainMenu!=null && _mainMenu.isActiveAndEnabled,_visible || _patchNotesOpen,versionVisible,_updateOffer!=null))return;
        Camera camera=_versionCanvas.renderMode==RenderMode.ScreenSpaceOverlay?null:_versionCanvas.worldCamera;
        _versionAnchor.GetWorldCorners(_versionCorners);
        if(_versionText!=null && _versionText.textBounds.size.x>0 && _versionText.textBounds.size.y>0){
            Bounds b=_versionText.textBounds;_versionCorners[0]=_versionAnchor.TransformPoint(new Vector3(b.min.x,b.min.y,0));_versionCorners[1]=_versionAnchor.TransformPoint(new Vector3(b.min.x,b.max.y,0));_versionCorners[2]=_versionAnchor.TransformPoint(new Vector3(b.max.x,b.max.y,0));_versionCorners[3]=_versionAnchor.TransformPoint(new Vector3(b.max.x,b.min.y,0));
        }
        float minX=float.MaxValue,minY=float.MaxValue,maxX=float.MinValue,maxY=float.MinValue;
        foreach(var corner in _versionCorners){var point=RectTransformUtility.WorldToScreenPoint(camera,corner);minX=Mathf.Min(minX,point.x);minY=Mathf.Min(minY,point.y);maxX=Mathf.Max(maxX,point.x);maxY=Mathf.Max(maxY,point.y);}
        Rect safe=Screen.safeArea;var pos=MenuNoticeLayout.Place(new NoticeRect(minX,Screen.height-maxY,maxX-minX,maxY-minY),new NoticeRect(safe.x,Screen.height-safe.yMax,safe.width,safe.height));
        Rect rect=new Rect(pos.X,pos.Y,pos.W,pos.H);
        EnsureNoticeSelectable();
        if(_noticeSelectableRect!=null){
            Vector3 world;if(RectTransformUtility.ScreenPointToWorldPointInRectangle((RectTransform)_versionCanvas.transform,new Vector2(rect.center.x,Screen.height-rect.center.y),camera,out world))_noticeSelectableRect.position=world;
            _noticeSelectableRect.sizeDelta=new Vector2(rect.width,rect.height)/_versionCanvas.scaleFactor;
        }
        string asset=_updateOffer!=null?"PNC_UpdateArrow_Available.png":"PNC_UpdateArrow_Current.png";
        Color old=GUI.color;
        if(_updateOffer!=null){
            float wave=Mathf.Sin(Time.unscaledTime*1.35f);float grow=1f+.025f*wave;
            Rect glow=new Rect(rect.center.x-rect.width*.56f,rect.center.y-rect.height*.56f,rect.width*1.12f,rect.height*1.12f);
            GUI.color=new Color(1f,.78f,.24f,.10f+.04f*wave);ParchmentArt.Draw(asset,glow);
            rect=new Rect(rect.center.x-rect.width*grow*.5f,rect.center.y-rect.height*grow*.5f,rect.width*grow,rect.height*grow);
            GUI.color=old;ParchmentArt.Draw(asset,rect);
            float phase=Time.unscaledTime%8f;if(phase<.7f){GUI.color=new Color(1f,.93f,.58f,Mathf.Sin(phase/.7f*Mathf.PI)*.65f);GUI.DrawTexture(new Rect(rect.xMax-5,rect.y+5,2,7),Texture2D.whiteTexture);GUI.DrawTexture(new Rect(rect.xMax-8,rect.y+8,8,2),Texture2D.whiteTexture);}
        } else ParchmentArt.Draw(asset,rect);
        GUI.color=old;
        if(EventSystem.current!=null && _noticeSelectable!=null && EventSystem.current.currentSelectedGameObject==_noticeSelectable.gameObject){GUI.color=new Color(1f,.8f,.3f,.9f);GUI.DrawTexture(new Rect(pos.X-3,pos.Y-3,pos.W+6,2),Texture2D.whiteTexture);GUI.DrawTexture(new Rect(pos.X-3,pos.Y+pos.H+1,pos.W+6,2),Texture2D.whiteTexture);GUI.color=old;}
        // Native selectable supplies mouse/controller navigation; IMGUI fallback is only used if no native canvas exists.
        if(_noticeSelectable==null && GUI.Button(rect,new GUIContent("","Patch notes"),GUIStyle.none))OpenPatchNotes();
    }
    void DestroyUpdateArrowTexture(){if(_noticeSelectable!=null)UnityEngine.Object.Destroy(_noticeSelectable.gameObject);if(_ownedNoticeRaycaster!=null)UnityEngine.Object.Destroy(_ownedNoticeRaycaster);}
}
