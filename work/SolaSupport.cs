using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// Local compatibility fixes. Development commands only with --sola-diagnostics.
public class SolaSupport : MonoBehaviour {
  static SolaSupport instance;
  static readonly bool Diagnostics=Environment.GetCommandLineArgs().Contains("--sola-diagnostics");
  static readonly string Work = @"D:\MateEngine-Sola\work";
  string lastId=""; float nextPoll; readonly List<string> errors=new List<string>();
  public static void Attach() {
    if(instance) return;
    instance=new GameObject("Sola Compatibility Support").AddComponent<SolaSupport>();
    DontDestroyOnLoad(instance.gameObject);
  }
  void Awake(){try{lastId=(string)JObject.Parse(File.ReadAllText(System.IO.Path.Combine(Work,"command.json")))["id"];}catch{}}
  void Start(){
    SolaDisplay.Attach();
    foreach(var character in All<LLMUnity.LLMCharacter>()){
      if(String.IsNullOrWhiteSpace(character.playerName))character.playerName="user";
      if(String.IsNullOrWhiteSpace(character.AIName))character.AIName="assistant";
    }
  }
  public static void SetOccluderVisibility(MonoBehaviour handler,int count){
    var field=handler.GetType().GetField("otherQuadGOs",BindingFlags.NonPublic|BindingFlags.Instance);
    var objects=(List<GameObject>)field.GetValue(handler);
    for(int i=0;i<objects.Count;i++){var obj=objects[i];if(obj&&obj.activeSelf!=(i<count))obj.SetActive(i<count);}
  }
  public static void InitializeLanguage(MonoBehaviour handler){Attach();instance.StartCoroutine(WaitForLocale(handler));}
  static IEnumerator WaitForLocale(MonoBehaviour handler){
    var initialization=UnityEngine.Localization.Settings.LocalizationSettings.InitializationOperation;
    yield return initialization;
    if(!handler)yield break;
    if(initialization.Status!=UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded){Debug.LogError("Sola: localization initialization failed: "+initialization.OperationException);yield break;}
    handler.GetType().GetMethod("SolaInitializeLanguage",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(handler,null);
  }
  void OnDestroy(){}
  void OnLog(string text,string stack,LogType type){if(type==LogType.Error||type==LogType.Exception){errors.Add(text+"\n"+stack);if(errors.Count>40)errors.RemoveAt(0);}}
  static string PathOf(Transform t){return t.parent?PathOf(t.parent)+"/"+t.name:t.name;}
  static IEnumerable<T> All<T>() where T:Component{return Resources.FindObjectsOfTypeAll<T>().Where(c=>c&&c.gameObject.scene.IsValid());}
  static Component Find(string type,string path){
    var list=All<Component>().Where(c=>c.GetType().Name==type && (String.IsNullOrEmpty(path)?c.gameObject.activeInHierarchy:PathOf(c.transform)==path)).ToArray();
    if(list.Length!=1)throw new Exception("Expected one "+type+" at "+path+", got "+list.Length+": "+String.Join(";",list.Select(c=>PathOf(c.transform))));
    return list[0];
  }
  void Update(){
    if(!Diagnostics)return;
    if(Time.realtimeSinceStartup<nextPoll)return;nextPoll=Time.realtimeSinceStartup+.15f;
    var file=System.IO.Path.Combine(Work,"command.json");if(!File.Exists(file))return;
    JObject c; try{c=JObject.Parse(File.ReadAllText(file));}catch{return;}
    string id=(string)c["id"];if(id==lastId)return;lastId=id;
    StartCoroutine(Run(c,id));
  }
  IEnumerator Run(JObject c,string id){
    object value=null;Exception failure=null;string op=(string)c["op"];
    if(op=="capture"){
      yield return new WaitForEndOfFrame();
      var target=System.IO.Path.Combine(Work,(string)c["filename"]??"runtime.png");
      var tex=new Texture2D(Screen.width,Screen.height,TextureFormat.RGBA32,false);tex.ReadPixels(new Rect(0,0,Screen.width,Screen.height),0,0);tex.Apply();File.WriteAllBytes(target,tex.EncodeToPNG());Destroy(tex);value=target;
    } else if(op=="portrait"){
      yield return new WaitForEndOfFrame();
      var cam=Camera.main;var avatar=All<Animator>().First(a=>a.isHuman&&a.gameObject.activeInHierarchy);
      var head=avatar.GetBoneTransform(HumanBodyBones.Head);var oldPos=cam.transform.position;float oldSize=cam.orthographicSize;var oldTarget=cam.targetTexture;var oldActive=RenderTexture.active;
      var target=new RenderTexture(1024,1024,24,RenderTextureFormat.ARGB32);target.antiAliasing=4;
      cam.targetTexture=target;cam.orthographicSize=(float?)c["size"]??.2f;cam.transform.position=new Vector3(head.position.x,head.position.y+.065f,oldPos.z);
      cam.Render();RenderTexture.active=target;var tex=new Texture2D(1024,1024,TextureFormat.RGBA32,false);tex.ReadPixels(new Rect(0,0,1024,1024),0,0);tex.Apply();
      value=System.IO.Path.Combine(Work,(string)c["filename"]??"portrait.png");File.WriteAllBytes((string)value,tex.EncodeToPNG());
      cam.targetTexture=oldTarget;cam.transform.position=oldPos;cam.orthographicSize=oldSize;RenderTexture.active=oldActive;Destroy(tex);target.Release();Destroy(target);
    } else try {
      if(op=="snapshot")value=Snapshot();
      else if(op=="display")value=SolaDisplay.Instance.Status();
      else if(op=="locale"){
        var init=UnityEngine.Localization.Settings.LocalizationSettings.InitializationOperation;
        value=new{init.IsDone,status=init.Status.ToString(),error=init.OperationException?.ToString(),locales=UnityEngine.Localization.Settings.LocalizationSettings.AvailableLocales.Locales.Select(l=>new{l.name,code=l.Identifier.Code}).ToArray(),selected=UnityEngine.Localization.Settings.LocalizationSettings.SelectedLocale?.Identifier.Code};
      }
      else if(op=="active"){
        var objects=Resources.FindObjectsOfTypeAll<GameObject>().Where(g=>g.scene.IsValid()&&PathOf(g.transform)==(string)c["path"]).ToArray();if(objects.Length!=1)throw new Exception("Ambiguous object");objects[0].SetActive((bool)c["value"]);
      }
      else if(op=="ui")value=Ui();
      else if(op=="components")value=All<MonoBehaviour>().Select(x=>new{type=x.GetType().Name,path=PathOf(x.transform),active=x.gameObject.activeInHierarchy,enabled=x.enabled}).ToArray();
      else if(op=="inspect"){
        var obj=Find((string)c["type"],(string)c["path"]);
        value=obj.GetType().GetFields(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance).Select(f=>new{name=f.Name,type=f.FieldType.FullName,value=SafeValue(f.GetValue(obj))}).ToArray();
      }
      else if(op=="button")((Button)Find("Button",(string)c["path"])).onClick.Invoke();
      else if(op=="pointer"){
        var events=UnityEngine.EventSystems.EventSystem.current;
        var pointer=new UnityEngine.EventSystems.PointerEventData(events){position=new Vector2((float)c["x"],(float)c["y"]),button=UnityEngine.EventSystems.PointerEventData.InputButton.Left};
        var hits=new List<UnityEngine.EventSystems.RaycastResult>();events.RaycastAll(pointer,hits);
        if(hits.Count==0)throw new Exception("No UI hit at diagnostic pointer location");
        pointer.pointerCurrentRaycast=hits[0];pointer.pointerPressRaycast=hits[0];pointer.eligibleForClick=true;
        var target=UnityEngine.EventSystems.ExecuteEvents.ExecuteHierarchy(hits[0].gameObject,pointer,UnityEngine.EventSystems.ExecuteEvents.pointerDownHandler);
        if(!target)target=UnityEngine.EventSystems.ExecuteEvents.GetEventHandler<UnityEngine.EventSystems.IPointerClickHandler>(hits[0].gameObject);
        pointer.pointerPress=target;pointer.rawPointerPress=hits[0].gameObject;
        UnityEngine.EventSystems.ExecuteEvents.Execute(target,pointer,UnityEngine.EventSystems.ExecuteEvents.pointerUpHandler);
        UnityEngine.EventSystems.ExecuteEvents.Execute(target,pointer,UnityEngine.EventSystems.ExecuteEvents.pointerClickHandler);
        value=PathOf(target.transform);
      }
      else if(op=="toggle")((Toggle)Find("Toggle",(string)c["path"])).isOn=(bool)c["value"];
      else if(op=="slider")((Slider)Find("Slider",(string)c["path"])).value=(float)c["value"];
      else if(op=="method"){
        var obj=Find((string)c["type"],(string)c["path"]);
        var argTokens=(JArray)c["args"]??new JArray();
        var method=obj.GetType().GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance).Single(m=>m.Name==(string)c["method"] && m.GetParameters().Length==argTokens.Count && m.GetParameters().Select((p,i)=>CanConvert(argTokens[i],p.ParameterType)).All(v=>v));
        var args=method.GetParameters().Select((p,i)=>argTokens[i].ToObject(p.ParameterType)).ToArray();value=SafeValue(method.Invoke(obj,args));
      }
      else if(op=="expression"){
        var obj=Find("UniversalBlendshapes",(string)c["path"]);var f=obj.GetType().GetField((string)c["name"]);f.SetValue(obj,(float)c["value"]);
      }
      else if(op=="field"){
        var obj=Find((string)c["type"],(string)c["path"]);var name=(string)c["name"];
        var field=obj.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public);
        if(field!=null)field.SetValue(obj,c["value"].ToObject(field.FieldType));
        else {var property=obj.GetType().GetProperty(name);if(property==null)throw new Exception("No field or property "+name);property.SetValue(obj,c["value"].ToObject(property.PropertyType));}
      }
      else if(op=="chat")value=((LLMUnity.LLMCharacter)Find("LLMCharacter",(string)c["path"])).Chat((string)c["message"]);
      else if(op=="chatui"){
        var obj=Find("ChatBot",(string)c["path"]);var input=obj.GetType().GetField("inputBubble",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(obj);
        input.GetType().GetMethod("SetText").Invoke(input,new object[]{(string)c["message"]});
        obj.GetType().GetMethod("onInputFieldSubmit",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(obj,new object[]{(string)c["message"]});
      }
      else if(op=="settings"){
        var instanceType=typeof(SaveLoadHandler);var data=SaveLoadHandler.Instance.data;
        foreach(var p in (JObject)c["values"]){var field=data.GetType().GetField(p.Key);if(field==null)throw new Exception("Unknown setting "+p.Key);field.SetValue(data,p.Value.ToObject(field.FieldType));}
        SaveLoadHandler.Instance.SaveToDisk();SaveLoadHandler.ApplyAllSettingsToAllAvatars();
      }
      else if(op=="quit")Application.Quit();
      else throw new Exception("Unknown operation "+op);
    }catch(Exception e){failure=e;}
    if(value is System.Threading.Tasks.Task task){
      while(!task.IsCompleted)yield return null;
      if(task.IsFaulted)failure=task.Exception;
      else if(task.IsCanceled)failure=new Exception("Task cancelled");
      else value=SafeValue(task.GetType().GetProperty("Result")?.GetValue(task));
    }
    File.WriteAllText(System.IO.Path.Combine(Work,"result.json"),JsonConvert.SerializeObject(new{id,ok=failure==null,value,error=failure?.ToString()},Formatting.Indented));
  }
  static object SafeValue(object v){if(v==null)return null;if(v is Component c)return c.GetType().Name+":"+PathOf(c.transform);if(v is GameObject g)return "GameObject:"+PathOf(g.transform);if(v is UnityEngine.Object u)return u.name;if(v is string||v.GetType().IsPrimitive||v.GetType().IsEnum)return v; if(v is IEnumerable es){var a=new List<object>();foreach(var x in es){if(a.Count>=80)break;a.Add(SafeValue(x));}return a;}return v.ToString();}
  static bool CanConvert(JToken v,Type type){try{v.ToObject(type);return true;}catch{return false;}}
  object Ui(){return new{
    buttons=All<Button>().Select(b=>new{path=PathOf(b.transform),active=b.gameObject.activeInHierarchy,interactable=b.interactable}).ToArray(),
    toggles=All<Toggle>().Select(b=>new{path=PathOf(b.transform),active=b.gameObject.activeInHierarchy,value=b.isOn}).ToArray(),
    sliders=All<Slider>().Select(b=>new{path=PathOf(b.transform),active=b.gameObject.activeInHierarchy,value=b.value,min=b.minValue,max=b.maxValue}).ToArray(),
    texts=All<TMPro.TMP_Text>().Where(t=>t.gameObject.activeInHierarchy).Select(t=>new{path=PathOf(t.transform),text=t.text}).ToArray(),
    legacyTexts=All<Text>().Where(t=>t.gameObject.activeInHierarchy).Select(t=>new{path=PathOf(t.transform),text=t.text}).ToArray()};}
  object Snapshot(){return new{
    screen=new{Screen.width,Screen.height},
    cameras=All<Camera>().Select(c=>new{path=PathOf(c.transform),active=c.gameObject.activeInHierarchy,c.enabled,c.orthographic,c.orthographicSize,position=c.transform.position.ToString(),rotation=c.transform.rotation.ToString(),c.clearFlags,background=c.backgroundColor.ToString()}).ToArray(),
    avatars=All<Animator>().Where(a=>a.isHuman).Select(a=>new{path=PathOf(a.transform),active=a.gameObject.activeInHierarchy,a.enabled,a.isHuman,a.avatar.name,hips=a.GetBoneTransform(HumanBodyBones.Hips)?.position.ToString(),head=a.GetBoneTransform(HumanBodyBones.Head)?.position.ToString(),leftEye=a.GetBoneTransform(HumanBodyBones.LeftEye)?.name,rightEye=a.GetBoneTransform(HumanBodyBones.RightEye)?.name,state=a.GetCurrentAnimatorStateInfo(0).shortNameHash,parameters=a.parameters.Select(p=>new{name=p.name,type=p.type.ToString(),value=p.type==AnimatorControllerParameterType.Bool?(object)a.GetBool(p.name):p.type==AnimatorControllerParameterType.Int?(object)a.GetInteger(p.nameHash):p.type==AnimatorControllerParameterType.Float?(object)a.GetFloat(p.nameHash):null}).ToArray()}).ToArray(),
    meshes=All<SkinnedMeshRenderer>().Where(m=>m.gameObject.activeInHierarchy&&m.sharedMesh).Select(m=>new{path=PathOf(m.transform),m.enabled,vertices=m.sharedMesh.vertexCount,shapes=Enumerable.Range(0,m.sharedMesh.blendShapeCount).Select(i=>m.sharedMesh.GetBlendShapeName(i)).ToArray(),bounds=m.bounds.ToString(),materials=m.sharedMaterials.Where(x=>x).Select(x=>new{x.name,shader=x.shader?.name}).ToArray()}).ToArray(),
    lights=All<Light>().Select(l=>new{path=PathOf(l.transform),active=l.gameObject.activeInHierarchy,l.enabled,l.intensity,color=l.color.ToString(),l.type}).ToArray(),errors=errors.ToArray()};}
}
