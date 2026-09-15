using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;
using Kirurobo;
using Newtonsoft.Json;

// Display and input fixes for the local Sola installation. No network access.
public class SolaDisplay : MonoBehaviour {
  public static SolaDisplay Instance;
  [Serializable] public class Preferences {public float uiScale=1.15f;public bool showQuickSettings=true;}
  public Preferences preferences=new Preferences();
  const string PreferencePath=@"D:\MateEngine-Sola\userdata\display.json";
  UniWindowController window; Camera cameraMain; IntPtr nativeWindow,hook;
  MouseProc mouseProc; volatile bool hoverAvatar; int pendingWheel;
  public int wheelEvents; public float lastWheelSize; public bool wheelHookInstalled;
  public string initializationError;
  Canvas quickCanvas; RectTransform panel,launcher; Slider sizeSlider,fontSlider;
  RectTransform quickRoot; Transform modelRoot,settingsRoot; Canvas[] menuCanvases;
  SettingsHandlerSliders[] settingsSliders; SkinnedMeshRenderer[] avatarMeshes=new SkinnedMeshRenderer[0];
  readonly Vector3[] corners=new Vector3[4]; readonly float[] frameTimes=new float[300]; int frameIndex,frameCount;
  float nextAvatarScan,nextLayout,nextWorkspace; int sceneScans; float initializationSeconds; int lastSizePercent=-1;
  TMP_Text sizeLabel,fontLabel; TMP_FontAsset font; Animator avatar;
  readonly List<RaycastResult> raycasts=new List<RaycastResult>(); PointerEventData pointer;
  readonly Dictionary<TMP_Text,float> originalFonts=new Dictionary<TMP_Text,float>();
  readonly Dictionary<Text,int> originalLegacyFonts=new Dictionary<Text,int>();
  readonly Dictionary<CanvasScaler,Vector2> originalReferences=new Dictionary<CanvasScaler,Vector2>();
  float saveAt,lastScreenHeight; bool needSave,initialized;
  public static bool QuickMenuOpen(){return (Instance&&Instance.panel&&Instance.panel.gameObject.activeInHierarchy)||SolaVoice.MenuOpen();}
  public static bool ProperArmature(GameObject model){var a=model.GetComponentInChildren<Animator>();return a&&a.isHuman&&a.avatar&&a.avatar.isValid&&a.GetBoneTransform(HumanBodyBones.Hips);}
  public static void Attach(){if(Instance)return;Instance=new GameObject("Sola Display").AddComponent<SolaDisplay>();DontDestroyOnLoad(Instance.gameObject);}
  static IEnumerable<T> All<T>() where T:Component{return Resources.FindObjectsOfTypeAll<T>().Where(x=>x&&x.gameObject.scene.IsValid());}
  static string PathOf(Transform t){return t.parent?PathOf(t.parent)+"/"+t.name:t.name;}
  IEnumerator Start(){
    yield return null;
    try{
      if(File.Exists(PreferencePath))preferences=JsonConvert.DeserializeObject<Preferences>(File.ReadAllText(PreferencePath))??new Preferences();
      preferences.uiScale=Mathf.Clamp(preferences.uiScale,.9f,1.25f);
      window=UniWindowController.current;cameraMain=Camera.main;
      nativeWindow=System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
      window.SetTransparentType(UniWindowController.TransparentType.ColorKey);window.alphaValue=1f;
      window.isTransparent=true;window.isHitTestEnabled=false;window.isClickThrough=false;
      // Keep the same character pixel size while providing a full-size UI workspace.
      cameraMain.orthographicSize=1.1f*Screen.height/512f;lastScreenHeight=Screen.height;
      var roots=All<Transform>().Where(t=>!t.parent).ToArray();
      modelRoot=roots.First(t=>t.name=="Model");settingsRoot=roots.First(t=>t.name=="Settings");
      settingsSliders=settingsRoot.GetComponentsInChildren<SettingsHandlerSliders>(true);
      menuCanvases=settingsRoot.GetComponentsInChildren<Canvas>(true).Where(c=>c.isRootCanvas&&c.name.EndsWith("MenuCanvas")).ToArray();
      font=settingsRoot.GetComponentsInChildren<TextMeshProUGUI>(true).Where(t=>t.font&&PathOf(t.transform).StartsWith("Settings/SettingsMenuCanvas/")).Select(t=>t.font).First();
      foreach(var old in All<AvatarScaleController>())old.enabled=false;
      BuildQuickSettings();RefreshScene();UpdateAvatar();
      mouseProc=OnMouse;hook=SetWindowsHookEx(14,mouseProc,GetModuleHandle(null),0);wheelHookInstalled=hook!=IntPtr.Zero;
      if(!wheelHookInstalled)Debug.LogWarning("Sola wheel hook could not start; Unity wheel input is used.");
      initialized=true;initializationSeconds=Time.realtimeSinceStartup;Debug.Log("Sola display ready at "+initializationSeconds+" seconds; lightweight UI, no cloned menus.");
    }catch(Exception e){initializationError=e.ToString();Debug.LogError("Sola display: "+e);}
  }
  void RefreshScene(){
    sceneScans++;
    foreach(var sliders in settingsSliders)if(sliders&&sliders.avatarSizeSlider){sliders.avatarSizeSlider.minValue=.35f;sliders.avatarSizeSlider.maxValue=2.4f;}
    foreach(var scaler in settingsRoot.GetComponentsInChildren<CanvasScaler>(true)){
      if(quickCanvas&&scaler.transform.IsChildOf(quickCanvas.transform))continue;
      if(scaler.uiScaleMode!=CanvasScaler.ScaleMode.ScaleWithScreenSize)continue;
      if(!originalReferences.ContainsKey(scaler))originalReferences[scaler]=scaler.referenceResolution;
      var reference=originalReferences[scaler];
      // Enlarge original menus together, retaining their scroll views and layouts.
      scaler.referenceResolution=reference/preferences.uiScale;
    }
    foreach(var text in settingsRoot.GetComponentsInChildren<TMP_Text>(true)){
      if(quickCanvas&&text.transform.IsChildOf(quickCanvas.transform))continue;
      var path=PathOf(text.transform);
      if(!path.StartsWith("Settings/")||path.Contains("/Model Info/")||path.Contains("/Template/")&&path.Contains("/Viewport/Content/Item"))continue;
      if(!originalFonts.ContainsKey(text))originalFonts[text]=text.fontSize;
      float start=originalFonts[text];
      // Small debug counters are not expanded into neighboring buttons.
      if(start<12)continue;
      text.enableAutoSizing=false;text.fontSize=Mathf.Max(start,20f);
    }
    foreach(var text in settingsRoot.GetComponentsInChildren<Text>(true)){
      if(!PathOf(text.transform).StartsWith("Settings/ChatBot AI/"))continue;
      if(!originalLegacyFonts.ContainsKey(text))originalLegacyFonts[text]=text.fontSize;
      text.fontSize=Math.Max(originalLegacyFonts[text],18);text.resizeTextForBestFit=false;
    }
  }
  void UpdateAvatar(){
    var found=modelRoot.GetComponentsInChildren<Animator>(false).FirstOrDefault(a=>a.isHuman);
    if(found==avatar)return;avatar=found;
    avatarMeshes=avatar?avatar.GetComponentsInChildren<SkinnedMeshRenderer>():new SkinnedMeshRenderer[0];
    RefreshScene();
  }
  void Update(){
    if(!initialized)return;
    if(Input.GetKeyDown(KeyCode.Escape)&&QuickMenuOpen())SetQuickOpen(false);
    frameTimes[frameIndex++%frameTimes.Length]=Time.unscaledDeltaTime;frameCount=Math.Min(frameCount+1,frameTimes.Length);
    if(Time.unscaledTime>=nextAvatarScan){UpdateAvatar();nextAvatarScan=Time.unscaledTime+.5f;}
    if(Time.unscaledTime>=nextWorkspace){FitWorkspace();nextWorkspace=Time.unscaledTime+.4f;}
    if(needSave&&Time.unscaledTime>=saveAt){Save();}
    var point=ClientPoint();hoverAvatar=CanScaleAt(point);
    int delta=Interlocked.Exchange(ref pendingWheel,0);
    if(!wheelHookInstalled&&hoverAvatar)delta=(int)(Input.mouseScrollDelta.y*120f);
    if(delta!=0){wheelEvents++;SetModelSize(SaveLoadHandler.Instance.data.avatarSize*(float)Math.Pow(1.08,delta/120f));lastWheelSize=SaveLoadHandler.Instance.data.avatarSize;}
    if(sizeSlider){sizeSlider.SetValueWithoutNotify(SaveLoadHandler.Instance.data.avatarSize);int percent=Mathf.RoundToInt(SaveLoadHandler.Instance.data.avatarSize/1.2f*100);if(percent!=lastSizePercent){lastSizePercent=percent;sizeLabel.text=percent+"%";}}
    PositionQuickSettings();
    // Big-screen mode controls the camera itself. Resizing normal mode keeps pixel size stable.
    if(Math.Abs(Screen.height-lastScreenHeight)>.5f){
      if(!avatar||!avatar.GetBool("isBigScreen"))cameraMain.orthographicSize*=Screen.height/lastScreenHeight;
      lastScreenHeight=Screen.height;
    }
  }
  void FitWorkspace(){
    if(!avatar||avatar.GetBool("isBigScreen")||avatar.GetBool("isDragging"))return;
    bool expanded=QuickMenuOpen()||menuCanvases.Any(c=>c&&c.gameObject.activeInHierarchy);
    int height=expanded?1024:Math.Max(640,Mathf.CeilToInt(SaveLoadHandler.Instance.data.avatarSize*400f+60f));
    int width=expanded?1536:Math.Max(900,Mathf.CeilToInt(height*1.4f));
    var current=window.windowSize;
    if(Math.Abs(current.x-width)<4&&Math.Abs(current.y-height)<4)return;
    var position=window.windowPosition;
    window.windowSize=new Vector2(width,height);
    window.windowPosition=position+(current-new Vector2(width,height))*.5f;
  }
  void LateUpdate(){
    if(!initialized)return;
    if(Time.unscaledTime<nextLayout)return;nextLayout=Time.unscaledTime+.1f;
    // A readable panel is fitted inside the render window instead of being cropped.
    foreach(var canvas in menuCanvases.Where(c=>c&&c.gameObject.activeInHierarchy)){
      var children=new List<RectTransform>();
      foreach(Transform child in canvas.transform)if(child.gameObject.activeSelf&&child is RectTransform rect)children.Add(rect);
      if(children.Count==0)continue;
      float xmin=1e30f,xmax=-1e30f,ymin=1e30f,ymax=-1e30f;
      foreach(var rect in children){rect.GetWorldCorners(corners);foreach(var corner in corners){var p=RectTransformUtility.WorldToScreenPoint(canvas.worldCamera,corner);xmin=Mathf.Min(xmin,p.x);xmax=Mathf.Max(xmax,p.x);ymin=Mathf.Min(ymin,p.y);ymax=Mathf.Max(ymax,p.y);}}
      float dx=xmin<12?12-xmin:xmax>Screen.width-12?Screen.width-12-xmax:0;
      float dy=ymin<12?12-ymin:ymax>Screen.height-12?Screen.height-12-ymax:0;
      if(Math.Abs(dx)+Math.Abs(dy)>.1f)foreach(var rect in children)rect.anchoredPosition+=new Vector2(dx,dy)/canvas.scaleFactor;
    }
  }
  Vector2 ClientPoint(){POINT pt;RECT rect;GetCursorPos(out pt);GetClientRect(nativeWindow,out rect);ScreenToClient(nativeWindow,ref pt);return new Vector2(pt.x*(float)Screen.width/Math.Max(1,rect.right),Screen.height-pt.y*(float)Screen.height/Math.Max(1,rect.bottom));}
  bool CanScaleAt(Vector2 point){
    if(!avatar||avatar.GetBool("isBigScreen")||avatar.GetBool("isDragging"))return false;
    if(point.x<0||point.y<0||point.x>=Screen.width||point.y>=Screen.height)return false;
    if(!AvatarScreenRect().Contains(point))return false;
    if(EventSystem.current){
      if(pointer==null)pointer=new PointerEventData(EventSystem.current);pointer.position=point;raycasts.Clear();EventSystem.current.RaycastAll(pointer,raycasts);
      if(raycasts.Any(r=>r.gameObject.layer!=2&&r.gameObject.GetComponent<Graphic>()))return false;
    }
    return true;
  }
  Rect AvatarScreenRect(){
    if(!avatar)return new Rect(Screen.width/2-60,Screen.height/2-180,120,360);
    float xmin=1e30f,xmax=-1e30f,ymin=1e30f,ymax=-1e30f;
    foreach(var mesh in avatarMeshes){
      if(!mesh||!mesh.enabled||!mesh.gameObject.activeInHierarchy)continue;var b=mesh.bounds;
      for(int i=0;i<8;i++){var p=cameraMain.WorldToScreenPoint(b.center+Vector3.Scale(b.extents,new Vector3((i&1)==0?-1:1,(i&2)==0?-1:1,(i&4)==0?-1:1)));xmin=Mathf.Min(xmin,p.x);xmax=Mathf.Max(xmax,p.x);ymin=Mathf.Min(ymin,p.y);ymax=Mathf.Max(ymax,p.y);}
    }
    return xmin==1e30f?new Rect():Rect.MinMaxRect(xmin,ymin,xmax,ymax);
  }
  public void SetModelSize(float value){
    value=Mathf.Clamp(value,.35f,2.4f);SaveLoadHandler.Instance.data.avatarSize=value;
    foreach(var s in settingsSliders)if(s&&s.avatarSizeSlider)s.avatarSizeSlider.SetValueWithoutNotify(value);
    foreach(var controller in modelRoot.GetComponentsInChildren<AvatarAnimatorController>(true))controller.transform.localScale=Vector3.one*value;
    QueueSave();
  }
  public void SetUIScale(float value){preferences.uiScale=Mathf.Clamp(value,.9f,1.25f);fontLabel.text=Mathf.RoundToInt(preferences.uiScale*100)+"%";RefreshScene();QueueSave();}
  void QueueSave(){needSave=true;saveAt=Time.unscaledTime+.4f;}
  void Save(){needSave=false;SaveLoadHandler.Instance?.SaveToDisk();File.WriteAllText(PreferencePath,JsonConvert.SerializeObject(preferences,Formatting.Indented));}
  void SetQuickOpen(bool value){panel.gameObject.SetActive(value);preferences.showQuickSettings=value;QueueSave();Debug.Log("Sola display panel "+(value?"opened":"closed"));}
  void OpenOriginalSettings(){SetQuickOpen(false);foreach(var menu in All<MenuActions>())menu.CloseAllMenus();var target=Resources.FindObjectsOfTypeAll<GameObject>().First(g=>g.scene.IsValid()&&PathOf(g.transform)=="Settings/SettingsMenuCanvas");target.SetActive(true);}
  RectTransform Box(string name,Transform parent,float x,float y,float w,float h,Color? color=null){
    var go=new GameObject(name,typeof(RectTransform));go.layer=5;var r=go.GetComponent<RectTransform>();r.SetParent(parent,false);r.anchorMin=r.anchorMax=r.pivot=new Vector2(0,1);r.anchoredPosition=new Vector2(x,-y);r.sizeDelta=new Vector2(w,h);
    if(color.HasValue){var img=go.AddComponent<Image>();img.color=color.Value;}return r;
  }
  TMP_Text Label(string name,Transform parent,string value,float x,float y,float w,float h,int size,Color? color=null){
    var rect=Box(name,parent,x,y,w,h);var text=rect.gameObject.AddComponent<TextMeshProUGUI>();text.font=font;text.fontSize=size;text.text=value;text.color=color??Color.white;text.enableAutoSizing=false;text.raycastTarget=false;text.alignment=TextAlignmentOptions.MidlineLeft;return text;
  }
  Button ActionButton(string name,Transform parent,string text,float x,float y,float w,float h,UnityEngine.Events.UnityAction action){
    var r=Box(name,parent,x,y,w,h,new Color(.19f,.23f,.30f,1));var b=r.gameObject.AddComponent<Button>();b.targetGraphic=r.GetComponent<Image>();b.onClick.AddListener(action);var label=Label("Label",r,text,0,0,w,h,18);label.alignment=TextAlignmentOptions.Center;return b;
  }
  Slider MakeSlider(string name,Transform parent,float x,float y,float w,float min,float max,float value,UnityEngine.Events.UnityAction<float> changed){
    var r=Box(name,parent,x,y,w,36);var background=Box("Track",r,0,15,w,6,new Color(.30f,.34f,.42f,1));var handleArea=Box("HandleArea",r,10,0,w-20,36);
    var handle=Box("Handle",handleArea,0,0,20,-12,new Color(.67f,.91f,.82f,1));
    var slider=r.gameObject.AddComponent<Slider>();slider.targetGraphic=handle.GetComponent<Image>();slider.handleRect=handle;slider.minValue=min;slider.maxValue=max;slider.SetValueWithoutNotify(value);slider.onValueChanged.AddListener(changed);return slider;
  }
  void BuildQuickSettings(){
    var root=new GameObject("Sola Display UI",typeof(RectTransform));root.layer=5;quickRoot=root.GetComponent<RectTransform>();quickCanvas=root.AddComponent<Canvas>();quickCanvas.sortingOrder=1200;
    // A new WorldSpace canvas defaults to this mode in the shipped player. Put it
    // immediately in front of the camera, with a one-to-one pixel coordinate space.
    root.AddComponent<SolaUIRaycaster>();
    DontDestroyOnLoad(root);
    panel=Box("显示设置",root.transform,0,0,420,390,new Color(.065f,.082f,.11f,1));
    Label("Title",panel,"Sola · 显示设置",24,18,324,40,26);
    ActionButton("Close",panel,"×",364,16,38,38,()=>SetQuickOpen(false));
    Label("SizeTitle",panel,"模型大小",24,78,240,28,20);sizeLabel=Label("SizeValue",panel,"100%",316,78,80,28,20);
    sizeSlider=MakeSlider("模型大小滑块",panel,76,118,268,.35f,2.4f,SaveLoadHandler.Instance.data.avatarSize,SetModelSize);
    ActionButton("Smaller",panel,"−",24,118,40,36,()=>SetModelSize(SaveLoadHandler.Instance.data.avatarSize/1.15f));
    ActionButton("Larger",panel,"+",356,118,40,36,()=>SetModelSize(SaveLoadHandler.Instance.data.avatarSize*1.15f));
    Label("FontTitle",panel,"菜单字号",24,182,240,28,20);fontLabel=Label("FontValue",panel,Mathf.RoundToInt(preferences.uiScale*100)+"%",316,182,80,28,20);
    fontSlider=MakeSlider("菜单字号滑块",panel,24,220,372,.9f,1.25f,preferences.uiScale,SetUIScale);
    Label("Hint",panel,"鼠标移到角色上，滚轮即可缩放。\n调整会自动保存，重启后仍然有效。",24,270,372,52,18,new Color(.77f,.82f,.90f,1));
    ActionButton("OriginalSettings",panel,"完整设置",24,338,178,36,OpenOriginalSettings);
    ActionButton("ResetDisplay",panel,"默认大小",218,338,178,36,()=>SetModelSize(1.2f));
    launcher=ActionButton("显示设置入口",root.transform,"显示设置",0,0,108,38,()=>SetQuickOpen(!panel.gameObject.activeSelf)).GetComponent<RectTransform>();
    panel.gameObject.SetActive(preferences.showQuickSettings);root.SetActive(true);
  }
  void PositionQuickSettings(){
    if(!launcher)return;
    quickRoot.sizeDelta=new Vector2(Screen.width,Screen.height);quickRoot.pivot=new Vector2(.5f,.5f);
    quickRoot.position=cameraMain.ScreenToWorldPoint(new Vector3(Screen.width*.5f,Screen.height*.5f,cameraMain.nearClipPlane+.1f));
    quickRoot.rotation=cameraMain.transform.rotation;quickRoot.localScale=Vector3.one*(cameraMain.orthographicSize*2f/Screen.height);
    if(!avatar)return;
    bool big=avatar.GetBool("isBigScreen");launcher.gameObject.SetActive(!big);
    if(big){panel.gameObject.SetActive(false);return;}
    var bounds=AvatarScreenRect();var hip=avatar.GetBoneTransform(HumanBodyBones.Hips);var center=cameraMain.WorldToScreenPoint(hip.position);
    float x=Screen.width*.5f-54;float y=Screen.height-54;
    launcher.anchoredPosition=new Vector2(x,-y);
    float px=Screen.width-452;
    px=Mathf.Clamp(px,16,Math.Max(16,Screen.width-436));float py=Mathf.Clamp(Screen.height/2f-195,16,Math.Max(16,Screen.height-406));
    panel.anchoredPosition=new Vector2(px,-py);
  }
  IntPtr OnMouse(int code,IntPtr message,IntPtr data){
    if(code>=0&&message==(IntPtr)0x020A&&hoverAvatar){
      var mouse=Marshal.PtrToStructure<MOUSE>(data);
      if(GetAncestor(WindowFromPoint(mouse.point),2)==nativeWindow){Interlocked.Add(ref pendingWheel,(short)(mouse.mouseData>>16));return (IntPtr)1;}
    }
    return CallNextHookEx(hook,code,message,data);
  }
  public object Hits(float x,float y){var p=new PointerEventData(EventSystem.current);p.position=new Vector2(x,y);var hits=new List<RaycastResult>();EventSystem.current.RaycastAll(p,hits);return hits.Select(r=>new{path=PathOf(r.gameObject.transform),r.distance,r.depth,r.sortingOrder}).ToArray();}
  public object Status(){float total=0f,worst=0f;for(int i=0;i<frameCount;i++){total+=frameTimes[i];worst=Mathf.Max(worst,frameTimes[i]);}uint key,flags;byte opacity;bool nativeAlpha=GetLayeredWindowAttributes(nativeWindow,out key,out opacity,out flags);return new{initialized,initializationError,initializationSeconds,sceneScans,wheelHookInstalled,wheelEvents,lastWheelSize,hoverAvatar,modelSize=SaveLoadHandler.Instance.data.avatarSize,preferences.uiScale,windowTransparency=window?.transparentType.ToString(),nativeAlpha,key,opacity,flags,screen=new{Screen.width,Screen.height},menu=QuickMenuOpen(),averageFrameMs=frameCount==0?0:total/frameCount*1000f,worstFrameMs=frameCount==0?0:worst*1000f,uiObjects=quickRoot?quickRoot.GetComponentsInChildren<Transform>(true).Length:0};}
  void OnDestroy(){if(hook!=IntPtr.Zero)UnhookWindowsHookEx(hook);if(needSave)Save();if(quickCanvas)Destroy(quickCanvas.gameObject);}
  delegate IntPtr MouseProc(int code,IntPtr message,IntPtr data);
  struct POINT{public int x,y;}
  struct RECT{public int left,top,right,bottom;}
  struct MOUSE{public POINT point;public uint mouseData,flags,time;public UIntPtr extra;}
  [DllImport("user32.dll")]static extern IntPtr SetWindowsHookEx(int kind,MouseProc proc,IntPtr module,uint thread);
  [DllImport("user32.dll")]static extern bool UnhookWindowsHookEx(IntPtr hook);
  [DllImport("user32.dll")]static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr message,IntPtr data);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]static extern IntPtr GetModuleHandle(string module);
  [DllImport("user32.dll")]static extern IntPtr WindowFromPoint(POINT point);
  [DllImport("user32.dll")]static extern IntPtr GetAncestor(IntPtr window,uint flags);
  [DllImport("user32.dll")]static extern bool GetCursorPos(out POINT point);
  [DllImport("user32.dll")]static extern bool ScreenToClient(IntPtr window,ref POINT point);
  [DllImport("user32.dll")]static extern bool GetClientRect(IntPtr window,out RECT rect);
  [DllImport("user32.dll")]static extern bool GetLayeredWindowAttributes(IntPtr window,out uint key,out byte alpha,out uint flags);
}

// The original transparent drag surface is an Overlay canvas. Its default
// raycaster sorts before world-space UI, regardless of Canvas.sortingOrder.
// Give this small UI an explicit input priority so buttons receive the click.
public class SolaUIRaycaster : GraphicRaycaster {
  public override int sortOrderPriority {get{return 32000;}}
  public override int renderOrderPriority {get{return 32000;}}
}
