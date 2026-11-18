# Prefab 导出目录

工作台保存对话框默认定位到本目录，可选择其他 Assets 内目录。导出仅新建，同名自动生成唯一文件名。

Prefab 自带 Canvas，引用源 PSD 导入的 Sprite；需要保留源 PSD 及其 .meta。显隐状态在导出时冻结，后续勾选不会自动更新已导出的 Prefab。

当前 TMP 试验样板位于 `战斗主菜单 TMP候选 1/战斗-主菜单界面_TMP候选.prefab`，包含独立字体和材质资源。详见 [候选说明](../Documentation/TMP_CANDIDATE_PREFAB.md)；正式窗口仍使用原 Image 导出入口。跨项目迁移候选时需要包含源 PSD、默认字体以及新字体／材质依赖。
