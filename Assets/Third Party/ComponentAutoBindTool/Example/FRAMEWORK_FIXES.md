# 实验台暴露的框架问题

## 本次窗口增强的修复记录（2026-10-29）

以下记录复现条件、修复行为与回归入口，不代表完整测试套件已通过。执行范围和最终结果统一记录在 [窗口增强验证报告](../../../../Validation/ViewEnhancements/README.md)；下方已有历史验证结果保持原样。

### 窗口关闭、销毁中的异常与重入

- 复现：直接销毁活动窗口；或在 Pause、Close、Destroy、取消回调及资源 Dispose 中抛错或再次请求关闭／销毁。
- 根因：窗口 Presenter 原销毁入口未先结束有效会话，关闭入口未先暂停；清理链中任一异常会中断后续节点。WindowEntry 的关闭操作位于释放 Scope 的保护范围外，也可能留下未释放实例。
- 行为：Close 先暂停运行子树，再关闭子级与自身；Destroy 先结束会话。重复调用不重复钩子，重入销毁等待当前阶段结束；异常继续收集，完成其余 Presenter、Scope、GameObject 和等待者清理后再传播。回调解绑子控件时不会跳过兄弟节点。
- 兼容：现有公开方法、重复 OnOpen 的参数刷新和 Tick 约定保留；已暂停／隐藏节点不重复 Pause，从未打开不补 Close。单异常保留原异常，多异常聚合传播。
- 测试：`WindowPresenterLifecycleTests.DestroyActiveWindowPausesClosesThenDestroysAndReleasesResourcesOnce`、`CallbackFailuresStillPauseCloseDestroyAndReleaseEveryWidget`、`ReentrantCloseAndDestroyWaitForCurrentPhaseAndRunOnce`；集成覆盖为 `ViewCoreEnhancementTests.DisposeContinuesAfterPresenterFailureAndWakesAllWaiters`。

### 高层窗口存在时，Normal 队列提前放行

- 复现：Top 或 System 窗口仍打开，Normal 层为空，此时 Tick 消费排队请求。
- 根因：原队列只检查 Normal 层是否为空，没有检查高层窗口。
- 行为：默认开启 `BlockQueueWhenHigherLayersOpen`，Top、System 任一层有打开窗口时继续等待；放行后每次 Tick 最多消费一项。优先级高者先出队，同优先级保持入队顺序，显式清空队列会撤销剩余请求。
- 兼容：原 `ShowView(..., immediately: false)` 继续入队；关闭该开关可恢复原来的高层放行规则，HUD 不参与此门禁。
- 测试：`ViewCoreEnhancementTests.HigherLayerBlocksTheExistingNormalQueueUntilItCloses`、`PriorityIsStableAndClearingQueuePreventsPendingRequestsFromOpening`。

### 动画完成回调失效与延迟销毁期间重开

- 复现：退场尚未完成就重开 Singleton，之后旧退场回调才返回；或同一 Tick 内先销毁一个到期窗口，其 Destroy 回调重开另一个已进入到期列表的窗口。
- 根因：异步完成回调与到期 ID 列表只反映旧状态，不能证明当前实例仍处于对应的退场或待销毁阶段。
- 行为：用过渡版本和当前状态拒绝过期、重复完成回调；过渡期间关闭输入，当前入场完成后恢复。延迟销毁同时等待计时到期与退场完成，并在实际销毁前复查计时记录、状态和过渡标记；重开取消旧计时和旧退场效果。
- 兼容：逻辑关闭和关闭等待者立即完成；配置动画时物理释放等待退场。无动画或同步完成动画不增加一帧延迟；重复排序刷新不重播正在执行的入场。
- 测试：`ViewCoreEnhancementTests.ReopeningDuringExitIgnoresStaleCompletionAndLocksInputOnlyDuringTransition`、`DelayedDestructionWaitsForBothTimerAndExitCompletion`、`ReopeningAnotherExpiredWindowCancelsItsPendingDestruction`、`ThrowingExitStillReleasesInstanceAndCompletesCloseWaiters`。

### 创建／恢复回调改变管理器状态后仍继续打开

- 复现：Presenter.OnCreate 释放所属 ViewManager；或 OnResume 立即关闭自身，外层打开流程随后仍发布 OnViewOpened。
- 根因：调用用户钩子之后没有重新核对管理器是否已释放、实例是否仍登记及仍在打开列表中。
- 行为：创建回调返回后若管理器已释放，清理刚准备的 Presenter、Scope 与 GameObject，不再登记实例；OnOpen 和层级恢复之后重新核对登记与打开列表，已经关闭的窗口不发布过期打开事件。
- 兼容：正常创建和打开事件的调用顺序不变；只阻止回调已经使当前打开操作失效时的后续步骤。
- 测试：`ViewCoreEnhancementTests.DisposingManagerDuringCreateReleasesThePreparedInstance`、`ClosingFromResumeDoesNotPublishAStaleOpenedEvent`；Presenter 层另有 `WindowPresenterLifecycleTests.DestroyDuringOnOpenDoesNotReopenChildWidgets`、`DestroyDuringPreviousSessionCleanupPreventsRefreshFromReopeningWindow`。

## 动态解绑跳过 Widget 关闭钩子

- 复现：打开 Widget 实验后执行 `DetachGroup`；原实现直接 Destroy，活动 Group 与两层叶节点没有 Pause、Close。
- 根因：`UnbindWidget` 调用的 Widget 销毁入口只销毁子树和清理资源袋；原 Close 本身也没有先暂停有效运行子树。
- 修复：Widget 基类关闭前先暂停有效子树，销毁前关闭未结束会话；增加重复调用及重入保护。回调、取消令牌或资源释放异常均继续其余清理尝试，最后传播错误。
- 兼容：公开方法和宿主 GameObject 所有权不变；业务在动态解绑时现在会收到缺失的 Pause/Close。已关闭、隐藏或暂停状态不会重复收到钩子，重复开窗刷新参数的约定不变。
- 验证：`WidgetLifecycleTests` 经真实 VContainer 绑定及窗口 UnbindWidget 验证嵌套回调顺序、资源袋、重复销毁与异常清理；原有隐藏恢复回归保留。
- 结果：2026-10-29 的 13 项生命周期用例全部通过；实际示例动态解绑、关闭后重开和 Scope 重置连续三轮通过。修复前失败 XML 与完整 129 项回归见 [边界修复测试报告](../../../../Validation/BoundaryFixes/README.md)。

## 路径解释与元数据更新边界

- 复现：Unix 下旧 `/Third Party/...` 默认路径被当成系统绝对路径；或 Inspector 更换/清空目标脚本后摘要仍是旧值。旧 Sync 在缺配置时还会先改写部分候选字段。
- 根因：旧格式兼容只限 Windows，配置解释和绝对边界检查混用；Inspector 没有调用显式编辑的同步入口，Sync 又没有先完整计算。
- 修复：配置兼容跨平台并与绝对路径校验分开；Inspector 仅显式选择时原子同步候选，重绘只读展示，支持一次 Undo。
- 兼容：按用户确认的广泛规则，配置 `/tmp/Output` 会解释到 Assets 内；文件夹选择器仍按真实绝对目录校验。默认输出改为宿主同级，生成文件仍必须同程序集。
- 验证：`AutoBindPathTests` 覆盖 Windows/Unix 词法规则与二次解析；`AutoBindInspectorMetadataTests` 覆盖清空、失败不部分写入、Undo/Redo 与只读摘要。真实 macOS/Linux Unity 执行结果单独记录，不用规则测试代替。
- 结果：Windows Unity 下 54 项路径用例与 10 项元数据用例全部通过；macOS/Linux Unity 尚未执行。

## Widget 隐藏后无法恢复 Tick

- 复现：打开 Widget → SetVisible(false) → SetVisible(true)。原实现隐藏时将 m_IsResumed 设为 false；显示时又以此值判断能否 Resume，因此无法恢复。
- 另一个复现：父窗已暂停，隐藏/显示局部 Widget；旧实现混用局部可见性与父级运行状态，重复 Pause 或产生错误恢复。
- 修复：分别记录父级恢复状态与局部 IsVisible，有效状态为“打开会话有效 && 父级运行 && 局部可见”。只有有效状态改变时调用一次 Pause/Resume，并传递给所有嵌套 Widget。窗口向隐藏 Widget 也传递父状态。
- 兼容：公开 API 不变。原来依赖重复 Pause/Resume 的代码会改为每次实际状态转换仅回调一次。
- 失败证据：WidgetLifecycleTests 两项测试在修复前均失败，见 Validation/FrameworkLab/widget-before.xml。
- 回归：LocalHideShowResumesTickExactlyOnce、LocalVisibilityCannotResumePausedParent；NestedWidgetVisibilityAndSharedScopeThreeRounds 验证真实窗口下两层嵌套、独立数据及父级暂停。

## 延迟销毁遍历期间修改字典

- 复现：打开 DestroyDelayed 窗口并关闭，下一帧计时器减值时更新正在 foreach 遍历的 Dictionary。
- 根因：Unity 6000.3.20f1 的 Mono Dictionary 在修改已有值时也会改变版本，枚举器抛出 InvalidOperationException。同版本 Mono 的最小复现已验证。
- 修复：复用 List 缓存待处理 ID，在独立 ID 集合上更新字典；到期销毁仍在计时更新后执行。没有改变延迟时长、取消规则或公开 API。
- 回归：DelayedDestroyCancellationAndExpiryThreeRounds，验证重开取消旧计时器、到期重建、Scope 释放，连续执行三轮。

## 实验资产的实例覆盖记录

- 复现：编辑器实验直接修改 Prefab 实例子对象名称，生成后重新打开场景；未记录 GameObject 的覆盖会丢失名称。
- 修复：在修改名称后调用 PrefabUtility.RecordPrefabInstancePropertyModifications(child.gameObject)。绑定组件的覆盖仍由正式流水线记录。
- 影响：仅示例实验资产，不修改框架 API 或源 Prefab。
- 回归：PipelineLabTests 的 InstanceOverride 在生成后重开场景检查节点名与绑定表，并检查源 Prefab 字节不变。
