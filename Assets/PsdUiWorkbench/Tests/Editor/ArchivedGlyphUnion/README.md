# 邻字合成历史实验（已退役）

实际 PSD 对照中的收益不足以支持逐段运行时组件、材质实例与布局纹理成本，当前方案已退回普通 TMP + 单字形 PSD 外描边。

本目录只保留实验源码、Shader、Prefab 和历史测试作为对照，不属于正式导出路径。资源通过 AssetDatabase.MoveAsset 迁移，保留 GUID；不得直接用于 Player。原程序集名称 `PsdUiWorkbench.Runtime` 仅为兼容历史脚本身份保留，现已限定 `includePlatforms: [Editor]`，不进入 Player 程序集；正式 Infrastructure 与 Presentation 均不引用它。组件已从 Add Component 菜单隐藏，历史测试标记 Explicit，不进入普通回归。

`Assets/` 保存此前静态／动态邻字 Prefab 和所属资源，`Shaders/` 保存邻字 Shader，`Runtime/` 保存已停用的同步器，`Tests/` 保存原型及验证。已导出资源若依赖其他正常字库，仍按 GUID 引用工具现有资产。

当前使用方式及验证以工具根目录 README、Documentation/TMP_SINGLE_GLYPH.md 为准。历史 JSON 里的旧路径是当时的真实输出记录，保留原文，不代表当前仍提供该导出选项。
