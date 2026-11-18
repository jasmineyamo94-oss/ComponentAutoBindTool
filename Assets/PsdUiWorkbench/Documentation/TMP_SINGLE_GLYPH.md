# 普通 TMP + 单字形 PSD 外描边

2026-11-16 已取得独立 Photoshop PNG，后续对照直接读取其像素，不再自动去白底，见 [基准复核说明](PSD_REFERENCE_CORRECTION.md)。本页方案及正式参数保持；本轮 Python 19/19、Unity 定向 1/1 通过，Unity 66/66、Python 13/13 为 2026-11-14 的历史回归结果，均不等于视觉还原证明。

实际 PSD 中邻字合成的改善难以察觉，不再为其默认增加每段运行时组件、材质实例与布局纹理。当前推荐方案为普通 TextMeshProUGUI + `TextMeshPro/Mobile/Distance Field - PSD Outer Stroke`，接受极少数字形交界处的轻微覆盖。

## 当前入口与交付

工作台“导出 UGUI Prefab” → **PSD 外描边（推荐，单 Pass）**。标准 TMP 保留为兼容选项。`PsdTextPrefabExport.Export`、`PsdTmpPrefabExporter.Export` 和窗口导出 API 的默认值统一为单字形外描边；枚举旧名称 `OuterStrokeExperimental` 保留，避免无必要的调用方改名。邻字模式已从枚举和菜单移除，旧数字值 2 会明确拒绝。

当前资产：[精简 Prefab](<../Exports/战斗-主菜单界面_单字形共享材质.prefab>)。19 个文本层、27 TMP、32 Image、6 个缺字体段、14 个共享文字材质。没有 Workbench 运行时组件、邻字布局属性、邻字纹理或历史实验依赖。当前 Player 编译程序集列表中没有 PsdUiWorkbench 程序集。[输出检查](PsdTextAudit/StrokeDiagnosis/single-glyph-output.json)、[窗口导出](PsdTextAudit/StrokeDiagnosis/single-glyph-export.json)。

文字正常通过 TMP 的 text、fontSize、characterSpacing、排版属性等修改，不需要自定义同步脚本；不存在邻字原型的 32 字形／单行限制。实际可显示字符仍受字体图集和 TMP 回退机制约束。使用 fontSharedMaterial 修改公共样式会影响共享者，业务改色优先使用文字 color；调用 TMP fontMaterial 等实例化 API 仍可能由 TMP 创建副本，本工具不改变其原生行为。

## 材质共享

Infrastructure 的 MaterialStore 先配置最终材质，再以字体资产身份和完整序列化材质属性比较结果。键包含 Shader、图集、关键字和材质属性，不再直接以字号、Tracking、伪粗体标记、原始描边像素值拆分。相同最终渲染状态复用持久化材质；候选临时材质即刻释放，仅在离线导出时进行比较，不增加运行时比较或缓存逻辑。

字号／字距／粗体标记可由各个 TMP 的几何独立表达。描边所需距离场宽度、填充色或其他实际材质属性不同，仍然必须分开，不能为减少数量强行合并。字体身份不同也不合并。原字号、Tracking、伪粗体 advance 的有界补偿、布局和显隐规则均保留，Core 没有为材质共享增加 Unity 依赖。

针对性 Prefab 回归验证：16 号普通字和 48 号伪粗体、不同 Tracking，在最终材质相同且无描边时共享；不同填充色、描边状态不合并；保存后重载保持共享引用，且没有生成布局纹理或运行时组件。标准 TMP 路径继续保留。

## 视觉与成本

实际整图截图已查看：[当前](PsdTextAudit/StrokeDiagnosis/single-glyph.png)、[原单字形基线](PsdTextAudit/StrokeDiagnosis/single-glyph-before.png)。两份 PNG 完全一致，说明这次简化与缓存调整没有改变该基线的像素输出；不是与 Photoshop 完全一致的声明。

该 PSD 原单字形基线也为 14 个文字材质，本次没有进一步下降。相比邻字动态原型的 21 个同步组件、21 张约 1 KB 原始布局纹理及 22 种活动文字材质，当前不再承担这些同步和邻字采样成本；这里只报告结构差异，不能换算成移动端的实际帧率或批次数收益。共享材质仍可能受图集、层级交错、Mask 等条件影响 UI 合批。

Face Dilate 仍为 0，现有屏幕导数 AA 和当前 TOXIA 伪粗体起点 0.35 保留。整体字形偏胖需要继续对字体、字重、描边和原稿做联合对照；本轮没有在缺少新证据时继续修改统一字重，也不宣称达到 98% 或全部字体适用。缺字体保持默认 TMP 字库、固定 36 UI、白色、普通字重、字距 0、无效果占位。

## 分层与历史实验

正式三层均为 Editor：Core 负责文档／布局，Infrastructure 负责最终材质复用与 Prefab 保存，Presentation 提供两种导出选项。Infrastructure、Presentation 不再引用历史同步器；没有新增或修改运行时业务代码、Importer 或旧插件。

邻字源码、Shader、测试、静态／动态样板经 AssetDatabase.MoveAsset 迁入 [Editor 历史目录](../Tests/Editor/ArchivedGlyphUnion/README.md)，保留 GUID。原程序集身份为兼容旧脚本引用保留，但 includePlatforms 已限定 Editor。组件从 Add Component 菜单隐藏，历史测试设为 Explicit，不进入常规回归。历史 JSON 路径保留当时的记录，旧实验 Prefab 不应作为项目 UI 使用。

测试与 Console 最终结果见 [VALIDATION](../VALIDATION.md) 和 [本轮回归原始记录](PsdTextAudit/StrokeDiagnosis/single-glyph-tests.json)。

本轮完整回归 **65/65 通过**，无失败／跳过。包含实际 Play Mode 帧数推进，不调用 ForceMeshUpdate，连续改为短文本、60 字符长文本和两行文本，材质引用保持相同，没有 Workbench 运行时组件。原 Importer 缓存、Sprite 引用、显隐、布局、源材质保护及两种现行导出模式继续通过。历史 Explicit 实验不计入本轮测试数。

归档后的两个样板均无 Missing Script，当前与历史 Shader 均无编译错误；正式代码不存在邻字组件／程序集引用。[最终检查](PsdTextAudit/StrokeDiagnosis/single-glyph-final.json)。源 PSD 和 .meta 的 SHA256 仍与初始审计一致。移动端批次和 GPU 验收尚未执行。

Console 实时检查为编译正常、错误 0。警告包含故意截断文本描述符的诊断用例、官方 Importer 对原 PSD 中 `.` 图层名的提示，以及 Test Runner 将本轮保留的 4 个截图／JSON 证据标记为新增未清理文件：这些证据在测试前写入、测试期间才完成资源登记，作为交付保留，没有冒充临时资源泄漏修复。临时 PSD／Prefab 测试目录已清理。历史日志未清空。[Console 原始记录](PsdTextAudit/StrokeDiagnosis/single-glyph-console.json)。
