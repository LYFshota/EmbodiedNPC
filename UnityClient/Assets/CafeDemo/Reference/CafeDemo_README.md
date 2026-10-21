# CafeDemo 当前场景与素材

`Assets/Scenes/CafeDemo.unity` 是唯一产品、场景集成与验收基准。Unity 6000.5.10f1，URP；Build Settings 仅启用该场景。Player、Merchant、RegularCustomer、Cup 与 Cafe NavMesh 复用正式 Runtime。

## 空间与来源

咖啡馆包含入口、主通道、吧台前后工作区、桌椅及沙发区。空间参考来自项目 `SceneResources/ReferencePictures`；Navigation Points 是实际寻路/测试消费者，不是可随意删除的装饰。

导入的 Kenney Furniture Kit 与 Food Kit 为 CC0，素材来源与 SHA256 保留在 [ImportManifest.csv](ImportManifest.csv)。许可原文为本目录 `kenney_furniture-kit-License.txt` 与 `kenney_food-kit-License.txt`。人物模型来源、wrapper 与动画见 [CafeDemo_Characters](CafeDemo_Characters.md)。许可、字体、第三方资源与现有 Cafe visuals 保留。

运行与控制见 [CafeDemo_Playable](CafeDemo_Playable.md)，系统边界见 [CURRENT_ARCHITECTURE](../../../../Docs/ARCHITECTURE.md)。Editor authoring 工具只用于当前 Cafe，不能覆盖用户手工修改。
