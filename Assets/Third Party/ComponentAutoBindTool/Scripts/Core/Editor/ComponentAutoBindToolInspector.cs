using ComponentAutoBindTool.Core;
using UnityEditor;
using UnityEngine;

namespace ComponentAutoBindTool.Editor
{
    [CustomEditor(typeof(Core.ComponentAutoBindTool), true)]
    public class ComponentAutoBindToolInspector : UnityEditor.Editor
    {
        private Core.ComponentAutoBindTool _target;
        private AutoBindGlobalSetting _setting;
        private AutoBindKeyMapSetting _keyMapSetting;
        private SerializedProperty _bindDatas;
        private SerializedProperty _bindComs;
        private SerializedProperty _bindFieldNames;
        private SerializedProperty _targetScript;
        private bool _showBindings;
        private bool _showKeyMap;
        private string _lastReport;
        private string _metadataError;

        private void OnEnable()
        {
            _target = (Core.ComponentAutoBindTool)target;
            _bindDatas = serializedObject.FindProperty("BindDatas");
            _bindComs = serializedObject.FindProperty("m_BindComs");
            _bindFieldNames = serializedObject.FindProperty("_bindFieldNames");
            _targetScript = serializedObject.FindProperty("m_targetScript");
            LoadSettings();
        }

        private void LoadSettings()
        {
            _setting = AutoBindEditorAssetLocator.LoadSingletonAsset<AutoBindGlobalSetting>();
            _keyMapSetting = AutoBindEditorAssetLocator.LoadSingletonAsset<AutoBindKeyMapSetting>();
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(_targetScript, new GUIContent("脚本"));
            if (EditorGUI.EndChangeCheck()) ApplyTargetScriptChange(_setting, out _);
            if (!string.IsNullOrEmpty(_metadataError)) EditorGUILayout.HelpBox(_metadataError, MessageType.Error);

            // 摘要始终取实际脚本与当前配置；重绘不会把候选写回组件。
            if (AutoBindTargetScriptSyncUtility.TryResolveMetadata(_target.m_targetScript, _setting,
                out var metadata, out string previewError))
                AutoBindInspectorDrawer.DrawSettingsSection(metadata.Namespace, metadata.ClassName, metadata.CodePath);
            else if (_setting != null)
                EditorGUILayout.HelpBox(previewError, MessageType.Error);

            if (_setting == null || _keyMapSetting == null)
            {
                EditorGUILayout.HelpBox("缺少唯一的 AutoBind 全局配置或组件映射配置，请修复配置后重试。", MessageType.Error);
                if (GUILayout.Button("重新加载配置")) LoadSettings();
                return;
            }
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            {
                AutoBindInspectorDrawer.DrawActionSection(HandleAutoBindAndValidate, HandleGenerateCode, HandleRemoveAll);
            }
            AutoBindInspectorDrawer.DrawBindComponentFoldout(_bindDatas, ref _showBindings, DrawBindings);
            AutoBindInspectorDrawer.DrawKeyMapTip(_keyMapSetting, ref _showKeyMap);
            if (!string.IsNullOrEmpty(_lastReport)) EditorGUILayout.HelpBox(_lastReport, MessageType.None);
        }

        internal bool ApplyTargetScriptChange(AutoBindGlobalSetting setting, out string error)
        {
            if (!AutoBindTargetScriptSyncUtility.SyncFromTargetScript(_targetScript,
                serializedObject.FindProperty("_namespace"), serializedObject.FindProperty("_className"),
                serializedObject.FindProperty("_codePath"), setting, out error))
            {
                serializedObject.Update();
                _metadataError = "此次脚本选择未应用：" + error;
                return false;
            }
            serializedObject.ApplyModifiedProperties();
            _metadataError = null;
            return true;
        }

        private void DrawBindings()
        {
            EditorGUI.BeginChangeCheck();
            int index = AutoBindInspectorDrawer.DrawBindDataList(_bindDatas);
            if (index >= 0)
            {
                _bindDatas.DeleteArrayElementAtIndex(index);
                GUI.changed = true;
            }
            if (EditorGUI.EndChangeCheck())
            {
                AutoBindSerializedBindingUtility.SyncRuntimeBindingCaches(_bindDatas, _bindComs, _bindFieldNames);
                serializedObject.ApplyModifiedProperties();
            }
        }

        private void HandleRemoveAll()
        {
            _bindDatas.ClearArray();
            _bindComs.ClearArray();
            _bindFieldNames.ClearArray();
            serializedObject.ApplyModifiedProperties();
        }

        private void HandleAutoBindAndValidate() => RunPipeline(false);
        private void HandleGenerateCode() => RunPipeline(true);

        internal AutoBindPipelineReport RunPipeline(bool generate, bool showDialog = true,
            AutoBindGlobalSetting settingsOverride = null, AutoBindKeyMapSetting keyMapOverride = null)
        {
            serializedObject.ApplyModifiedProperties();
            AutoBindPipelineReport report = generate
                ? AutoBindBuildPipeline.GenerateCode(_target, settingsOverride ?? _setting, keyMapOverride ?? _keyMapSetting)
                : AutoBindBuildPipeline.RescanAndValidate(_target, settingsOverride ?? _setting, keyMapOverride ?? _keyMapSetting);
            serializedObject.Update();
            string title = report.Succeeded ? (generate ? "生成完成" : "校验通过") : "操作失败";
            _lastReport = title + "\n" + report.ValidationResult.BuildReport();
            if (generate && report.Succeeded)
                _lastReport += report.CodeFileUpdated ? "\n已更新代码；绑定引用已更新到内存，请按需保存场景或 Prefab。" : "\n代码内容未变化，已跳过写入。";
            if (report.Succeeded)
            {
                if (report.ValidationResult.WarningCount > 0) Debug.LogWarning(_lastReport, _target);
                else Debug.Log(_lastReport, _target);
            }
            else Debug.LogError(_lastReport, _target);
            if (showDialog && !Application.isBatchMode) EditorUtility.DisplayDialog(title, _lastReport, "确定");
            return report;
        }
    }
}
