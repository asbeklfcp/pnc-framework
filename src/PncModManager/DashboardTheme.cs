using System.Collections.Generic;
using UnityEngine;
namespace PncModManager;
public sealed partial class Plugin {
    GUISkin _dashboardSkin;
    Font _appliedThemeFont;
    readonly List<Texture2D> _themeTextures=new List<Texture2D>();
    static readonly Color Ink=new Color(.24f,.105f,.035f,1f);
    Texture2D Panel(Color color){return Frame(color,new Color(.35f,.16f,.045f,.7f));}
    Texture2D Frame(Color fill,Color edge) {
        var t=new Texture2D(9,9,TextureFormat.RGBA32,false){filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp};
        for(int y=0;y<9;y++)for(int x=0;x<9;x++)t.SetPixel(x,y,x<1 || x>7 || y<1 || y>7?edge:fill);
        t.Apply();_themeTextures.Add(t);return t;
    }
    void State(GUIStyle style,Texture2D normal,Texture2D hover){
        style.normal.background=normal;style.hover.background=hover;style.active.background=hover;style.focused.background=hover;
        style.onNormal.background=hover;style.onHover.background=hover;style.onActive.background=hover;style.onFocused.background=hover;
        style.border=new RectOffset(2,2,2,2);
        foreach(var state in new[]{style.normal,style.hover,style.active,style.focused,style.onNormal,style.onHover,style.onActive,style.onFocused})state.textColor=Ink;
    }
    void EnsureDashboardTheme() {
        if(_dashboardSkin!=null){ApplyNativeThemeFont();return;}
        _dashboardSkin=UnityEngine.Object.Instantiate(GUI.skin);
        _titleStyle=null;
        if(NativeTypeface.Font!=null)_dashboardSkin.font=NativeTypeface.Font;
        var clear=Frame(Color.clear,Color.clear);
        var paper=Frame(new Color(1f,.88f,.61f,.62f),new Color(.4f,.19f,.045f,.55f));
        var button=Frame(new Color(.92f,.59f,.18f,.72f),new Color(.35f,.14f,.025f,1f));
        var hover=Frame(new Color(1f,.78f,.32f,.88f),new Color(.48f,.22f,.045f,1f));
        var field=Frame(new Color(1f,.91f,.65f,.82f),new Color(.48f,.24f,.07f,.85f));
        State(_dashboardSkin.window,clear,clear);_dashboardSkin.window.padding=new RectOffset(85,85,100,95);
        State(_dashboardSkin.box,paper,paper);_dashboardSkin.box.padding=new RectOffset(12,12,10,10);
        State(_dashboardSkin.button,button,hover);_dashboardSkin.button.padding=new RectOffset(10,10,7,7);_dashboardSkin.button.fontSize=17;_dashboardSkin.button.wordWrap=true;
        _dashboardSkin.label.fontSize=17;_dashboardSkin.label.normal.textColor=Ink;_dashboardSkin.label.wordWrap=true;
        State(_dashboardSkin.textField,field,hover);_dashboardSkin.textField.padding=new RectOffset(9,9,6,6);_dashboardSkin.textField.fontSize=17;
        State(_dashboardSkin.textArea,field,hover);_dashboardSkin.textArea.fontSize=17;
        State(_dashboardSkin.toggle,field,hover);_dashboardSkin.toggle.fontSize=17;
        var checkedBox=Frame(new Color(.58f,.32f,.055f,1f),new Color(.27f,.10f,.02f,1f));
        for(int x=2;x<=6;x++)checkedBox.SetPixel(x,4,new Color(1f,.88f,.5f,1));checkedBox.Apply();
        _dashboardSkin.toggle.onNormal.background=checkedBox;_dashboardSkin.toggle.onHover.background=checkedBox;
        foreach(var s in new[]{_dashboardSkin.verticalScrollbar,_dashboardSkin.horizontalScrollbar,_dashboardSkin.horizontalSlider,_dashboardSkin.verticalSlider})State(s,paper,paper);
        foreach(var s in new[]{_dashboardSkin.verticalScrollbarThumb,_dashboardSkin.horizontalScrollbarThumb,_dashboardSkin.horizontalSliderThumb,_dashboardSkin.verticalSliderThumb})State(s,button,hover);
        _dashboardSkin.verticalScrollbar.fixedWidth=16;_dashboardSkin.horizontalScrollbar.fixedHeight=16;
        _dashboardSkin.settings.selectionColor=new Color(.63f,.32f,.04f,.5f);_dashboardSkin.settings.cursorColor=Ink;
        ApplyNativeThemeFont();
    }
    void ApplyNativeThemeFont(){
        Font font=NativeTypeface.Font;if(font==null || font==_appliedThemeFont)return;_appliedThemeFont=font;_dashboardSkin.font=font;
        foreach(var style in new[]{_dashboardSkin.window,_dashboardSkin.box,_dashboardSkin.button,_dashboardSkin.label,_dashboardSkin.toggle,_dashboardSkin.textField,_dashboardSkin.textArea})style.font=font;
        foreach(var style in new[]{_titleStyle,_sectionStyle,_descriptionStyle,_selectedButtonStyle,_errorStyle,_fallbackMenuButtonStyle,_profilePanelStyle,_profileHeadingStyle,_collapsibleHeaderStyle,_activeProfileButtonStyle,_modeBadgeStyle,_modeBadgeTitleStyle,_modeBadgeValueStyle,_notesHeading,_notesBody,_notesCategory,_notesEntry,_notesDate})if(style!=null)style.font=font;
    }
    void DestroyDashboardTheme(){
        if(_visible)SetVisible(false);
        if(_dashboardSkin!=null)UnityEngine.Object.Destroy(_dashboardSkin);
        foreach(var texture in _themeTextures)UnityEngine.Object.Destroy(texture);_themeTextures.Clear();ParchmentArt.Dispose();NativeTypeface.Dispose();
    }
}

