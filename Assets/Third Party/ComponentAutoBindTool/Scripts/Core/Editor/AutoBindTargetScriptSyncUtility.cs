using System;
using UnityEditor;
using UnityEngine;

namespace ComponentAutoBindTool.Editor
{
    internal readonly struct AutoBindTargetScriptMetadata
    {
        public string Namespace { get; }
        public string ClassName { get; }
        public string CodePath { get; }

        public AutoBindTargetScriptMetadata(string namespaceValue, string className, string codePath)
        {
            Namespace = namespaceValue;
            ClassName = className;
            CodePath = codePath;
        }
    }

    internal static class AutoBindTargetScriptSyncUtility
    {
        internal static void ApplyMetadata(AutoBindBuildInput input, SerializedObject target)
        {
            target.FindProperty("m_Namespace").stringValue = input.HostType.Namespace ?? string.Empty;
            target.FindProperty("m_ClassName").stringValue = input.HostType.Name;
            target.FindProperty("m_CodePath").stringValue = input.CodePath;
        }

        public static bool SyncFromTargetScript(SerializedProperty targetScriptProperty, SerializedProperty namespaceProperty,
            SerializedProperty classNameProperty, SerializedProperty codePathProperty, AutoBindGlobalSetting setting)
        {
            bool succeeded = SyncFromTargetScript(targetScriptProperty, namespaceProperty, classNameProperty,
                codePathProperty, setting, out string error);
            if (!succeeded) Debug.LogError(error);
            return succeeded;
        }

        internal static bool SyncFromTargetScript(SerializedProperty targetScriptProperty, SerializedProperty namespaceProperty,
            SerializedProperty classNameProperty, SerializedProperty codePathProperty, AutoBindGlobalSetting setting, out string error)
        {
            error = null;
            if (targetScriptProperty == null || namespaceProperty == null || classNameProperty == null || codePathProperty == null)
            {
                error = "无法读取 AutoBind 元数据序列化字段。";
                return false;
            }

            var reference = targetScriptProperty.objectReferenceValue;
            if (reference != null && !(reference is MonoBehaviour))
            {
                error = "目标脚本不是有效的 MonoBehaviour。";
                return false;
            }
            if (!TryResolveMetadata(reference as MonoBehaviour, setting, out var metadata, out error)) return false;

            // 全部候选解析成功后才更新同一 SerializedObject，由调用方与引用变更一起提交。
            namespaceProperty.stringValue = metadata.Namespace;
            classNameProperty.stringValue = metadata.ClassName;
            codePathProperty.stringValue = metadata.CodePath;
            return true;
        }

        internal static bool TryResolveMetadata(MonoBehaviour targetScript, AutoBindGlobalSetting setting,
            out AutoBindTargetScriptMetadata metadata, out string error)
        {
            metadata = new AutoBindTargetScriptMetadata(string.Empty, string.Empty, string.Empty);
            error = null;
            if (targetScript == null) return true;
            if (setting == null)
            {
                error = "缺少 AutoBind 全局配置，无法解析目标脚本的输出路径。";
                return false;
            }
            try
            {
                MonoScript script = MonoScript.FromMonoBehaviour(targetScript);
                string scriptPath = script != null ? AssetDatabase.GetAssetPath(script) : string.Empty;
                if (string.IsNullOrEmpty(scriptPath))
                {
                    error = "无法解析目标脚本的资源路径。";
                    return false;
                }
                string resolvedPath = AutoBindPathUtility.GetResolvedCodePath(setting, scriptPath);
                if (string.IsNullOrEmpty(resolvedPath) || !AutoBindPathUtility.IsPathUnderAssets(resolvedPath))
                {
                    error = "代码保存路径必须是 Assets 内的有效目录：" + resolvedPath;
                    return false;
                }
                Type type = targetScript.GetType();
                metadata = new AutoBindTargetScriptMetadata(type.Namespace ?? string.Empty, type.Name,
                    AutoBindPathUtility.ToAssetRelativePath(resolvedPath));
                return true;
            }
            catch (Exception exception)
            {
                error = "无法解析 AutoBind 元数据：" + exception.Message;
                return false;
            }
        }
    }
}
