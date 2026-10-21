using EmbodiedNPC.Settings;

namespace CafeDemo
{
    public static class SettingsOperationHelp
    {
        public static string Text => SettingsUiLanguage.T(
            "WASD：移动　　鼠标：控制视角\nE：与 NPC 交互　　G：表情面板\n鼠标滚轮：拉近 / 拉远视角\nESC：关闭当前界面或取消操作\nAlt + Enter：切换全屏 / 窗口模式",
            "WASD: Move     Mouse: Look around\nE: Interact with NPC     G: Emote panel\nMouse Wheel: Zoom In / Out\nESC: Close the current interface or cancel\nAlt + Enter: Toggle Fullscreen / Windowed");
    }
}
