using EmbodiedNPC.Settings;

namespace CafeDemo
{
    public static class SettingsOperationHelp
    {
        public static string Text => SettingsUiLanguage.T(
            "WASD：移动　鼠标：调整视角\nE：互动　G：玩家动作面板\nLeft Alt：切换鼠标捕获；切回游戏后可右键重新捕获\n滚轮：跟随视角拉近 / 拉远\nEsc：菜单 / 返回 / 取消　Alt + Enter：全屏 / 窗口\n启用自由视角后：Tab 切换视角\n处于自由视角时：H 切换提示；WASD 移动，Space 上升，Left Ctrl 下降，Left Shift 加速，滚轮调整视野\n设置或聊天期间，相应场景快捷键暂停。",
            "WASD: Move · Mouse: Look\nE: Interact · G: Player actions\nLeft Alt: Toggle mouse capture; right-click to recapture after returning to the game\nWheel: Zoom in / out in follow view\nEsc: Menu / Back / Cancel · Alt + Enter: Fullscreen / Window\nWith free camera enabled: Tab switches view\nWhile in free camera: H toggles hints; WASD moves; Space rises; Left Ctrl descends; Left Shift speeds up; wheel changes field of view\nRelevant world shortcuts pause during settings or chat.");
    }
}
