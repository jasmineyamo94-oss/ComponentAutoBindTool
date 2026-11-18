# 标准 TMP 外描边补偿验证

验证日期：2026-11-12。环境：Unity 6000.3.20f1，项目自带标准 TMP SDF Shader。

本轮已得到不依赖字体名称的 SDF 轮廓换算关系。它能把居中描边补偿为外描边，同时保持原填充阈值；不能承诺所有字体、任意字号与 Photoshop 像素完全相同。1 像素细描边、相邻字符遮挡、距离场余量不足都有实际反例。

本轮只增加 Tests/Editor 验证工具、TMP 测试程序集引用和证据，正式 Prefab 导出仍为 Image。没有解析 PSD 文字样式、改变 Importer 或修改现有字体、材质、Prefab。

后续扩展已完成：15 个字体文件／字面、3024 组追加矩阵及 SDF16/Sharpness 对照见 [扩展验证](TMP_OUTLINE_EXTENDED_VALIDATION.md)。下文保留最初 864 组数据，不将两轮不同样本混为同一矩阵。

## 1. 实测范围与结果

字体为 TOXIA、Liberation Sans、Noto Sans CJK SC。前两者用 `H8O`，中文字体用 `H田国`。分别测试：

- `TextMeshPro/Mobile/Distance Field` 和 `TextMeshPro/Distance Field`。
- SDFAA 字库，采样字号 64/96，Atlas Padding 6/12，1024×1024 临时图集。
- 渲染字号 32/64/96，正常/合成粗体，目标外扩 1/3/5 像素。
- 默认字距，以及额外 characterSpacing=40 的分离字形。增加字距只是为了隔离相邻字符遮挡，不是最终导出的修复策略。
- 总计 **864 组**，每组与同字体、同字号、同排版的无描边基准比较。另有非零初始字重/Face Dilate 探针，以及 0.5/1/2 倍均匀缩放测试。

864 组中，684 组可表达所需轮廓；其中 648 组阴影参数也在标准 Inspector 范围内，36 组阴影需增加距离场余量。180 组连所需轮廓都超出当前字库与参数范围，明确拒绝，不能将它们算成还原成功。

| 字距 | 目标外扩 | 可表达组数 | 最大填充通道相对差异 | 填充 0.5 阈值边界框最大变化 |
| --- | --- | ---: | ---: | ---: |
| 默认 | 1 px | 144 | 12.342% | 1 px |
| 默认 | 3 px | 120 | 1.087% | 0 px |
| 默认 | 5 px | 78 | 7.436% | 0 px |
| 分离字形 | 1 px | 144 | 12.328% | 1 px |
| 分离字形 | 3 px | 120 | 0.000139% | 0 px |
| 分离字形 | 5 px | 78 | 0% | 0 px |

这里的相对差异为 `sum(abs(补偿图红通道 - 基准图红通道)) / sum(基准图红通道)`，不是出错像素占比，也不是与 Photoshop 的差异。红色填充、绿色描边用于区分通道。0 表示本次 8 位输出中逐像素相同；非零也可能来自描边的混色，不能全部解释成字体变细。

全部可表达轮廓中，alpha=0.5 的整体边界框四边平均外扩量与目标最大相差 **0.75 px**。这是整数边界框指标，不代表所有局部轮廓的距离误差上限。字形曲率、SDF 采样与阈值量化仍会影响外边界。

648 组合法阴影的最大相对蓝通道差异约 **0.000417%**。36 组越界阴影即使数学上能输出接近基准的结果，也不属于标准参数范围内的成功。验证程序分别标记它们。

所有可表达样本的首字符 origin 与 baseline 偏移均为 0。分离字形后大描边误差消失，确认其中一项原因是相邻字符的描边覆盖，而非排版中心发生移动。

原始证据：[完整矩阵](tmp-outline-validation.json)、[字重探针](tmp-outline-weight-probe.json)、[测试结果](tmp-outline-tests.json)。GPU/采样条件变化时应重跑，不要求每次浮点误差完全相同。

## 2. 外描边换算公式

以下针对上述标准 SDF Shader，`Outline Softness=0`、Ratios 启用、均匀缩放、正交参考画布。不是 Bitmap、Sprite Shader、任意自定义 Shader 的通式。

| 记号 | 含义 |
| --- | --- |
| G | 材质 `_GradientScale`，本实验为 Atlas Padding+1 |
| S | 当前字体字形的实际缩放，读取 `TMP_CharacterInfo.scale`，不要只凭字体名称或 fontSize 推测 |
| C | UI 单位到屏幕像素的均匀缩放，本矩阵为 1 |
| p | 希望外扩的屏幕像素数 |
| A0、A | 基准、最终 `_ScaleRatioA` |
| D0、D | 基准、最终 `_FaceDilate` |
| W | 最终 `_OutlineWidth` |
| w | 当前字形的 Shader 字重：正常 `_WeightNormal/4`，合成粗体 `_WeightBold/4` |

定义：

```text
q = p / (2 × G × S × C)
L = (w + D0) × A0

W = q / A
D = (q + L) / A - w
```

它保持以下内边界不变量：

```text
(w + D - W) × A = (w + D0) × A0
```

原来没有额外字重和 Face Dilate 时（w=0、D0=0），简化为：

```text
Outline Width = p / (2 × GradientScale × 字形缩放 × 屏幕缩放 × ScaleRatioA)
Face Dilate   = Outline Width
```

例如 G=13、S=1、C=1、A=12/13，要外扩 2 px：W=D=0.0833333。在 TOXIA 96 字号样本中，不补偿的填充通道差异为 15.893%；补偿后为 0，实测外扩 2 px。

**PSD 导出时应保留设计单位。** Content 的 1 UI 单位对应 1 PSD 像素时，若目标是 PSD 原稿的 pPSD 像素描边，取 `q=pPSD/(2GS)`；屏幕显示时自然外扩 `C×pPSD` 像素。只有刻意要求屏幕固定像素宽度时，才按上式除以 C。不能每次 CanvasScaler 缩放都无条件抵消描边缩放。

### 不能固定读取旧 ScaleRatioA

当前 TMP 的 UpdateShaderRatios 会依据最终的 Face Dilate、Outline Width 和最大字重重算 A。令：

```text
r = (G - 1) / G
wMax = max(_WeightNormal, _WeightBold) / 4
A = r / max(1, wMax + D + W)
```

求解顺序：

1. 先假设 A=r，计算 W、D；如果 `wMax+D+W<=1`，此分支成立。
2. 否则，使用饱和分支 `A=(r-2q-L)/(wMax-w)`，再计算 W、D。
3. 分母为零、A<=0、W 超出 [0,1]、D 超出 [-1,1]，或代回官方 UpdateShaderRatios 后不满足方程时，拒绝该组合。不要强制 Clamp 后宣称还原成功。

验证程序采用这个解析解。接近饱和时，固定次数迭代可能尚未收敛，曾在有效组合上造成误判。

字重探针：G=13、S=0.5、p=4.5、w=0.1、D0=0.08。A 从 0.9230769 变成 0.7384614，W≈0.4687501、D≈0.5937502。简单 `D=D0+W` 的填充差异为 **5.574%**，通式补偿后为 **0**，填充二值边界也保持。该探针的阴影超出标准范围，不能据此宣称阴影也可直接使用。

## 3. 阴影必须一起补偿

Face Dilate 会同时改变 Underlay 的轮廓与可用余量。令 C0/C1 在本节专指基准/最终 `_ScaleRatioC`（区别于上节的屏幕缩放），U 为 `_UnderlayDilate`：

```text
U1 = [U0×C0 + (w+D0)×A0 - (w+D1)×A1] / C1
OffsetX1 = OffsetX0 × C0/C1
OffsetY1 = OffsetY0 × C0/C1
Softness1 = Softness0 × C0/C1
```

重算官方 ratios 后检查收敛、C1>0 和参数范围。Offset/Dilate 不能超过 [-1,1]，Softness 不能超过 [0,1]。本轮验证采用 Offset=(0.15,-0.1)、Dilate=0、Softness=0.08 的原生 Underlay 基准；单独读取蓝色阴影覆盖，避免 Face/Outline 遮挡投影后误判为阴影尺寸改变。

TOXIA 96 字号、外扩 2 px 时，不校正的阴影覆盖相对差异为 16.071%；联动校正后为 0。这里证明的是 **TMP 原生阴影在加外描边前后保持**，没有证明 Photoshop 的距离、扩展、大小、混合模式与原生 Underlay 一一等价。

## 4. 已确认的限制与下一阶段处理

- **1 px 细描边混色**：标准 Mobile Shader 使用 `lerp(faceColor, outlineColor, sqrt(min(1, outline*2)))`；标准完整版也有相应细描边混合。即使内轮廓阈值相同，描边颜色仍可能混入填充色。仅调 D/W 无法保证红通道完全相同。若坚持标准 Shader，应设置视觉容差并提示，而不能承诺逐像素还原。
- **相邻字符遮挡**：每个字符自身的描边会参与字符顺序叠放，粗描边可能盖到邻字 Face。TOXIA 32 字号/5 px 样本默认字距差异 7.436%，分离后为 0。不能为了消除它自动改 PSD 字距；需要接受差异，或后续单独评估整行“描边在后、填充在前”的绘制方案。
- **距离场余量**：加大材质参数不会创造图集里不存在的距离信息。Padding/采样字号/最终字号共同决定可表达范围。采样越大不代表描边空间一定越大，应根据目标 UI 尺寸计算并实测。
- **字号和基线**：本轮与同一个 TMP 基准比较，未验证 PSD 字号、分辨率、文本变换矩阵、段落框、行距、字距、基线偏移。后续转换仍须读取这些元数据；不能用含描边/阴影的 Sprite 边界当文本框来缩放填充。
- **回退字体和混排**：公式不包含字体名称，但 S、字重、图集 G 和材质 ratios 均可能随字体变化。需按实际 Font Asset/材质组换算；同一文本含不同字号时，一套材质参数不能保证所有字符拥有相同绝对像素描边。
- **未覆盖**：任意字体、全部字形、可变字体、真实粗体替代字库、SDF16/SDF32、非均匀缩放、透视、HDR/MSDF、自定义 Shader、渐变/多重描边、Photoshop 抗锯齿与效果混合。不得将三个字库样本的结果推广为已验证全部字体。

建议后续转换采用“统一公式＋按字库检查可表达范围＋逐层诊断”的策略。字号/基线、外描边、阴影分开验收；标准材质按字体和样式复用。求解及校准放在 Editor 导出阶段，不增加每帧校准、读像素或材质实例化。

## 5. 复现与证据

`Tests/Editor/TmpOutlineValidation.cs` 通过临时 Font Asset、World Space UGUI Canvas、Preview Scene、正交 Camera、线性 768×256 ARGB32 RenderTexture 渲染。1 倍缩放时 1 UI 单位=1像素，禁用 AutoSize/换行，Outline Softness=0，Sharpness=0，开启 Extra Padding。生成的图集、材质、GameObject、RenderTexture、读回纹理和 Preview Scene 均释放；报告与对照 PNG 是显式交付证据。

最终 Workbench EditMode 回归 31/31 通过，无失败或跳过，其中 5 项为新增 GPU 验证。0.5/1/2 倍缩放下，填充相对差异小于 0.01%，二值填充边界保持，2 px 目标外扩的边界框平均量误差不超过 0.25 px。测试对各次 Probe 前后场景及渲染目标状态作了断言。

通过 Unity CLI 的 script eval 执行：

```csharp
return PsdUiWorkbench.Tests.TmpOutlineValidation.Run(true);
```

864 组会超过默认 30 秒 CLI 超时，需传 `--timeout 120`。返回 valid 只统计轮廓，阴影还应按 JSON 的具体 status 检查。`Probe(...)` 返回单条结果且不写文件；`TmpOutlineTests` 以真实 GPU 检查均匀缩放、一般字重补偿、越界拒绝及 Preview Scene 释放，`-nographics` 下明确跳过，不当作图形验证通过。

每种字体都有 baseline / centered / compensated 三张原生渲染。红色为填充，绿色为描边：

| 字体 | 无描边基准 | 默认居中描边 | 补偿外描边 |
| --- | --- | --- | --- |
| TOXIA | [基准](outline-Toxia_FRE-baseline.png) | [居中](outline-Toxia_FRE-centered.png) | [补偿](outline-Toxia_FRE-compensated.png) |
| Liberation Sans | [基准](outline-LiberationSans-baseline.png) | [居中](outline-LiberationSans-centered.png) | [补偿](outline-LiberationSans-compensated.png) |
| Noto Sans CJK | [基准](outline-NotoSansCJKsc-Regular-baseline.png) | [居中](outline-NotoSansCJKsc-Regular-centered.png) | [补偿](outline-NotoSansCJKsc-Regular-compensated.png) |

轮廓、Face Dilate、Underlay 和 Gradient Scale 的概念参见 [Unity 官方 Distance Field Shader 文档](https://docs.unity.cn/Packages/com.unity.textmeshpro@4.0/manual/ShadersDistanceField.html)。本文公式来自当前项目本地标准 Shader、UpdateShaderRatios 实现及以上实验推导，不是官方对 Photoshop 兼容性的承诺。
