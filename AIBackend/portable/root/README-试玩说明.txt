EmbodiedNPC Windows 试玩说明

首次试玩可先双击同目录的「使用说明.html」，查看带游戏截图的配置教程。

1. 解压到可写目录后，双击「启动游戏.cmd」（Start.cmd 也可启动）。请先完整解压，不要在压缩包内运行。
2. 首次进入主菜单，点击「配置 AI，进入咖啡馆」或打开「设置 → AI 配置」，填写供应商、API 地址、API Key 和模型名称。测试连接后，点击「保存并应用」；按游戏提示重启以生效。
3. 退出时使用游戏内「退出游戏」，等待启动窗口自动结束。它会正常关闭本次服务并保留 UserData。下次仍双击「启动游戏.cmd」。

Windows 试玩包已内置记忆查找模型（Embedding），普通玩家保持默认即可，无需反复设置或另行下载。模型资源随包不表示已经启用，运行状态以游戏内显示为准。
API 由你选择的服务商提供，获取模型、测试和对话可能产生费用。请妥善保管 API Key，不要分享 UserData 中的配置文件。
连接测试只验证简短回复；完整 NPC 对话的兼容性请在设置内查看对应测试和失败提示。

常用操作
WASD 移动，鼠标调整视角；E 互动；Esc 菜单/返回；Left Alt 切换鼠标捕获；G 打开玩家动作面板。
启用自由视角后，Tab 切换视角；处于自由视角时，H 切换提示，WASD 移动，Space 上升，Left Ctrl 下降，Left Shift 加速。滚轮在自由视角调整视野，在跟随视角调整距离。

目录与排查
Backend、UnityPlayer 和 Licenses 是运行资源与许可证，请保持原有相对位置。
UserData 首次启动时创建，保存模型配置、密钥、玩家设置、存档和日志；更新前请备份，不要随意删除。
启动入口自动申请空闲端口，不会接管或结束已有服务。调试时可使用 Start.cmd --port 18860 指定端口。
同一目录不能重复启动；不同解压目录的 UserData 和服务相互隔离。
Stop.cmd 只用于游戏已经退出但本包服务仍未正常关闭的情况，不会关闭正在运行的游戏或其他服务。
更多技术说明见 Backend/PORTABLE-START.txt；问题日志见 UserData/logs。

English quick start
Extract all files, then double-click 启动游戏.cmd or Start.cmd.
Open Settings → AI configuration, enter your provider, API URL, API key and model name, test the connection, then Save and apply. Restart when prompted.
Exit through the game's Quit button and wait for the launcher to finish. UserData is preserved.
The Windows package includes the default memory-search model. Keep the default unless you need advanced settings. API charges may apply; keep your API key private.
