# Xingmang 资产清单与许可元数据

## 资产与所有者确认

| 项目 | 当前记录 |
|---|---|
| 资源 | `UnityClient/Assets/VRMCompatibilityTest/Xingmang.vrm` |
| 格式 / 大小 | VRM 0（GLB 2 容器）；23,871,256 字节 |
| 当前 SHA-256 | `d62dbf4301d3ac62b5cae3772f1dc81aad770a91b36e2c0dabed7dba38adbb7c` |
| 元数据修正前 SHA-256 | `62f693a02af30d5780b952730dd901886e640dd50bf603baf68fe35b17e67e57` |
| Unity Meta SHA-256 | `0cdce0f99e49bf35a1a6856e12a127930b63b4298395a88cc905e5ba82cfd3ef` |
| 原作者 / 建模署名 | 辉歪歪；保留内嵌作者字段 |
| 授权方 | LYFshota |
| 本人权利范围确认 | **OWNER CONFIRMED**：所有者已确认委托买断及相关著作财产权归属。无需再次提交完整合同或公开私人交易资料。 |
| 适用范围 | 模型及其中内嵌的身体、头发、服装、贴图等，在所有者实际拥有或控制的可授权权利范围内，一并适用独立许可。 |
| 实际独立许可例外 | 当前复用资料中未识别出可列名的独立第三方部件；如发现具体例外，在此记录部件、来源及原许可，不以本项目条款覆盖。 |
| 独立许可 | [Xingmang.LICENSE.md](../UnityClient/Assets/VRMCompatibilityTest/Xingmang.LICENSE.md)；已由 LYFshota 于 2026-10-21 正式采用。 |

哈希只用于版本识别和变更核对，不是许可证正文的固定条件；合法修改版不会因哈希改变就自动失去许可。若模型随后修正元数据，应记录新哈希与实际修改，旧哈希保留为来源版本。

## 当前元数据与本轮修正

| 字段 | 当前值 | 本轮处理 |
|---|---|---|
| `commercialUssageName` | **`Disallow`** | 已由 `Allow` 修正 |
| `licenseName` | `Other` | 保留 |
| `otherLicenseUrl` | 原独立许可链接 | 保留 |
| `author` | 辉歪歪 | 保留 |
| `allowedUserName` | `ExplicitlyLicensedPerson` | 保留；符合独立许可的公众即为获许可使用者，无需逐人申请 |
| `violentUssageName` | `Disallow` | 保留 |
| `sexualUssageName` | `Disallow` | 保留 |

独立许可链接：https://github.com/LYFshota/EmbodiedNPC/blob/main/UnityClient/Assets/VRMCompatibilityTest/Xingmang.LICENSE.md

**本轮已仅将 VRM 的商业标志从 `Allow` 改为 `Disallow`；Unity Meta 未改。** JSON 深比较仅此一处语义变化，其他 JSON 内容和整个 BIN 分块保持不变，容器长度及四字节对齐验证通过。候选没有既有 Library，本轮未启动其他候选 Editor，也未执行 Unity 导入；这不是新的导入 PASS。本许可证已由所有者最终确认并正式采用；元数据修正本身不替代该书面授权，也不等于已发布整个源码候选。VRM 0 的规范字段拼写即 `commercialUssageName`，不改成 VRM 1.0 字段，也不把自定义项目许可误标为标准 CC BY-NC。

依据：[VRM 0 官方元数据 Schema](https://github.com/vrm-c/vrm-specification/blob/master/specification/0.0/schema/vrm.meta.schema.json)、[VRM 0 官方说明](https://github.com/vrm-c/vrm-specification/blob/master/specification/0.0/README.md)。
