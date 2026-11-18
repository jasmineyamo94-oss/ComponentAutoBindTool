# TMP 外描边扩展字体验证

本轮在原有 864 组验证之外，新增 **3024 组**。覆盖 **15 个字体文件／字面、12 个字体家族**，继续使用同一 Outline＋Face Dilate 公式，没有添加按字体名称区分的经验修正系数。

结论：扩大字体类型是必要的。2 px 及以上的可表达描边在分离字形后仍能很好地保持填充；1 px 描边在小字号、细笔画字体上的残差明显扩大。标准 Shader 的统一参数不足以保证所有字体与 Photoshop 像素一致。

所有代码、报告、对照图位于本工具目录，正式 PSD 文本导出仍使用 Image。本轮不修改字体文件、现有材质、PSD 或正式导出逻辑。

## 范围与数据完整性

- 环境：Unity 6000.3.20f1、Direct3D11；两个项目现有标准 SDF Shader。每份报告还记录实际 colorSpace、生成时间和用时。
- SDFAA 主矩阵：15 个文件×2 个 Padding（6/12）×3 个渲染字号（16/32/64）×4 个外扩目标（1/2/3/5 px）×正常/合成粗体×默认/分离字距×2 个 Shader，共 **2880 组**。采样字号 90；图集 1024×1024。
- SDF16 对照：TOXIA、Noto Sans CJK、Times New Roman、Segoe UI Light，Padding=12、正常字重、分离字距，其余字号和描边目标同上，共 **96 组**。
- Sharpness 对照：Noto、Courier、Segoe UI Light、SimSun，Sharpness=0.5/1、Padding=12、正常字重、分离字距、1 px 描边，共 **48 组**。
- 英文用 `Hig8O`，覆盖竖线、小点、下伸部和封闭轮廓；中文用 `H永国鬱田`，增加转折和密集笔画。思源宋体 Heavy 本机文件缺少“鬱”，改用它实际包含的 `H永国田`，没有用回退字体补齐。
- 分离字距为 characterSpacing=100，用于隔离字形互相遮挡，不是建议修改原 PSD 字距。

15 个源文件在验证后重新计算 SHA-256，与报告记录一致。全部完成样本均无回退字形、无渲染边缘裁剪、无空的基准填充二值掩码；同一样本各字符的实际缩放一致。批次还检查 Preview Scene 数量、活动场景和 RenderTexture.active 是否恢复。

首轮思源宋体缺字的失败报告保留为 [SDFAA-14-missing-glyph.json](TmpOutlineExtended/SDFAA-14-missing-glyph.json)，不计入 3024 组完成样本。修改的是测试字符，不是源字体。系统字库通过 TMP 公共文件路径接口读取，未复制进工程，也未分发字体文件。换机器复现时，字库文件可能缺失或版本不同，应检查诊断及哈希。

## 15 个字体文件的结果

下表取 SDFAA、分离字距的可表达组合，汇总两个 Shader、三个字号、两个 Padding 和正常/合成粗体。百分比是填充红通道的归一化差异，不是出错像素占比，也不是与 PSD 的相似度。

| 字体文件／字面 | 源字体样式 | 1 px 最大填充差异 | 2/3/5 px 最大填充差异 |
| --- | --- | ---: | ---: |
| TOXIA | Regular | 15.396% | 0% |
| Liberation Sans | Regular | 23.246% | 0% |
| Noto Sans CJK SC | Regular | 22.110% | 0% |
| Arial | Regular | 22.111% | 0.000091% |
| Arial Bold | 真实 Bold | 14.971% | 0% |
| Arial Italic | 真实 Italic | 22.596% | 0% |
| Times New Roman | Regular | 28.362% | 0% |
| Courier New | Regular | 37.619% | 0% |
| Segoe UI Light | 真实 Light | 36.468% | 0% |
| 等线 Light | 真实 Light | 33.956% | 0% |
| 等线 Bold | 真实 Bold | 18.259% | 0% |
| 宋体 TTC face 0 | Regular | 32.131% | 0% |
| 黑体 | Regular | 22.853% | 0.000090% |
| 楷体 | Regular | 27.364% | 0.000063% |
| 思源宋体 SC Heavy | 真实 Heavy，本机子集 | 17.019% | 0% |

源字体的真实 Bold/Light/Italic 与 TMP 合成粗体分别记录；不能把一次合成 Bold 当作测试过了独立的粗体字体文件。TTC 仅测试 face 0，不宣称覆盖整个字体集合。

2880 组主矩阵中，**1440 组**轮廓参数可表达、阴影也在标准参数范围内；另 **1440 组**因当前字号、Padding 和描边目标组合不可表达而被拒绝。例如，小字号想外扩 5 px，并非增加材质值就一定能实现。参数可表达只代表方程与范围成立，不代表视觉无差异。

| SDFAA 情况 | 1 px | 2 px | 3 px | 5 px |
| --- | ---: | ---: | ---: | ---: |
| 默认字距最大填充差异 | 37.615% | 0.07685% | 1.04168% | 0.19616% |
| 分离字形最大填充差异 | 37.619% | 0% | 0.000091% | 0.000091% |

2/3/5 px 可表达样本的填充二值边界框保持，外边界框四边平均扩张量与目标最大相差 0.5 px。这仍是整体整数边界框指标，不是所有局部轮廓误差的上限。

1 px 样本的填充二值边界框最多变化 7 px；同时记录的字符 origin、baseline 并未因此产生排版位移。小点或细笔画在抗锯齿、颜色混合后跨过 0.5 阈值，会使整体二值边界框跳变，不能将这个数直接解释成整段文字移位 7 px，也不能仅靠边界框认定填充保持。

本轮保持同一公式：主矩阵的最大内边界方程残差约 2.76×10^-8，外扩方程残差约 1.29×10^-8。合法阴影的最大相对通道差异约 0.000081%。方程残差与图像差异是两种独立指标。

## SDF16 能否解决 1 px 问题

96 组中 64 组参数可表达，32 组超范围。匹配相同字体、字号、Padding、字距、字重与 1 px 目标后：

- SDFAA 的最大填充差异约 36.468%。
- SDF16 的最大填充差异约 36.585%。

本轮没有发现 SDF16 能普遍消除这一问题。它改变的是距离场生成方式，标准 Shader 的细描边混色仍存在。不能把切换 SDF16 作为统一修复策略。

## Sharpness 联动的收益与代价

每条探针同时比较三个状态：原始 Sharpness=0 的无描边文字、调 Sharpness 后的无描边文字、调 Sharpness 并补偿描边后的文字。这样可以区分“补偿成功”与“先改变基准后看似成功”。

| Sharpness | 相对同 Sharpness 基准的最大填充差异 | 相对原始 Sharpness=0 基准的最大差异 |
| --- | ---: | ---: |
| 0.5 | 0.000134% | 20.627% |
| 1 | 0.000115% | 33.825% |

每个探针的无描边基准本身也改变了：Sharpness=0.5 时至少约 5.29%，Sharpness=1 时至少约 8.05%。所以这不是保持原稿外观的免费补偿。

从当前标准 Shader 可以解释此现象：细描边颜色混合含 `sqrt(min(1, outline*2))`，Sharpness 增大后改变 Shader 的 scale，使混色更快进入饱和分支，同时也改变填充抗锯齿过渡。不能为了降低一个对比指标而忽略另一个基准。

## 当前建议与后续验收

1. 继续使用上一轮推导的通式。当前不需要按 Arial、宋体、楷体等名称增加修正表，也不需要为了验证名义轮廓公式而无限增加字体数量。
2. 下一步应优先覆盖**实际 PSD 使用的字体文件、字号和样式**，以及将来的新增字体；先拿到真实字体映射，再逐层对照原稿。用系统替代字体跑通不等于原稿还原。
3. 2 px 及以上可表达的标准外描边可以作为下一阶段的候选路径；仍需检查邻字遮挡与原稿字距，不能自动拉开字符来隐藏差异。
4. 1 px、小字号、细笔画文字保留明确诊断；Sharpness 可作为用户选择的清晰度策略，不能默认启用并宣称保持填充。是否需要额外绘制方案，应作为后续独立决策，并测量移动端绘制成本。
5. 尚未验证 PSD 字号到 UI 单位、文本框、字体替代、行距、基线、旋转/非均匀变换、真实 Photoshop 投影或混合模式。不要直接把本轮结果等同于文本导出验收。

可持续验证应围绕字体类型和边界条件：真实粗细与斜体、不同字符集、密集笔画、点和细线、小字号、缺字与回退。未来若使用可变字体、彩色字体、其他字体面或自定义 Shader，应增加对应样本，本轮没有覆盖它们。

## 复现及证据

- 验证器：`Tests/Editor/TmpOutlineValidation.cs`；新增矩阵驱动：`Tests/Editor/TmpOutlineExtendedValidation.cs`。它们仅属于 Editor 测试程序集。
- 完整 EditMode 回归 **35/35 通过**，无失败或跳过，原始结果见 [tests.json](TmpOutlineExtended/tests.json)。新增检查覆盖 SDFAA/SDF16 实际渲染、源字形和裁剪校验、缺字时拒绝及 Sharpness 改变原始填充的独立指标。Unity 编译成功，当前 Console 错误为 0。
- [汇总 JSON](TmpOutlineExtended/summary.json) 保存总数、每字体统计、参数范围、Sharpness 对照及失败尝试；各批次原始 JSON 保留逐条数据、源路径/哈希、family/style、采样模式与诊断。
- `success=true` 表示批次完成且没有检测到源字体、裁剪或状态恢复问题，**不表示该批次视觉全部通过**。必须同时阅读行 status 和图像误差。
- 对照 PNG：`SDFAA-06-*` 为 Times，`08-*` 为 Segoe UI Light，`11-*` 为宋体，`13-*` 为楷体，`14-*` 为思源宋体 Heavy；各有 baseline/centered/compensated 三张，在 32 字号、2 px、默认字距下生成。它们是实际 Camera 渲染，不是效果示意图。

通过 Unity CLI 的 script eval 执行，单批次设置 `--timeout 120`；按顺序运行，避免对同一 Editor 并发渲染或重载：

```csharp
return PsdUiWorkbench.Tests.TmpOutlineExtendedValidation.RunBatch(8);
return PsdUiWorkbench.Tests.TmpOutlineExtendedValidation.RunBatch(8, true);
return PsdUiWorkbench.Tests.TmpOutlineExtendedValidation.RunBatch(8, false, 0.5f);
```

第一行是 SDFAA 主矩阵；第二行是 SDF16 对照；第三行是 Sharpness 对照。索引对应矩阵驱动中的固定 15 项目录。缺少文件或字符时保留失败诊断，不自动安装字体、不改名匹配、不借助回退字体。
