# 战斗主菜单 PSD：真实文本审计与 TMP 填充对照

本轮完成源文字／样式审计、已有字体的实际渲染对照及缺字体段的固定占位验证，尚未把文本转换接入正式导出。

源资源为 `Assets/Psd/战斗-主菜单界面.psd`：1080×2400、8 位 RGB、72 DPI。86 条原始图层记录中包含 17 条分组结束标记；有效节点 69 个，其中 **20 个文本节点**。全部文本的现代 TySh、EngineData、效果 Descriptor 均读取成功，并按 `lyid` 与官方快照关联。

## 本轮代码的位置与边界

- **源格式审计**：`Tests/Editor/psd_text_audit.py`、`test_psd_text_audit.py`。只读的验证原型，解析有边界和复杂度限制的 Descriptor/EngineData；不调用旧插件，不解压图层像素，不修改 PSD。文件哈希会顺序读取源文件，不能将“未解压像素”误解为哈希也不读文件内容。
- **Unity 对照**：`Tests/Editor/PsdTextReferenceValidation.cs`、`PsdTextReferenceTests.cs`。读取现有官方快照和 Sprite，在 Preview Scene 渲染，使用临时 Font Asset/材质；不调整源字体资产、Importer 设置、显隐配置或正式 Prefab。
- **证据**：`Documentation/PsdTextAudit/` 保存原始数据、结构化文本清单、字体扫描结果、逐次参数测量和实际渲染。

本审计阶段最初仍全部位于验证代码。后续已将文本模型、布局规划、TMP 构建和候选 Prefab 保存独立到 Core/Infrastructure，见 [可编辑 TMP 候选](TMP_CANDIDATE_PREFAB.md)。当前 Importer 不依赖 Python，正式窗口也不读取本报告；源结构的 C# 读取与缓存仍需后续接入。

## 真正使用了哪些字体

只统计可见文本样式段实际引用的字体，不将 FontSet 内未使用的 AdobeInvisFont、AdobeHeitiStd 等条目当作缺失依赖。

| PostScript 名 | 使用情况 | 本机结果 |
| --- | --- | --- |
| TOXIA | 18 层使用，其中 13 层只使用它 | 找到项目内 `Toxia_FRE.ttf` |
| Rubik-Medium | 5 层混排，主要是 `/` 与 `:` | 按已确认规则使用默认字体固定占位 |
| JiangChengYuanTi-500W | `-`、`.` 两层 | 按已确认规则使用默认字体固定占位 |

最初扫描项目、Windows 系统字体及当前用户字体目录，共 406 个字体文件，按内部 PostScript 名匹配，不依赖文件名猜测；TTC 遍历全部字体面，扫描无读取错误。结果见 [fonts.json](PsdTextAudit/fonts.json)。后续按用户确认的占位规则继续，不再查找这两个字体，也不把缺失视为阻塞项。

TOXIA 仅能确认当前文件的内部名称与 PSD 一致，并记录 SHA-256；PSD 不携带原始字体文件哈希，不能证明当前文件与美术原机字体的版本完全相同。

## 缺字体段的已确认规则

使用 `TMP_Settings.defaultFontAsset`，本项目当前为 `LiberationSans SDF`。占位字号固定 **36 UI 单位**，不乘源文本字号或缩放；使用普通字重、白色，不继承原稿的粗体、字距、描边、阴影或其他效果。保留原文字内容，用作可见占位。

只替换缺失的样式段。例如 `1/10` 中只有 `/` 使用默认字体，前后的数字仍用 TOXIA，并保留其字号和候选描边。不同段使用独立的临时 TMP 组件与材质验证效果隔离；这只是 Editor 验证结构，不是正式导出的最终运行时层级设计。

7 个缺字体段均已生成占位预览，含源未导入的 ID 947 的实验截图；没有因此修改官方导入勾选或正式导出的 DrawNodes 筛选。占位不参与严格的原稿匹配统计，不再开展对应字体的还原处理。

## 20 层文字与样式清单

以下字号为本文件 72 DPI、正向均匀缩放后的候选 UI 单位，不是直接抄 Sprite 高度。所有文本都设置了 FauxBold=true；19 层 Tracking=100，计时器 `29:51`（ID 1067）为 200。所有当前启用的图层效果均只有单色外描边，效果 Scale=100%。投影、内阴影、发光等虽保留参数，但未启用，不能因字典中存在它们就创建对应效果。

| 图层 ID | 文字 | 样式段字体 | 候选 UI 字号 | 外描边原值 | 对照状态 |
| ---: | --- | --- | ---: | ---: | --- |
| 936 | 1/10 | TOXIA／Rubik-Medium／TOXIA | 30 | 2 px | 分隔符默认占位 |
| 937 | 5 | TOXIA | 40 | 3 px | 已测填充 |
| 947 | 10/10 | TOXIA／Rubik-Medium／TOXIA | 24 | 2 px | 分隔符占位验证，官方仍未导入 |
| 948 | 4 | TOXIA | 40 | 3 px | 已测填充 |
| 959 | 3 | TOXIA | 40 | 3 px | 已测填充 |
| 969 | 5 | TOXIA | 40 | 3 px | 已测填充 |
| 979 | Backpack | TOXIA | 38 | 5 px | 已测填充 |
| 994 | Emergency | TOXIA | 38 | 5 px | 已测填充 |
| 1001 | 60 | TOXIA | 34 | 2 px | 已测填充 |
| 1008 | 60 | TOXIA | 34 | 2 px | 已测填充 |
| 1015 | 600/600 | TOXIA／Rubik-Medium／TOXIA | 28 | 3 px | 分隔符默认占位 |
| 1020 | 3/3 | TOXIA／Rubik-Medium／TOXIA | 30 | 2 px | 分隔符默认占位 |
| 1057 | `16  4Zombie  ` | TOXIA | 48 | 3 px | 已测填充，空格原样保留 |
| 1058 | Hell | TOXIA | 48 | 3 px | 已测填充 |
| 1059 | - | JiangChengYuanTi-500W | 48 | 3 px | 默认字体固定占位 |
| 1060 | . | JiangChengYuanTi-500W | 48 | 3 px | 默认字体固定占位 |
| 1067 | 29:51 | TOXIA／Rubik-Medium／TOXIA | 30 | 2 px | 分隔符默认占位 |
| 1081 | 43 | TOXIA | 40 | 2 px | 已测填充 |
| 1090 | 0 | TOXIA | 50 | 3 px | 已测填充 |
| 1091 | Energy Level | TOXIA | 34 | 3 px | 已测填充 |

实际段落终止符、样式段起点和长度、原始变换、颜色、描边不透明度均保留在 [inventory.json](PsdTextAudit/inventory.json)。ID 947 和 948 的描边不透明度为 80%，其余为 100%。EngineData 段长度按 UTF-16 单位校验；输出候选文字只移除一枚段尾 CR，不对内部空格或换行做 Trim。

例如 Energy Level 的 StyleRun.FontSize≈44.6346，TySh 均匀缩放≈0.7617409，相乘约为 **34**。Backpack 使用约 38 UI 单位。原始 `bounds`、`boundingBox`、图层像素矩形与变换基线分别保留，不能将含光学边界的矩形当作文本框并强制缩放填满。

## 对照基准的限制

官方 12.0.2 文档明确说明，Importer 生成 Sprite 时忽略图层 Effects、Blend Modes、Layer Opacity 等属性。因此官方 Sprite 在本轮只用于**填充形状和布局**对照，不用于验收 Photoshop 外描边或投影。依据见 [Unity PSD Importer 官方说明](https://docs.unity3d.com/Packages/com.unity.2d.psdimporter@12.0/manual/index.html)。

本轮的 `*-tmp-outline-candidate.png` 使用上一阶段已验证的外描边通式和 PSD 中记录的描边参数生成，属于候选效果，尚无 Photoshop 单层效果渲染基准，不宣称原稿效果验收通过。

## 13 层 TOXIA：390 次实际渲染测量

每层测试 `_WeightBold`=0/0.25/0.5/0.75/1、临时字体 boldSpacing=0/2.5/7、TMP kern 开／关，共 30 种组合。保留 PSD 推导的字号、字距、对齐和基线，不优化文本 X/Y，不缩放填满 Sprite 边界。

Tracking=100 按 0.1 em 转为本版 TMP characterSpacing=10。TMP 的字偶调整、字形 advance、boldSpacing 会进一步影响实际间距；源文件 AutoKerning=true 不意味着“直接启用 TMP kern 后一定相同”。本版 TMP 对某些字体调整记录还可能忽略额外 spacing，因此 kern 开关属于诊断实验，不能把关闭它无条件写入正式转换规则。

离屏渲染保持 1 UI 单位=1 像素，截取窗口相对 PSD 使用整数平移，保留原始小数基线，避免额外重采样改变比较相位。填充参考使用临时的预乘 Alpha 材质，半透明覆盖探针实测 128/255≈0.501961；不把不同混合方式的 Alpha 输出当成字形差异。

评价指标为 `sum(abs(候选 alpha - 参考 alpha)) / sum(参考 alpha)`，只评价覆盖，不评价颜色、描边或与 Photoshop 的整体相似度。13 层等权平均：

- TMP 默认候选（WeightBold=0.75、boldSpacing=7、kern 开）为 **64.024%**。
- 每层在上述有限网格中的最低候选，平均为 **27.625%**；单层约 **22.847%～34.730%**。
- 统一用 WeightBold=0.25、boldSpacing=2.5、kern 关，平均约 **28.828%**、最差约 36.814%。WeightBold=0.5 的平均约 28.938%、最差约 34.730%。这说明存在较好的公共起点，不能据此证明它是跨字体公式或唯一正确参数。

| 层 | 默认候选差异 | 网格最低候选差异 | 参考/候选二值覆盖 IoU |
| --- | ---: | ---: | ---: |
| Backpack（979） | 93.16% | 28.52% | 0.753 |
| Emergency（994） | 97.38% | 26.74% | 0.770 |
| Energy Level（1091） | 122.29% | 31.53% | 0.731 |

归一化差异可以大于 100%，不是准确率或错误字符比例；细笔画和 1 像素边缘差异会显著影响这个指标。最优仅指有限网格中对本参考指标最低，不代表全局最优，也不是建议逐层自动拟合原稿。

参数变化能减少差异，但仍没有达到像素级一致。剩余差异应继续区分光栅化/抗锯齿、Hinting、伪粗体规则、字体版本及排版实现。它们不能全部归因于 Outline＋Face Dilate，也不应通过每层任意 X/Y 或缩放修正藏起来。

数据见 [summary.json](PsdTextAudit/summary.json)、[unity-reference.json](PsdTextAudit/unity-reference.json)。读取后者时必须检查 `measured`；固定占位记录没有做原稿匹配，其空嵌套对象可能被 JsonUtility 序列化为零值，不能按“零误差”统计。`placeholderSpans` 记录逐段实际字号、字体和效果开关。

## 渲染证据

| 层 | 官方填充参考 | TMP 填充候选 | TMP 外描边候选，尚未验收原稿效果 |
| --- | --- | --- | --- |
| Energy Level | [参考](PsdTextAudit/1091-official-fill.png) | [填充](PsdTextAudit/1091-tmp-fill.png) | [描边](PsdTextAudit/1091-tmp-outline-candidate.png) |
| Backpack | [参考](PsdTextAudit/979-official-fill.png) | [填充](PsdTextAudit/979-tmp-fill.png) | [描边](PsdTextAudit/979-tmp-outline-candidate.png) |
| 16  4Zombie | [参考](PsdTextAudit/1057-official-fill.png) | [填充](PsdTextAudit/1057-tmp-fill.png) | [描边](PsdTextAudit/1057-tmp-outline-candidate.png) |

缺字体占位另有 7 张截图，例如 [1/10 混排](PsdTextAudit/936-placeholder.png)、[600/600 混排](PsdTextAudit/1015-placeholder.png)、[单独的“-”](PsdTextAudit/1059-placeholder.png)。数字段的描边没有传给默认字体的分隔符。

## 验证和后续接入边界

Python 格式测试 7/7 通过，包含 PSD/PSB 内存夹具、奇数未知块、单位和 Unicode/转义、重复键、未知 Descriptor 类型、逐字节截断，以及真实 PSD 的 ID/样式段与禁用效果检查。实验读取器尚不支持全局 Layr 图层区、16/32 位、旧 tySh 样式和任意 Descriptor 变体，不能作为完整 PSD 解析库发布。

完整 Unity EditMode 回归 **36/36 通过**，无失败或跳过，结果见 [tests.json](PsdTextAudit/tests.json)。占位测试检查字号固定、普通字重、无描边/阴影、混排段效果隔离，以及源资源和默认字体不被改变。

最终一轮编译成功、没有新增错误；没有清空日志。Console 仍保留早先失败测试的一条 Editor 释放错误，以及临时 PSD 图层名 `.` 被官方改为 `._` 的警告。早先失败留下的测试专用窗口、Editor、配置和临时 PSD 副本已清理；最终测试目录没有遗留副本。最新占位验证还检查默认字体材质序列化值与 boldSpacing 均保持，见 [placeholder-tests.json](PsdTextAudit/placeholder-tests.json)。

回归暴露出一个原有测试的时序问题：打开原生属性窗口后又手动创建同一 Importer 的 Editor，可能同时存在两个所有者。测试已改为先关闭本次创建的属性窗口、等待一帧并断言释放，再由测试创建官方 Editor；没有修改生产桥接 Editor 或忽略错误日志。

Unity 实测 PSD、`.meta`、TOXIA 字体及项目默认 TMP 字体资产的哈希均未改变，临时 Preview Scene、活动场景、RenderTexture.active 恢复。没有创建 TMP Font Asset 或 Material 资产，没有改正式 Prefab。

生产接入沿用上述默认字体固定占位策略，不要求补齐缺失字体。仍应完成：可继承的样式段/段落模型；单行/多行及不同字体的布局验证；不依赖官方 Sprite 的 Photoshop 效果参考；图层不透明度和颜色空间验收。不要把样式段统一成首字体，也不要默认启用文件中禁用的阴影。前述数学外描边补偿仍可复用，本轮不否定它；当前未解决的是已有字体的 PSD 文本整体排版与光栅化一致性。

复现审计：

```text
python -B Assets/PsdUiWorkbench/Tests/Editor/psd_text_audit.py Assets/Psd/战斗-主菜单界面.psd Assets/PsdUiWorkbench/Documentation/PsdTextAudit/source.json
python -B -m unittest discover -s Assets/PsdUiWorkbench/Tests/Editor -p test_psd_text_audit.py -v
```

最初的字体扫描可用 `--scan-fonts` 复现，需要本机已安装的 fontTools；当前缺字体直接占位，不要求再次扫描。PSD 结构审计本身只用 Python 标准库。Unity CLI script eval 执行 `return PsdUiWorkbench.Tests.PsdTextReferenceValidation.Run();`，生成对照证据。单元测试用 `Measure(false)`，不覆盖交付报告和 PNG。

结构格式依据 [Adobe PSD/PSB 文件规范](https://www.adobe.com/devnet-apps/photoshop/fileformatashtml/)；EngineData 语法参考 [psd-tools 的维护者源码说明](https://github.com/psd-tools/psd-tools/blob/main/src/psd_tools/psd/engine_data.py)。这里的独立审计不调用 psd-tools 或旧插件，不将第三方说明当成 Photoshop 排版与 TMP 对应关系的保证。
