# UI View Template

在 Hierarchy 右键选择 **UI (Canvas) → UI View Template**。输入不含 `View` 后缀的英文基础名，例如 `Inventory`；文件名按固定规则派生，只能预览和复制。

“使用 Args 传递数据”默认勾选：生成专属参数脚本。取消勾选时不生成该文件，Presenter 与路由统一使用内置 EmptyViewArgs；预览显示“不生成”，选择在创建期间固定并跨脚本重载保留。

| 产物 | 固定名称 |
|---|---|
| 宿主 | InventoryView.cs |
| Presenter | InventoryPresenter.cs |
| 参数（可选） | InventoryArgs.cs；未勾选时不生成 |
| 路由 | InventoryRoutes.cs |
| 绑定代码 | InventoryView.BindComponents.cs |
| Prefab | InventoryView.prefab |

## 输出设置

主窗口的“路径设置”打开独立窗口，填写或选择两个 Assets 内的根目录，点击“保存设置”。默认脚本目录为 `Assets/AAGame/Scripts/UI`，Prefab 目录为 `Assets/AAGame/UI/Prefabs`。四份或五份脚本放在脚本根目录下的 `Inventory` 子目录，Prefab 直接放在 Prefab 根目录。

设置保存在 `ProjectSettings/AutoBindViewTemplateSettings.asset`，可加入版本管理；首次未保存设置时沿用代码中的默认值。全局 AutoBind 配置保持原状，新脚本沿用其命名空间。脚本与绑定文件固定同目录、同程序集。目标程序集必须支持宿主在 Editor 中挂载，并直接引用 Core、ViewCore、VContainer。

## 创建过程

1. 检查名称、目录、类型、文件冲突以及唯一的 AutoBind 全局配置和组件映射。
2. 生成可独立编译的宿主骨架、路由和可选参数，等待第一轮脚本编译。
3. 在临时预览场景挂载宿主，通过正式 AutoBind 流水线生成绑定代码，同时补齐宿主注册与 Presenter，等待第二轮编译。
4. 校验最终绑定，保存新 Prefab，在最初选中的 Canvas/UI 节点下创建连接该 Prefab 的实例；没有合适父节点时在原场景创建标准 Canvas 和必要的 EventSystem。

模板包含空白 View 根节点及铺满的 Content，不添加背景、标题或业务按钮。根节点自动具备 RectTransform、Canvas、GraphicRaycaster、ComponentAutoBindTool 与 View，AutoBind 目标引用指向新 View。默认使用 ViewBase 的 Singleton、NonDestroy 等策略。

```csharp
// 勾选 Args：
manager.ShowView(InventoryRoutes.Main, new InventoryArgs());
// 未勾选 Args：
manager.ShowView(InventoryRoutes.Main);
```

项目仍需通过自己的 Prefab helper/资源后端提供 `InventoryView` 资源键；工具不修改 LifetimeScope 或资源目录注册。

## 重试、清理与保存

- 主窗口显示当前阶段，创建中禁止重复提交。任务在当前 Editor 会话的 Domain Reload 间恢复，关闭窗口也不会停止任务；不承诺退出 Unity 后续建。重新打开菜单可查看任务。
- 编译失败时显示具体错误；修复后点击“重试”。已加载完整类型时直接继续，不依赖一次可能不会发生的编译重载。
- 不覆盖已有文件或同名类型。创建中出现外部同名文件、原父节点被删除、原场景被关闭时停止任务。
- “取消并清理本次产物”只清理任务登记且内容、元数据未变化的文件。若检测到任何产物被修改，保留整个关联文件组，避免误删用户编辑；外部文件始终保留。
- 成功后可点击“清除并重新创建”，确认清单后删除最近一次成功生成的脚本、Prefab、对应 `.meta` 和原场景实例。保留原名称、输出路径、命名空间与 Args 选项，等待脚本重载后，可切换 Args 再点击“创建”。不会自动创建第二次。
- 清除只用于尚未开始编辑的模板。任一文件、元数据、Prefab 或实例层级/组件被修改，原实例已删除/移走，或已加载场景中存在其他同类型实例时，整体停止清除。后续失败或取消的任务不会覆盖前一次成功回执；退出 Unity 后不保留清除记录。
- 清除保留 Canvas、EventSystem、目录及其他对象，不保存场景，也不支持 Undo。操作前在 `Library/AutoBindViewTemplate/Cleared` 备份登记文件；删除中途失败会尝试恢复缺失文件。“打开清除备份”可定位备份目录，供手动恢复文件。如果其他源码引用了已删除类型，修复相关引用后再点“重试”；清除编译失败期间不能开始新任务。
- 只保存本次新建 Prefab，场景保持未保存状态，包括未命名场景。Undo/Redo 作用于场景实例和本次补建的 Canvas/EventSystem，磁盘脚本和 Prefab 保留。
- 首版在普通编辑场景使用；Prefab Stage、Play Mode、编译/导入期间禁止开始创建。返回普通场景后再处理重试或清理。

工具全部位于 Editor 程序集；运行时框架接口没有变化。绑定文件仍由 AutoBindBuildPipeline 产生，后续调整 UI 层级后继续使用原 Inspector 重扫/生成工作流。

## 按位置删除已有模板

窗口重新打开、Unity 重启或原成功回执失效后，可在主窗口输入原基础名，点击“按位置删除已有模板…”。此入口不要求生成回执、宿主类型可加载或 AutoBind 全局配置可用。

独立删除窗口显示只读基础名，可输入或选择实际脚本目录、`基础名View.prefab` 文件；默认带入主窗口的输出位置，修改只作用于此次删除，不写项目共享路径设置。六项只读预览列出完整磁盘路径和文件/元数据存在状态，提供 Project 定位；缺失项跳过，不扫描整个项目。

1. 核对清单后点击“删除…”；第一次弹窗列出实际文件、`.meta` 和可移除的空叶目录。
2. 点击“继续到危险操作警告”后，第二次弹窗提示已编辑内容也会删除、无法通过 Undo 恢复，以及场景和源码引用失效的风险。
3. 只有明确点击“仍然删除”才开始备份和删除。两次弹窗默认取消，Enter、数字键盘 Enter、Escape 和关闭窗口均取消。

删除范围固定为该基础名的 View、Presenter、Args、Routes、BindComponents 脚本，以及指定 Prefab 和各自 `.meta`。Args 按磁盘存在情况处理，与主窗口 Args 勾选项无关。额外业务文件、子目录和改名文件保留；只有同名脚本叶目录在清单中标明且删除后为空时，才非递归移除该目录及 `.meta`。共享目录和 Assets 根目录不会删除。

**此入口只删除磁盘资产，不移除任何场景实例，也不主动清理场景 Undo 或保存场景。** 原实例或其他引用可能留下 Missing Script、Missing Prefab；请自行处理。删除前在 `Library/AutoBindViewTemplate/Cleared` 备份文件及元数据，提供“打开删除备份”。确认期间文件或元数据变化会停止操作，需刷新清单并重新确认。删除中途失败会尝试恢复缺失文件和原 GUID。

编译、导入、Play Mode、Prefab Stage 或其他创建/清除任务未结束时禁用；失败创建任务须先取消。已有编译错误不阻止此入口定位、删除资产。删除已完成但编译失败时分别显示结果与诊断，不自动恢复文件；修复后点击“重新检测编译”，该按钮只检查或重新编译，不重复删除。

主窗口输入保持不变，不自动重建。被删除产物对应的成功回执会失效。原“清除并重新创建”仍要求产物及实例未被修改，并会清理原场景实例；两个入口的范围不同。
