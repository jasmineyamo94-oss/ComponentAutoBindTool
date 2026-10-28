# 框架实验台验证记录

验证版本：Unity 6000.3.20f1。验证在项目副本中执行，使用实际 Unity 编译器、Test Runner、VContainer、Prefab 与场景资产。

## 结果

| 检查 | 结果 | 证据 |
|---|---|---|
| 完整回归套件 | 51 / 51 通过 | final-tests.xml |
| 最后一次故障注入属性恢复修订 | 13 / 13 编辑器实验通过 | final-editor.xml |
| Widget 修复前复现 | 2 / 2 按预期失败 | widget-before.xml |
| 正式资产重建、绑定生成、静态中文图集 | 通过 | verification.log |
| 实际程序集重载后恢复对象与 UI Toolkit 报告 | 通过 | domain-reload.txt |
| 重载后连续生成 | 内容及文件时间不变 | domain-reload.txt |
| 4 种横屏渲染 / Safe Area / 射线 / 点击 / 滚动 | 通过，8 张 PNG | Screenshots、verification.log |

51 项包括原有 31 项（原示例集成测试迁移到新窗口结构）、新增 2 项 Widget 状态回归、5 项运行实验集成、13 项编辑器实验。5 项运行实验分别连续运行并重置三轮。

延迟销毁测试在倒计时内重开后继续等待超过 3 秒，确认旧计时器已取消；再次关闭并等待到期，确认新实例被重建。Scope 创建/释放计数在每次重置后相等。

编辑器实验覆盖未知前缀、缺失组件、重复字段、非法名称、无效路径、嵌套边界与 TMP 跳过、改名直接生成、首次生成、重复生成、场景与 Prefab 保存、实例覆盖及提交失败回滚。实例覆盖实验会重新打开场景检查持久化后的名称和绑定表。故障注入完成后文件属性恢复到注入前的原值。

## 截图与交互边界

- 1280×720：参考横屏。
- 1920×1080：标准高清横屏。
- 2340×1080：宽屏，左右各模拟 80 像素 Safe Area。
- 1440×1080：横屏 4:3。

每种尺寸包含 widgets 与 observer 两张真实 Camera.Render 截图。截图检查了中文字形、布局、无遮挡导航、观察抽屉、滚动区域；捕获脚本检查所有可见 Button 至少 64 个参考像素、Reset 射线命中、PointerClick 重置以及 ScrollRect 滚动。

这是编辑器中的渲染与事件系统验证。没有将其作为真实手机上的帧率、内存、功耗或触摸硬件验证；真机性能未测试。

## 复现

完整测试（不加 -quit）：

    Unity.exe -batchmode -nographics -projectPath "<项目副本>" -runTests -testPlatform EditMode -testFilter ComponentAutoBindTool.Tests -testResults "<结果.xml>" -logFile "<日志.log>"

正式重建（字体预烘焙启用图形设备）：

    Unity.exe -batchmode -projectPath "<项目副本>" -executeMethod ComponentAutoBindTool.Example.Editor.AutoBindExampleSetup.Rebuild -quit -logFile "<日志.log>"

实际 Domain Reload 验证（由方法退出，不加 -quit）：

    Unity.exe -batchmode -projectPath "<项目副本>" -executeMethod ComponentAutoBindTool.Example.Editor.PipelineLabReloadValidation.Run -logFile "<日志.log>"

多分辨率渲染（由方法退出，不加 -quit，不加 -nographics）：

    Unity.exe -batchmode -projectPath "<项目副本>" -executeMethod ComponentAutoBindTool.Example.Editor.LabCapture.Run -logFile "<日志.log>"

截图写入副本的 Validation/FrameworkLab/Screenshots。验证工具不访问框架私有字典。原有 Example 场景、AutoBindTest 脚本与已有示例资产 GUID 比较未发现变化。
