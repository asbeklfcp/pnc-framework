using UnityEngine;
namespace PncModManager;
public sealed partial class Plugin {
    readonly UpdateNoticeState _gameUpdateNotice=new UpdateNoticeState();
    AudioSource _updateChimeSource;
    AudioClip _updateChimeClip;
    void ObserveUpdateOffer(){
        if(_updateOffer==null || _gameUpdateNotice.LastRelease==_updateOffer.Id)return;
        var player=UnityEngine.Object.FindAnyObjectByType<PlayerStats>();
        bool playing=player!=null && player.isActiveAndEnabled && _mainMenu==null && Time.timeScale>0f && !CaptureIsActive() && !_visible && !_patchNotesOpen;
        _gameUpdateNotice.Observe(_updateOffer.Id,playing,Time.unscaledTime);
        PlayUpdateChime();
        Logger.LogInfo("[UPDATER] New offer "+_updateOffer.Id+"; gameplay="+playing+"; arrow hold20s/fade0.6s; border5s; chime2s; no gameplay input changes");
    }
    void PlayUpdateChime(){
        if(_updateChimeSource==null){
            _updateChimeSource=gameObject.AddComponent<AudioSource>();_updateChimeSource.playOnAwake=false;
            _updateChimeSource.spatialBlend=0;_updateChimeSource.loop=false;_updateChimeSource.volume=.35f;
            _updateChimeSource.ignoreListenerPause=true;
            _updateChimeClip=AudioClip.Create("PNC update chime",UpdateChime.Rate*2,1,UpdateChime.Rate,false);
            _updateChimeClip.SetData(UpdateChime.Samples(),0);_updateChimeSource.clip=_updateChimeClip;
        }
        _updateChimeSource.Play();
    }
    void DestroyUpdateNotice(){
        if(_updateChimeSource!=null){_updateChimeSource.Stop();UnityEngine.Object.Destroy(_updateChimeSource);}
        if(_updateChimeClip!=null)UnityEngine.Object.Destroy(_updateChimeClip);
    }
    void DrawGameplayUpdateNotice(){
        Color old=GUI.color;
        try{
            float border=_gameUpdateNotice.BorderAlpha(Time.unscaledTime);
            if(border>0)DrawUpdateBorder(border);
            float alpha=_gameUpdateNotice.Alpha(Time.unscaledTime);
            if(alpha<=0 || _visible || _patchNotesOpen || Time.timeScale<=0 || CaptureIsActive())return;
            Rect safe=Screen.safeArea;float size=Mathf.Clamp(Screen.height/1080f*38f,32f,72f);
            float grow=1f+.04f*Mathf.Sin(Time.unscaledTime*3f);
            Rect rect=new Rect(safe.xMax-size-12f,Screen.height-safe.yMin-size-10f,size,size);
            rect=new Rect(rect.center.x-size*grow*.5f,rect.center.y-size*grow*.5f,size*grow,size*grow);
            GUI.color=new Color(1f,1f,1f,alpha);ParchmentArt.Draw("PNC_UpdateArrow_Available.png",rect);
            float phase=Time.unscaledTime*2.1f;float sparkle=Mathf.Max(0,Mathf.Sin(phase));
            GUI.color=new Color(1f,.93f,.55f,alpha*sparkle);
            DrawSparkle(rect.xMax-3,rect.y+5,Mathf.Max(2,size*.10f));
        }finally{GUI.color=old;}
    }
    static void DrawSparkle(float x,float y,float size){
        GUI.DrawTexture(new Rect(x-size,y-1,size*2,2),Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x-1,y-size,2,size*2),Texture2D.whiteTexture);
    }
    static void DrawUpdateBorder(float alpha){
        float w=Screen.width,h=Screen.height;
        float thickness=(float)UpdateBorderLayout.Thickness(w,h);
        GUI.color=new Color(1f,.70f,.12f,alpha*.45f);
        foreach(var strip in UpdateBorderLayout.Strips(w,h))GUI.DrawTexture(new Rect((float)strip.X,(float)strip.Y,(float)strip.W,(float)strip.H),Texture2D.whiteTexture);
        float perimeter=2*(w+h);
        for(int i=0;i<32;i++){
            float pos=(i/32f*perimeter+Time.unscaledTime*65f)%perimeter,x,y;
            if(pos<w){x=pos;y=thickness*.5f;}else if((pos-=w)<h){x=w-thickness*.5f;y=pos;}else if((pos-=h)<w){x=w-pos;y=h-thickness*.5f;}else{x=thickness*.5f;y=h-(pos-w);}
            float shine=.35f+.65f*Mathf.Pow(Mathf.Max(0,Mathf.Sin(Time.unscaledTime*4+i*2.3f)),3);
            GUI.color=new Color(1f,.94f,.60f,alpha*shine);DrawSparkle(x,y,thickness*.8f);
        }
    }
}
