using System;
using UnityEngine;

namespace EmbodiedNPC.Settings
{
    // Four window-size preferences; no rendering quality or Backend ownership.
    public static class DisplaySettings
    {
        public static readonly Vector2Int[] Presets = { new(3840,2160), new(2560,1440), new(1920,1080), new(1280,720) };
        public static readonly string[] Values = { "3840x2160", "2560x1440", "1920x1080", "1280x720" };
        private static int editorSelection=3;
        private static bool editorFreeCamera;
        public static bool FreeCameraEnabled => Application.isEditor ? editorFreeCamera : PortablePreferences.Current.FreeCameraEnabled;
        public static bool SetFreeCameraEnabled(bool enabled)
        {
            if (Application.isEditor) { editorFreeCamera = enabled; return true; }
            return PortablePreferences.Current.SetFreeCameraEnabled(enabled);
        }
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetActiveWindow();
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
#endif
        private static float WindowDpi(float fallback)
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            try { uint dpi=GetDpiForWindow(GetActiveWindow());if(dpi>0)return dpi; }
            catch(EntryPointNotFoundException) { }
#endif
            return fallback;
        }
        public static bool IsPreset(string value) => Array.IndexOf(Values,value)>=0;
        public static int DefaultIndex(int width,int height)
        {
            for(int i=0;i<Presets.Length;i++)
                if(Presets[i].x<=width && Presets[i].y<=height && (Presets[i].x<width || Presets[i].y<height))return i;
            return Presets.Length-1;
        }
        public static Vector2Int FitWindow(Vector2Int requested,Vector2Int workArea,float dpi)
        {
            float scale=Mathf.Max(1,dpi>0?dpi/96:1);
            int width=Mathf.Max(1,workArea.x-Mathf.CeilToInt(32*scale));
            int height=Mathf.Max(1,workArea.y-Mathf.CeilToInt(80*scale));
            float ratio=Mathf.Min(1,Mathf.Min((float)width/requested.x,(float)height/requested.y));
            return new Vector2Int(Mathf.Max(1,Mathf.FloorToInt(requested.x*ratio)),Mathf.Max(1,Mathf.FloorToInt(requested.y*ratio)));
        }
        public static int SelectedIndex => Application.isEditor ? editorSelection : Mathf.Max(0,Array.IndexOf(Values,PortablePreferences.Current.DisplayResolution));

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Initialize()
        {
            if(Application.isEditor || PortableDataPaths.StartupError!=null)return;
            var display=Screen.mainWindowDisplayInfo;
            var preferences=PortablePreferences.Current;
            int index=Array.IndexOf(Values,preferences.DisplayResolution);
            if(index<0)
            {
                index=DefaultIndex(display.width,display.height);
                if(!preferences.SetDisplayResolution(Values[index]))return;
            }
            ApplyResolution(index);
        }
        public static string Select(int index)
        {
            if(index<0 || index>=Values.Length)return SettingsUiLanguage.T("无效的分辨率。","Invalid resolution.");
            if(Application.isEditor)
            { editorSelection=index;return SettingsUiLanguage.T("Editor 预览选择；Game View 保持不变。","Editor preview selection; Game View is unchanged."); }
            if(!PortablePreferences.Current.SetDisplayResolution(Values[index]))
                return SettingsUiLanguage.T("无法保存画面设置：","Cannot save display settings: ")+PortablePreferences.Current.Error;
            return ApplyResolution(index);
        }
        private static string ApplyResolution(int index)
        {
            var display=Screen.mainWindowDisplayInfo;
            var requested=Presets[index]; var size=requested;
            var mode=Screen.fullScreenMode;
            if(mode==FullScreenMode.Windowed)
            {
                size=FitWindow(requested,display.workArea.size,WindowDpi(display.physicalDpi));
                Screen.SetResolution(size.x,size.y,mode);
                // Keep the entire frame in the current monitor's usable area, including DPI-scaled chrome.
                Screen.MoveMainWindowTo(display,new Vector2Int(display.workArea.x+Mathf.Max(0,(display.workArea.width-size.x)/2),display.workArea.y+Mathf.Max(0,(display.workArea.height-size.y)/2)));
            }
            else Screen.SetResolution(size.x,size.y,mode);
            return SettingsUiLanguage.T("已应用：","Applied: ")+size.x+" × "+size.y+(size!=requested?SettingsUiLanguage.T("（已适配桌面可用空间）"," (fitted to the desktop work area)"):"");
        }
    }
}
