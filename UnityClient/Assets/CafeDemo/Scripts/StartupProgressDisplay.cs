using System;
using System.Text;
using EmbodiedNPC.Settings;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CafeDemo
{
    public enum StartupStepState { Waiting, Running, Completed, Failed }
    public enum StartupUiFailure { None, Http, Configuration, Restart, Provider, WebSocket, Admission, Scene, Portable }

    // Scene-owned text/buttons only. Owners supply evidence; this view never checks readiness or connects.
    public sealed class StartupProgressDisplay : IDisposable
    {
        public GameObject Root { get; }
        public StartupStepState[] States { get; private set; }
        public StartupUiFailure Failure { get; private set; }
        public string ErrorCode { get; private set; }
        public string VisibleText => body.text;
        private readonly TMP_Text body;
        private readonly TMP_FontAsset font;
        private readonly Button retry, configure, details;
        private string[] chinese, english;
        private bool expanded;
        private bool compact;
        private readonly bool canRetry, canConfigure;
        public void SetCompact(bool value) { compact=value; Render(); }
        private string auxiliary="Embedding / 索引：状态未确认", auxiliaryEn="Embedding / index: unconfirmed";

        public StartupProgressDisplay(Transform parent,TMP_FontAsset source,Vector2 min,Vector2 max,Action recheck,Action openConfig)
        {
            canRetry=recheck!=null; canConfigure=openConfig!=null;
            Root=new GameObject("StartupProgress",typeof(RectTransform),typeof(Image));Root.transform.SetParent(parent,false);
            var r=(RectTransform)Root.transform;r.anchorMin=min;r.anchorMax=max;r.offsetMin=r.offsetMax=Vector2.zero;
            Root.GetComponent<Image>().color=new Color(.12f,.08f,.05f,.91f);
            font=source.sourceFontFile==null?source:TMP_FontAsset.CreateFontAsset(source.sourceFontFile);
            if(font!=source)font.isMultiAtlasTexturesEnabled=true; else font=null;
            body=new GameObject("StartupSteps",typeof(RectTransform),typeof(TextMeshProUGUI)).GetComponent<TMP_Text>();body.transform.SetParent(Root.transform,false);
            body.font=font??source;body.fontSize=24;body.color=new Color(.97f,.94f,.84f);body.alignment=TextAlignmentOptions.TopLeft;body.richText=true;body.raycastTarget=false;
            body.textWrappingMode=TextWrappingModes.Normal;body.overflowMode=TextOverflowModes.Ellipsis;
            r=body.rectTransform;r.anchorMin=Vector2.zero;r.anchorMax=Vector2.one;r.offsetMin=new Vector2(28,90);r.offsetMax=new Vector2(-28,-24);
            retry=Button("StartupRecheck",0,recheck);configure=Button("StartupConfigure",1,openConfig);details=Button("StartupDetails",2,()=>{expanded=!expanded;Render();});
            retry.gameObject.SetActive(recheck!=null);configure.gameObject.SetActive(openConfig!=null);
            SettingsUiLanguage.Changed+=Render;
        }
        private Button Button(string name,int index,Action action)
        {
            var go=new GameObject(name,typeof(RectTransform),typeof(Image),typeof(Button));go.transform.SetParent(Root.transform,false);
            var r=(RectTransform)go.transform;r.anchorMin=new Vector2(index/3f,0);r.anchorMax=new Vector2((index+1)/3f,0);r.offsetMin=new Vector2(14,18);r.offsetMax=new Vector2(-14,68);
            go.GetComponent<Image>().color=new Color(.36f,.29f,.20f,1);var button=go.GetComponent<Button>();button.targetGraphic=go.GetComponent<Image>();button.onClick.AddListener(()=>action?.Invoke());
            var text=new GameObject("Caption",typeof(RectTransform),typeof(TextMeshProUGUI)).GetComponent<TMP_Text>();text.transform.SetParent(go.transform,false);text.font=body.font;text.fontSize=21;text.color=body.color;text.alignment=TextAlignmentOptions.Center;text.raycastTarget=false;
            r=text.rectTransform;r.anchorMin=Vector2.zero;r.anchorMax=Vector2.one;r.offsetMin=r.offsetMax=Vector2.zero;
            return button;
        }
        public void Begin(string[] zh,string[] en)
        {chinese=zh;english=en;States=new StartupStepState[zh.Length];ClearFailure();expanded=false;ConfigAuxiliary(null);Step(0,StartupStepState.Running);}
        public void ClearFailure(){Failure=StartupUiFailure.None;ErrorCode=null;Render();}
        public void Step(int index,StartupStepState state)
        {if(States==null||index<0||index>=States.Length)return;States[index]=state;Render();}
        public void Fail(int index,StartupUiFailure reason,string code)
        {
            Failure=reason;ErrorCode=code;
            if(States!=null)for(int i=0;i<States.Length;i++)if(States[i]==StartupStepState.Running)States[i]=StartupStepState.Waiting;
            Step(index,StartupStepState.Failed);
        }
        public void ConfigAuxiliary(ModelConfigView config)
        {
            auxiliary="Embedding / 索引：状态未确认";auxiliaryEn="Embedding / index: unconfirmed";
            if(config?.embedding?.enabled==false){auxiliary="Embedding 选定配置：关闭；实际索引未确认";auxiliaryEn="Selected Embedding: disabled; index unconfirmed";}
            else if(config?.embedding?.provider_type=="builtin"&&config?.profiles?.builtin_resource_status=="missing"){auxiliary="内置资源文件缺失；加载与索引未确认";auxiliaryEn="Built-in files missing; loading/index unconfirmed";}
            Render();
        }
        private static string T(string zh,string en)=>SettingsUiLanguage.T(zh,en);
        public static (string Chinese,string English) FailureDescription(StartupUiFailure failure)=>failure switch
        {
            StartupUiFailure.Http=>("Backend HTTP 不可用。请先在外部启动服务，再重新检查。","Backend HTTP unavailable. Start it externally, then recheck."),
            StartupUiFailure.Configuration=>("主模型未配置。请打开 AI 配置完成设置。","Main model unconfigured. Open AI configuration."),
            StartupUiFailure.Restart=>("配置已保存，等待 Backend 重启应用。请外部重启后重新检查。","Saved settings need a Backend restart. Restart externally, then recheck."),
            StartupUiFailure.Provider=>("Provider 检查未通过。请检查模型、权限与连接，再重新检查。","Provider check failed. Check the model, access and connection, then recheck."),
            StartupUiFailure.WebSocket=>("WebSocket 连接失败。请检查服务与 WebSocket 地址后重新检查。","WebSocket connection failed. Check the service and WebSocket endpoint, then recheck."),
            StartupUiFailure.Admission=>("Cafe 启动准入未通过；玩家控制仍被保护。请打开 AI 配置检查。","Cafe admission failed; controls remain blocked. Check AI configuration."),
            StartupUiFailure.Scene=>("场景加载异常。仍停留当前界面，请检查场景与 Console 后重试。","Scene loading failed. Remain here and check the scene and Console before retrying."),
            StartupUiFailure.Portable=>("便携用户数据不可用。请通过正确的启动入口运行；修复文件或权限后重新启动。","Portable user data unavailable. Use the portable entry; fix files or permissions and restart."),
            _=>("","")
        };
        private string Recovery(){if(Failure==StartupUiFailure.Portable)return T(PortableDataPaths.ErrorDescription(ErrorCode),"Portable user data unavailable: "+ErrorCode+". Check the portable entry, files and permissions; restart after repair.");var text=FailureDescription(Failure);return T(text.Chinese,text.English);}
        private void Render()
        {
            if(States==null)return;var text=new StringBuilder();
            Root.GetComponent<Image>().color=compact&&!expanded?Color.clear:new Color(.12f,.08f,.05f,.91f);
            body.alignment=compact&&!expanded?TextAlignmentOptions.Center:TextAlignmentOptions.TopLeft;
            if(compact && !expanded) text.Append(Failure==StartupUiFailure.None?T("正在进入咖啡馆…", "Entering the café…"):T("暂时无法进入咖啡馆", "Unable to enter the café"));
            for(int i=0;(!compact || expanded) && i<States.Length;i++)
            {
                var state=States[i];string caption=state==StartupStepState.Completed?T("已完成","Done"):state==StartupStepState.Running?T("进行中","In progress"):state==StartupStepState.Failed?T("失败","Failed"):T("等待中","Waiting");
                string color=state==StartupStepState.Running?"F4CC75":state==StartupStepState.Completed?"AAA394":state==StartupStepState.Failed?"F0A58C":"D1C8B5";
                text.Append("<color=#").Append(color).Append('>');if(state==StartupStepState.Running)text.Append("<b>");
                text.Append(i+1).Append(". ").Append(T(chinese[i],english[i])).Append(" · ").Append(caption);
                if(state==StartupStepState.Running)text.Append("</b>");text.Append("</color>\n");
            }
            if(!compact || expanded)text.Append("\n<size=20>").Append(T(auxiliary,auxiliaryEn)).Append("</size>");
            if(Failure!=StartupUiFailure.None)text.Append("\n\n").Append(Recovery());
            if(expanded)text.Append("\n<size=18>").Append(T("详情：","Details: ")).Append(ErrorCode??T("尚无错误；仅显示已收到的阶段信号。","No error; stages reflect received signals only.")).Append("</size>");
            body.text=text.ToString();retry.interactable=Failure!=StartupUiFailure.None;configure.interactable=Failure!=StartupUiFailure.None&&Failure!=StartupUiFailure.Portable;
            retry.gameObject.SetActive(canRetry && (!compact || Failure!=StartupUiFailure.None));
            configure.gameObject.SetActive(canConfigure && (!compact || Failure!=StartupUiFailure.None));
            retry.GetComponentInChildren<TMP_Text>().text=T("重新检查","Recheck");configure.GetComponentInChildren<TMP_Text>().text=T("打开 AI 配置","AI configuration");details.GetComponentInChildren<TMP_Text>().text=T(expanded?"收起详情":"查看详情",expanded?"Hide details":"Details");
        }
        public static StartupUiFailure ConfigFailure(ModelConfigView config,string error)
        {
            if(error!=null&&error.StartsWith("PORTABLE_",StringComparison.Ordinal))return StartupUiFailure.Portable;
            if(error!=null||config==null)return StartupUiFailure.Http;
            if(!config.saved_configured)return StartupUiFailure.Configuration;
            if(config.profiles?.restart_required==true||config.profiles?.active_runtime==null)return StartupUiFailure.Restart;
            return StartupUiFailure.Provider;
        }
        public void Dispose()
        {
            SettingsUiLanguage.Changed-=Render;
            if(font==null)return;foreach(var texture in font.atlasTextures)if(texture!=null)UnityEngine.Object.Destroy(texture);
            if(font.material!=null)UnityEngine.Object.Destroy(font.material);UnityEngine.Object.Destroy(font);
        }
    }
}
