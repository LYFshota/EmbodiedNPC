"""EmbodiedNPC 的默认后端入口。

M3 起，使用带 Provider、结构化校验与安全回退的正式服务。
启动方式保持不变：``uvicorn app.main:app``。
"""

from .m3_main import app
