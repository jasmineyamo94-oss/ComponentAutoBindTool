using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using BindData = ComponentAutoBindTool.Core.ComponentAutoBindTool.BindData;

namespace ComponentAutoBindTool.Editor
{
    // 同一次扫描的输入，同时供校验、序列化提交和代码生成使用。
    internal sealed class AutoBindBuildInput
    {
        public Core.ComponentAutoBindTool Target { get; }
        public Type HostType { get; }
        public string ScriptPath { get; }
        public string CodePath { get; }
        public string OutputPath { get; }
        public IReadOnlyList<BindData> Bindings { get; }

        public AutoBindBuildInput(Core.ComponentAutoBindTool target, AutoBindGlobalSetting setting,
            IReadOnlyList<BindData> bindings)
        {
            Target = target;
            HostType = target.m_targetScript != null ? target.m_targetScript.GetType() : null;
            var script = target.m_targetScript != null ? MonoScript.FromMonoBehaviour(target.m_targetScript) : null;
            ScriptPath = script != null ? AssetDatabase.GetAssetPath(script) : string.Empty;
            CodePath = AutoBindPathUtility.GetInspectorCodePath(setting, ScriptPath);
            OutputPath = HostType == null || string.IsNullOrEmpty(CodePath) ? string.Empty :
                Path.Combine(AutoBindPathUtility.NormalizeCodePath(CodePath), HostType.Name + ".BindComponents.cs").Replace('\\', '/');
            Bindings = bindings;
        }
    }
}
