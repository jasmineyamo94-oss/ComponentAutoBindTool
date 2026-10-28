# ComponentAutoBindTool 验证记录

验证日期：2026-10-28。Unity 版本：6000.3.20f1。
在当前项目 Assets、Packages 和 ProjectSettings 的独立副本中执行，避免干扰已打开的编辑器。

## 结果

- NUnit：31 项通过，0 失败，0 跳过，详见 editmode-results.xml。
- 测试套件中的 PlayMode 集成：实际进入运行模式，验证窗口和嵌套 Widget 注入、数据更新、
  缓存关闭与重开、立即销毁时 Presenter/Widget 清理、重新创建以及关闭全部窗口。
- 两次独立启动 Unity 的生成编译验证：4 种宿主全部通过。
  第一阶段生成代码，第二阶段编译后调用 IAutoBindHost，确认绑定引用正确且重复调用复用 UIView。
- 示例窗口与 Widget 的 Inspector 再次生成：2 项通过。绑定状态、代码及文件修改时间均不变化。
- 旧框架归档：11 份源码及其 .meta，共 22 个文件，与迁移前工作区备份 SHA256 全部一致。
- Assets 中没有旧 ViewCore 命名空间、ViewLifetimeScope、AutoBindTestBase 或 ViewBundle 的 C# 引用。
- 插件和接入文档的 git diff --check 通过。

compiled-hosts-and-example.txt 保存最终 Unity 批处理结果的关键原始日志行。
完整临时工程与日志保留在本机 E:/CodexValidation/ComponentAutoBindTool-20261028，
不纳入本仓库的交付文件。

## 覆盖范围

| 行为 | 验证 |
| --- | --- |
| 节点改名直接生成、元数据与三份绑定列表同步 | 自动化通过 |
| 未知前缀、缺失组件、非法/重复字段 | 失败且组件、场景及代码不变 |
| 多 partial 成员、显式接口、多个字段声明、命名空间别名 | 冲突拒绝提交；合法重载可生成 |
| 最近匹配的 sealed / 隐藏基类方法 | 拒绝提交 |
| 路径边界、旧路径格式、输出程序集 | 自动化通过 |
| 保存前失败、保存后代码提交失败 | 恢复组件与文件，保留既有未保存场景编辑 |
| 重扫 Undo；重复生成的内容与修改时间 | 自动化通过 |
| 场景保存重新加载 | 运行时字段名绑定正确 |
| 直接 Prefab 资产与 Prefab 编辑模式 | 保存与重新加载后绑定正确 |
| 场景 Prefab 实例 | 保存场景覆盖，原 Prefab 文件不变 |
| 未生成的 Widget | 可挂载，getter 返回 null 并报告缺少生成 |
| Widget 绑定失败后修复 | 可重试，成功后复用相同 UIView |
| 缺失字段名 | 返回 null，不回退同类型组件；纯索引重载可用 |
| 普通、基类显式接口、protected internal 虚方法、public 虚方法宿主 | 实际生成、编译和接口分派通过 |

## 复现

普通测试入口及两阶段生成编译验证方式见插件 MIGRATION.md。
不要将验证副本中的 Assets/AATest/CompilationGenerated 复制回主项目：
这些文件用于跨编译验证，常规测试会在各自临时目录生成和清理同一组宿主的代码。

回滚覆盖可捕获的提交异常；未实现进程崩溃恢复。不同平台的文件系统、版本控制锁定策略及
自定义 AssetPostprocessor 行为未进行矩阵测试。
