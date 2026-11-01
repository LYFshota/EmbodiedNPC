# Unity 源码首次导入修复验证（2026-11-01）

## 基线与隔离

- 公开仓库 `LYFshota/EmbodiedNPC` main：`49c6fdeed26fdf07425befd628b9e488bafb697f`，对应 v0.1.4-preview 源码。
- Unity `6000.5.10f1 (3bd4f66ad299)`，Windows / DX12 / RTX 4060 Laptop；UniVRM 保持 `9750dadc592f421c356bf0376c73e61abbe323e3`，manifest/lock 不变。
- 从已与公开远端核实一致的公开 Git 对象建立独立 `baseline` 与 `fix` worktree。两者启动前均没有 Library；未复制任何原工程 Library 或模型导入产物，关闭 Accelerator 下载/上传。
- 初始对照使用独立 UPM 缓存；修复复验共享本轮下载的原始 UPM 包缓存，Git 包仍按 lock 恢复。该缓存不含模型导入产物。
- 本地主工作区的实际 9 个未提交文件单独记录 SHA-256；公开原工作区不参与 Unity 测试。

本地原始证据保留在 `D:\EmbodiedNPC\BuildValidation\SourceColdImport-20261101\`，不随源码提交。

## 实际复现与原因边界

初次恢复曾出现 Git TLS EOF / Registry ECONNRESET。保留失败日志后，通过本机验收进程的代理和 HTTP/1.1 环境完成固定包恢复，没有更改系统网络设置或包版本。

`baseline-cold-ready-network.log` 中六项模型全部在 `UniGLTF.MaterialFactory.LoadAsync`（MaterialFactory.cs:118）收到 null Shader，产生 `ArgumentNullException: Shader` / `Asset import failed`。包内 Shader 的导入记录在模型之前，因此仅看文件导入先后不足以证明 Shader 查找已就绪；固定导入器通过 `Shader.Find` 取材质 Shader，没有处理本轮刷新中的 null 结果。

这次 Unity 最终退出码仍为 **0**。随后只读检查 `baseline-models-and-scenes.json` 显示：六项根对象全部缺失，Avatar、网格、材质子资产均为 0；CafeDemoV2 有 114 项扫描问题。失败产物不会仅因正常重新打开而自动补齐。

已确认失败点、失败产物和恢复条件；未声称定位 Unity 内部 Shader 名称登记的更底层原因。保持同一源码、模型和包版本，在空闲 Editor 显式加载包内 Shader 后定向重导入即可完整恢复，未发现需要升级依赖或更换模型的证据。另一个临时小工程证明，单纯在预处理回调声明依赖没有得到所需的 ScriptedImporter 依赖结果，未将该实验方案提交。

## 最小改动

- `PublicSourceModels.cs`：从原 `PublicPreviewBuild.ValidateModels()` 拆出六项固定模型的只读验证，并增加 Animator Avatar 绑定、网格、材质、Shader 检查。
- `PublicSourceModelImport.cs`：仅在主 Editor 连续空闲更新后运行；检查编译、AssetDatabase、Shader 编译、包注册与固定 Shader 的实际加载状态。只重导入检测为不完整的资源，不调用 Refresh、SaveAssets 或发布构建。
- 修复前先把“模型内容 + 原 .meta + Shader 依赖 + Unity 版本”的尝试标识写入 Library。相同状态最多自动修复一次；失败明确报告路径并停止，重载/重开不会无限导入。健康模型只验证。
- `PublicPreviewBuild.ValidateModels()` 复用只读验证；独立批处理入口为 `PublicSourceModelImport.ValidateBatch()`，异常使 executeMethod 失败。
- 增加三个定向 EditMode 检查，更新 SOURCE_SETUP 与中英文 README。

## 六项模型结果

| 资源 | 根对象 | 有效 Humanoid Avatar | Mesh | Material | Texture |
| --- | --- | --- | ---: | ---: | ---: |
| 薇薇.vrm | PASS | 1，已绑定 | 3 | 15 | 28 |
| Ao.vrm | PASS | 1，已绑定 | 3 | 27 | 55 |
| 桜田 史利矢.vrm | PASS | 1，已绑定 | 3 | 17 | 28 |
| GirlA.vrm | PASS | 1，已绑定 | 2 | 4 | 4 |
| CozyCafe.glb | PASS | 不适用 | 19 | 9 | 6 |
| Xingmang.vrm | PASS | 1，已绑定 | 3 | 26 | 52 |

五个 VRM 使用 `VRM10/Universal Render Pipeline/MToon10`，GLB 使用 `Universal Render Pipeline/Lit`；被引用材质 Shader 支持当前平台且无编译错误。模型子资产扫描问题为 0。

## 冷启动、重开和运行

- **修复后全新 Library：PASS。** 普通 Editor 启动，没有 executeMethod、手动 Reimport 或修复菜单；`fix-cold-editor.log` 记录六项自动定向修复及 `PUBLIC_SOURCE_MODEL_IMPORT_READY: repaired=6`。
- **二次打开：PASS。** `second-open-editor.log` 记录 `repaired=0`。日志中 QuickSearch 的 ASIAI0B 索引不能计作 UniVRM 模型重新导入；未出现新的 VRM10/UniGLTF 模型修复。
- **场景：PASS。** `fix-models-and-scenes.json` 扫描 TitleScene 474 个对象、CafeDemoV2 2245 个对象；Missing Prefab、Missing Script、丢失序列化引用、实际模型 Mesh/Material/Shader 问题均为 0。TMP 按需生成的文字网格不当作丢失模型网格。
- **定向测试：3/3 PASS。** `targeted-tests.xml`：六模型完整性、健康模型重复验证不增加 GlobalArtifactProcessedVersion、缺失导入诊断。
- **有限修复：PASS。** 在对照工程的内存导入产物中临时清除一项 Avatar 绑定，只修复该项；再次注入同一问题时抛出单次修复诊断，没有再导入。恢复内存绑定后验证通过，未保存原始资源。证据：`bounded-recovery.json`。
- **实际 Editor Play：PASS。** 无 Backend 时 TitleScene 正常运行并显示“暂时无法连接 AI 服务”；使用仓库现有 loopback HTTP/WS 测试服务及新建的隔离会话后，通过原 EnterCafe 按钮进入 CafeDemoV2，四个 NPC 与玩家均有有效 Avatar 和启用的模型 Renderer，已检查实际画面。证据：`play-evidence/offline-title.*`、`fixture-title.*`、`fixture-cafe.*`。
- 最小服务只返回确定性的配置/连接与经验回执，不导入生产 Backend、不访问数据库或调用模型。一次追加截图夹具因放在 Unity Temp 下而被退出清理，已保留失败记录，并移到外部目录重跑；不是模型回归。

## 保护与结论

本次不提交场景、Prefab、Animator、人设资产、原始模型、已有 .meta、Packages 或项目设置改动。Unity 自行产生的字体换行/URP 运行时设置序列化等差异在取证后恢复；临时探针和测试会话不纳入提交。主工作区 9 项 WIP 保持不变。

受测工具链下不再需要任何手动模型导入、重新绑定或场景生成操作，建议将这项限定的 Editor 修复合入公开 main。没有升级或覆盖 Release。本轮没有验证全新物理机器、其他驱动/平台、真实 LLM/Embedding、完整回归或新 Player 构建；不能将有限自动修复表述为所有机器均开箱即用。首次失败记录会保留，最终是否成功仍以模型/场景验证和稳定运行画面为准。
