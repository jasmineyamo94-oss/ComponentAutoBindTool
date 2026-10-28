# 实验台暴露的框架问题

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
