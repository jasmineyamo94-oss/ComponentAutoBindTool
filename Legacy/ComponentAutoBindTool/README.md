# 旧 UI 框架归档

本目录保存迁移前工作区中的旧框架源码及原始 .meta，位于 Assets 外，不参与 Unity 导入与编译。暂不删除，恢复时应先检查 GUID、类型和当前依赖，不能直接与新框架并用。

| 旧类型 | 新接入方式 |
| --- | --- |
| ViewLifetimeScope | ViewBase + WindowEntry 创建子 Scope |
| 旧 IAutoBindHost | ComponentAutoBindTool.ViewCore.IAutoBindHost |
| 旧 IUiViewComponent | ComponentAutoBindTool.ViewCore.IUiViewComponent |
| 旧 ViewManager / IViewManager | ViewCore.Management 中的新 ViewManager 与 Contracts |
| ViewBundle / IViewEvent | IViewArgs / WindowPresenterBase 生命周期 |
| 旧 ViewRootHelper / IViewRootHelper | 按 UILayer 提供根节点的新 IViewRootHelper |
| 旧 IViewPrefabHelper | 新 IViewPrefabHelper，包含实例释放契约 |
| AutoBindTestBase | 示例使用 ViewBase / WidgetViewBase |

旧 ViewLayer 的数值不能直接映射到 UILayer；迁移的场景和 Prefab 必须显式配置新层级与关闭策略。
