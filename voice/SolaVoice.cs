using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Networking;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using TMPro;
using Newtonsoft.Json.Linq;

// Thin Unity UI and avatar adapter. Recognition and audio stay outside the render loop.
public class SolaVoice:MonoBehaviour {
 public static SolaVoice Instance; public static bool MenuOpen(){return Instance&&Instance.panel&&Instance.panel.gameObject.activeSelf;}
 const string Base="http://127.0.0.1:18768";
 string token="",lastChat="",pendingReply="",sentReply="",lastError="",serverInstance="";int cursor,generation,chatId;bool pendingDone,polling,posting,connected;
 JObject state=new JObject();TMP_FontAsset font;RectTransform uiRoot,panel,bubble,content;TMP_Text status,transcript,microphone,subtitle,listenLabel,codexLabel;TMP_InputField input,workspace;
 ScrollRect scroll;LLMUnity.LLMCharacter character;UniversalBlendshapes expressions;float nextPoll,nextLookup,nextReply,mouth;
 readonly List<string> history=new List<string>();string lastTurn="";
 class LocalRequest {public volatile bool done;public string text,error;}
 LocalRequest Request(string path,string body=null){
  var result=new LocalRequest();ThreadPool.QueueUserWorkItem(_=>{try{
   using(var client=new TcpClient("127.0.0.1",18768))using(var stream=client.GetStream()){
    stream.ReadTimeout=5000;stream.WriteTimeout=5000;var bytes=Encoding.UTF8.GetBytes(body??"");
    string header=(body==null?"GET ":"POST ")+path+" HTTP/1.1\r\nHost: 127.0.0.1:18768\r\nx-sola-token: "+token+"\r\nConnection: close\r\nContent-Type: application/json\r\nContent-Length: "+bytes.Length+"\r\n\r\n";
    var head=Encoding.ASCII.GetBytes(header);stream.Write(head,0,head.Length);if(bytes.Length>0)stream.Write(bytes,0,bytes.Length);
    using(var reader=new StreamReader(stream)){string response=reader.ReadToEnd();int cut=response.IndexOf("\r\n\r\n");if(!response.StartsWith("HTTP/1.1 200")||cut<0)throw new Exception("本地语音接口未成功响应");result.text=response.Substring(cut+4);}
   }
  }catch(Exception e){result.error=e.Message;}finally{result.done=true;}});return result;
 }
 public static void Attach(){if(Instance)return;Instance=new GameObject("Sola Voice").AddComponent<SolaVoice>();DontDestroyOnLoad(Instance.gameObject);}
 IEnumerator Start(){
  try{font=TMP_FontAsset.CreateFontAsset("Microsoft YaHei","Regular",40);}catch(Exception ex){Debug.LogWarning("Sola CJK font: "+ex.Message);}
  for(int i=0;!font&&i<100;i++){font=Resources.FindObjectsOfTypeAll<TMP_Text>().Where(t=>t&&t.gameObject.scene.IsValid()&&t.font&&t.transform.root.name=="Settings").Select(t=>t.font).FirstOrDefault(f=>f.name!="LiberationSans SDF");if(font)break;yield return new WaitForSeconds(.2f);}
  if(!font)font=Resources.FindObjectsOfTypeAll<TMP_FontAsset>().FirstOrDefault(f=>f&&f.HasCharacter('你'));
  if(!font){Debug.LogError("Sola voice: UI font unavailable");yield break;}
  BuildUI();Debug.Log("Sola voice adapter ready");
 }
 void Update(){
  if(!panel)return;
  var cam=Camera.main;if(cam&&uiRoot){uiRoot.sizeDelta=new Vector2(Screen.width,Screen.height);uiRoot.pivot=new Vector2(.5f,.5f);uiRoot.position=cam.ScreenToWorldPoint(new Vector3(Screen.width*.5f,Screen.height*.5f,cam.nearClipPlane+.08f));uiRoot.rotation=cam.transform.rotation;uiRoot.localScale=Vector3.one*(cam.orthographicSize*2f/Screen.height);}
  if(Input.GetKeyDown(KeyCode.Escape)&&MenuOpen())panel.gameObject.SetActive(false);
  if(Time.unscaledTime>=nextPoll&&!polling){nextPoll=Time.unscaledTime+.2f;StartCoroutine(Poll());}
  if(Time.unscaledTime>=nextLookup){nextLookup=Time.unscaledTime+1;
   expressions=Resources.FindObjectsOfTypeAll<UniversalBlendshapes>().FirstOrDefault(x=>x&&x.gameObject.activeInHierarchy);
   if(!character)character=Resources.FindObjectsOfTypeAll<LLMUnity.LLMCharacter>().FirstOrDefault(x=>x&&x.gameObject.scene.IsValid());
  }
  if(chatId==generation&&!posting&&(pendingReply!=sentReply||pendingDone)&&Time.unscaledTime>=nextReply){nextReply=Time.unscaledTime+.12f;StartCoroutine(SendReply());}
  float target=(float?)state["mouth"]??0;mouth=Mathf.Lerp(mouth,target,1-Mathf.Exp(-Time.unscaledDeltaTime*(target>mouth?24:15)));
  if(expressions&&((bool?)state["speaking"]==true||mouth>.001f)){expressions.A=mouth*.8f;expressions.O=mouth*.25f;}
 }
 IEnumerator Poll(){
  polling=true;
  if(token==""){try{token=File.ReadAllText(@"D:\MateEngine-Sola\userdata\voice\token.txt").Trim();}catch{polling=false;status.text="语音后台启动中…";yield break;}}
  {
   var req=Request("/state?client=unity&after="+cursor);while(!req.done)yield return null;
   if(req.error==null){
    try{
     state=JObject.Parse(req.text);connected=true;int g=(int)state["generation"];
     if(serverInstance!=(string)state["instance"]){serverInstance=(string)state["instance"];cursor=0;}
     if(g!=generation){generation=g;if(character)character.CancelRequests();pendingReply=sentReply="";pendingDone=false;}
     foreach(var e in (JArray)state["events"]){cursor=Math.Max(cursor,(int)e["seq"]);if((string)e["type"]=="chat"&&(int)e["generation"]==generation)BeginChat((int)e["id"],(string)e["text"]);}
     RefreshUI();
    }catch(Exception ex){Debug.LogWarning("Sola voice response: "+ex.Message);}
   }else{connected=false;lastError=req.error;state["mouth"]=0;state["speaking"]=false;status.text="语音后台未连接 · "+lastError;}
  }
  polling=false;
 }
 void BeginChat(int id,string text){
  chatId=id;pendingReply=sentReply="";pendingDone=false;
  if(!character){pendingReply="本地对话模型还没有就绪，请稍等片刻再试。";pendingDone=true;return;}
  // The stock chat menu disables these objects. Keep the model available while its menu is closed.
  if(character.llm&&!character.llm.gameObject.activeInHierarchy){character.llm.transform.SetParent(transform,false);character.llm.gameObject.SetActive(true);}
  if(!character.gameObject.activeInHierarchy){character.transform.SetParent(transform,false);character.gameObject.SetActive(true);}
  StartCoroutine(ChatRoutine(id,text));
 }
 IEnumerator ChatRoutine(int id,string text){
  System.Threading.Tasks.Task<string> task=null;
  try{string prompt="请使用简洁自然的中文口语回答，通常一到三句话，不输出Markdown。你是桌宠Sola，也叫小歪。只有明确收到的任务状态才是真实信息，不要编造Codex工作进度。用户说："+text;task=character.Chat(prompt,t=>{if(id==generation)pendingReply=t;});}
  catch(Exception ex){Debug.LogWarning("Sola voice LLM: "+ex.Message);}
  float deadline=Time.realtimeSinceStartup+90; if(task!=null)while(!task.IsCompleted&&id==generation&&Time.realtimeSinceStartup<deadline)yield return null;
  if(task!=null&&!task.IsCompleted){character.CancelRequests();if(id==generation){pendingReply="对话模型准备时间较长，请稍后再试。";pendingDone=true;}yield break;}
  if(id==generation){pendingReply=task!=null&&!task.IsFaulted&&!task.IsCanceled?task.Result:"本地对话暂时遇到问题，请稍后再试。";pendingDone=true;}
 }
 IEnumerator SendReply(){
  posting=true;int id=chatId;string value=pendingReply;bool done=pendingDone;pendingDone=false;sentReply=value;
  yield return Post("/reply",new JObject{{"id",id},{"text",value},{"done",done}});
  posting=false;
 }
 public void Control(string action){StartCoroutine(Post("/control",new JObject{{"action",action}}));}
 public void CycleMicrophone(){var devices=state["devices"] as JArray;if(devices==null||devices.Count==0){microphone.text="请先开启麦克风以读取可用设备";return;}string selected=(string)state["audio_device"];if(String.IsNullOrEmpty(selected))selected="default";int index=-1;for(int i=0;i<devices.Count;i++)if((string)devices[i]["id"]==selected)index=i;StartCoroutine(Post("/control",new JObject{{"action","audio_device"},{"device",(string)devices[(index+1)%devices.Count]["id"]}}));}
 public void SubmitText(string text){if(String.IsNullOrWhiteSpace(text))return;StartCoroutine(Post("/control",new JObject{{"action","text"},{"text",text.Trim()}}));if(input)input.text="";}
 IEnumerator Post(string path,JObject data){
  var r=Request(path,data.ToString());while(!r.done)yield return null;if(r.error!=null)Debug.LogWarning("Sola voice request failed: "+path+" "+r.error);
 }
 string Phase(string p){switch(p){case "idle":return "麦克风已关闭";case "listening":return "正在听你说话";case "hearing":return "听到了，继续说…";case "recognizing":return "正在识别";case "thinking":return "正在思考";case "synthesizing":return "准备开口…";case "speaking":return "正在说话 · 可以打断";case "error":return "需要检查语音服务";default:return p;}}
 void RefreshUI(){
  string phase=(string)state["phase"];status.text=Phase(phase);bool listening=(bool?)state["listening"]??false;listenLabel.text=listening?"关闭麦克风":"开启麦克风";
  string mic=(string)state["microphone"];microphone.text=String.IsNullOrEmpty(mic)?"Ctrl + Alt + V 开关麦克风\n开启后可连续对话":(mic.Length>42?mic.Substring(0,40)+"…":mic)+"\n"+((bool?)state["aec"]==true?"回声消除已启用 · ":"")+(listening?"麦克风开启":"麦克风关闭");
  string user=(string)state["input"]??"",reply=(string)state["reply"]??"";
  if(user!=lastChat&&lastTurn!=""){history.Add(lastTurn);if(history.Count>12)history.RemoveAt(0);}lastChat=user;lastTurn=user==""?"":"你："+user+"\n\nSola："+reply;
  string body=String.Join("\n\n────────\n\n",history.Concat(lastTurn==""?new string[0]:new[]{lastTurn}));
  if(body=="")body="和我聊聊吧。\n\n你可以说：\n“Codex 现在做到哪了？”\n“让 Codex 帮我检查这个项目。”\n\n点击开启麦克风，或直接打字。";
  string err=(string)state["error"];if(!String.IsNullOrEmpty(err))body+="\n\n提示："+err;
  if(transcript.text!=body){transcript.text=body;transcript.ForceMeshUpdate();content.sizeDelta=new Vector2(0,Mathf.Max(240,transcript.preferredHeight+20));scroll.verticalNormalizedPosition=0;}
  var tasks=(JArray)state["tasks"];int active=tasks.Count(t=>!(bool)t["stale"]&&((string)t["phase"]=="working"||(string)t["phase"]=="waiting"));
  codexLabel.text="Codex："+(active>0?active+" 个活跃任务":"暂无活跃任务")+"    "+((string)state["codex"]?["detail"]??"");
  if(workspace&&!workspace.isFocused&&workspace.text=="")workspace.text=(string)state["workspace"];
  bubble.gameObject.SetActive(!MenuOpen()&&((bool?)state["speaking"]==true||phase=="hearing"||phase=="recognizing"||phase=="thinking"));
  subtitle.text=reply.Length>100?reply.Substring(reply.Length-100):reply==""?status.text:reply;
 }
 RectTransform Box(string name,Transform parent,float x,float y,float w,float h,Color? color=null){var g=new GameObject(name,typeof(RectTransform));g.layer=5;var r=g.GetComponent<RectTransform>();r.SetParent(parent,false);r.anchorMin=r.anchorMax=r.pivot=new Vector2(0,1);r.anchoredPosition=new Vector2(x,-y);r.sizeDelta=new Vector2(w,h);if(color.HasValue)g.AddComponent<Image>().color=color.Value;return r;}
 TMP_Text Text(string name,Transform parent,string value,float x,float y,float w,float h,int size){var r=Box(name,parent,x,y,w,h);var t=r.gameObject.AddComponent<TextMeshProUGUI>();t.font=font;t.fontSize=size;t.text=value;t.color=new Color(.92f,.94f,1);t.richText=false;t.raycastTarget=false;t.enableWordWrapping=true;return t;}
 Button Button(string name,Transform parent,string value,float x,float y,float w,float h,UnityEngine.Events.UnityAction action){var r=Box(name,parent,x,y,w,h,new Color(.23f,.29f,.44f,.98f));var b=r.gameObject.AddComponent<Button>();b.targetGraphic=r.GetComponent<Image>();b.onClick.AddListener(action);var t=Text(name+"Label",r,value,8,6,w-16,h-12,19);t.alignment=TextAlignmentOptions.Center;return b;}
 TMP_InputField InputBox(string name,Transform parent,string hint,float x,float y,float w,float h){var r=Box(name,parent,x,y,w,h,new Color(.08f,.11f,.18f,1));var text=Text("Text",r,"",10,8,w-20,h-16,18);var placeholder=Text("Hint",r,hint,10,8,w-20,h-16,18);placeholder.color=new Color(.6f,.64f,.72f);var f=r.gameObject.AddComponent<TMP_InputField>();f.textComponent=(TextMeshProUGUI)text;f.textViewport=text.rectTransform;f.placeholder=placeholder;f.lineType=TMP_InputField.LineType.SingleLine;f.characterLimit=4000;return f;}
 void BuildUI(){
  var go=new GameObject("Sola Voice Canvas",typeof(RectTransform),typeof(Canvas),typeof(SolaUIRaycaster));DontDestroyOnLoad(go);uiRoot=go.GetComponent<RectTransform>();var canvas=go.GetComponent<Canvas>();canvas.sortingOrder=32500;
  var launch=Button("VoiceOpen",go.transform,"语音 / 对话",0,0,136,40,()=>panel.gameObject.SetActive(!panel.gameObject.activeSelf));var lr=launch.GetComponent<RectTransform>();lr.anchorMin=lr.anchorMax=lr.pivot=new Vector2(.5f,0);lr.anchoredPosition=new Vector2(150,16);
  panel=Box("VoicePanel",go.transform,0,0,590,570,new Color(.055f,.073f,.12f,1));panel.anchorMin=panel.anchorMax=panel.pivot=new Vector2(.5f,.5f);panel.anchoredPosition=Vector2.zero;
  Text("Title",panel,"Sola · 语音与 Codex",22,18,475,36,27);Button("Close",panel,"×",540,14,34,34,()=>panel.gameObject.SetActive(false));
  status=Text("VoiceStatus",panel,"语音后台连接中…",24,65,540,30,22);microphone=Text("Microphone",panel,"",24,100,432,38,15);Button("Device",panel,"切换设备",466,101,102,32,CycleMicrophone);
  var viewport=Box("Conversation",panel,22,144,546,245,new Color(.08f,.10f,.16f,1));viewport.gameObject.AddComponent<RectMask2D>();scroll=viewport.gameObject.AddComponent<ScrollRect>();scroll.horizontal=false;scroll.viewport=viewport;scroll.movementType=ScrollRect.MovementType.Clamped;
  content=Box("Content",viewport,0,0,546,245);content.anchorMin=new Vector2(0,1);content.anchorMax=new Vector2(1,1);content.pivot=new Vector2(.5f,1);content.anchoredPosition=Vector2.zero;content.sizeDelta=new Vector2(0,245);scroll.content=content;
  transcript=Text("Transcript",content,"",12,10,520,230,20);transcript.rectTransform.anchorMin=Vector2.zero;transcript.rectTransform.anchorMax=Vector2.one;transcript.rectTransform.offsetMin=new Vector2(12,10);transcript.rectTransform.offsetMax=new Vector2(-12,-10);
  input=InputBox("Message",panel,"输入一句话，Enter 发送",22,401,434,44);input.onSubmit.AddListener(SubmitText);Button("Send",panel,"发送",466,401,102,44,()=>SubmitText(input.text));
  var listen=Button("Listen",panel,"开启麦克风",22,455,168,42,()=>Control("toggle"));listenLabel=listen.GetComponentInChildren<TMP_Text>();Button("Interrupt",panel,"打断说话",201,455,130,42,()=>Control("interrupt"));Button("CodexStatus",panel,"询问任务进度",342,455,226,42,()=>SubmitText("Codex任务现在进度怎么样？"));
  codexLabel=Text("CodexState",panel,"",22,503,546,23,14);workspace=InputBox("Workspace",panel,"Codex 工作目录",22,529,434,30);Button("SaveWorkspace",panel,"设置目录",466,529,102,30,()=>StartCoroutine(Post("/control",new JObject{{"action","workspace"},{"path",workspace.text}})));
  panel.gameObject.SetActive(false);
  bubble=Box("VoiceSubtitle",go.transform,0,0,460,108,new Color(.055f,.075f,.12f,.95f));bubble.anchorMin=bubble.anchorMax=bubble.pivot=new Vector2(.5f,0);bubble.anchoredPosition=new Vector2(0,72);subtitle=Text("Subtitle",bubble,"",16,12,428,84,20);bubble.gameObject.SetActive(false);
 }
 public object Status(){return new{connected,lastError,generation,cursor,menu=MenuOpen(),phase=(string)state["phase"],mouth,font=font?font.name:"",hasExpressions=expressions!=null,hasCharacter=character!=null};}
}
