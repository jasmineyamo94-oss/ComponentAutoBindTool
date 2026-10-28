using Third_Party.ComponentAutoBindTool.Scripts.Core;
using UnityEditor;
using UnityEngine;

namespace ComponentAutoBindTool.Editor
{
    [CustomEditor(typeof(Core.ComponentAutoBindTool), true)]
    public class ComponentAutoBindToolInspector : UnityEditor.Editor
    {
        private Core.ComponentAutoBindTool m_Target;
        private AutoBindGlobalSetting m_Setting;
        private AutoBindKeyMapSetting m_KeyMapSetting;
        private SerializedProperty m_BindDatas;
        private SerializedProperty m_BindComs;
        private SerializedProperty m_BindFieldNames;
        private SerializedProperty m_TargetScript;
        private bool m_ShowBindings;
        private bool m_ShowKeyMap;
        private string m_LastReport;

        private void OnEnable()
        {
            m_Target = (Core.ComponentAutoBindTool)target;
            m_BindDatas = serializedObject.FindProperty("BindDatas");
            m_BindComs = serializedObject.FindProperty("m_BindComs");
            m_BindFieldNames = serializedObject.FindProperty("m_BindFieldNames");
            m_TargetScript = serializedObject.FindProperty("m_targetScript");
            LoadSettings();
        }

        private void LoadSettings()
        {
            m_Setting = AutoBindEditorAssetLocator.LoadSingletonAsset<AutoBindGlobalSetting>();
            m_KeyMapSetting = AutoBindEditorAssetLocator.LoadSingletonAsset<AutoBindKeyMapSetting>();
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.PropertyField(m_TargetScript, new GUIContent("脚本"));
            serializedObject.ApplyModifiedProperties();
            if (m_Setting == null || m_KeyMapSetting == null)
            {
                EditorGUILayout.HelpBox("缺少唯一的 AutoBind 全局配置或组件映射配置，请修复配置后重试。", MessageType.Error);
                if (GUILayout.Button("重新加载配置")) LoadSettings();
                return;
            }
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            {
                AutoBindInspectorDrawer.DrawActionSection(HandleAutoBindAndValidate, HandleGenerateCode, HandleRemoveAll);
            }
            AutoBindInspectorDrawer.DrawSettingsSection(m_Target.Namespace, m_Target.ClassName, m_Target.CodePath);
            AutoBindInspectorDrawer.DrawBindComponentFoldout(m_BindDatas, ref m_ShowBindings, DrawBindings);
            AutoBindInspectorDrawer.DrawKeyMapTip(m_KeyMapSetting, ref m_ShowKeyMap);
            if (!string.IsNullOrEmpty(m_LastReport)) EditorGUILayout.HelpBox(m_LastReport, MessageType.None);
        }

        private void DrawBindings()
        {
            EditorGUI.BeginChangeCheck();
            int index = AutoBindInspectorDrawer.DrawBindDataList(m_BindDatas);
            if (index >= 0)
            {
                m_BindDatas.DeleteArrayElementAtIndex(index);
                GUI.changed = true;
            }
            if (EditorGUI.EndChangeCheck())
            {
                AutoBindSerializedBindingUtility.SyncRuntimeBindingCaches(m_BindDatas, m_BindComs, m_BindFieldNames);
                serializedObject.ApplyModifiedProperties();
            }
        }

        private void HandleRemoveAll()
        {
            m_BindDatas.ClearArray();
            m_BindComs.ClearArray();
            m_BindFieldNames.ClearArray();
            serializedObject.ApplyModifiedProperties();
        }

        private void HandleAutoBindAndValidate() => RunPipeline(false);
        private void HandleGenerateCode() => RunPipeline(true);

        internal AutoBindPipelineReport RunPipeline(bool generate, bool showDialog = true,
            AutoBindGlobalSetting settingsOverride = null, AutoBindKeyMapSetting keyMapOverride = null)
        {
            serializedObject.ApplyModifiedProperties();
            AutoBindPipelineReport report = generate
                ? AutoBindBuildPipeline.GenerateCode(m_Target, settingsOverride ?? m_Setting, keyMapOverride ?? m_KeyMapSetting)
                : AutoBindBuildPipeline.RescanAndValidate(m_Target, settingsOverride ?? m_Setting, keyMapOverride ?? m_KeyMapSetting);
            serializedObject.Update();
            string title = report.Succeeded ? (generate ? "生成完成" : "校验通过") : "操作失败";
            m_LastReport = title + "\n" + report.ValidationResult.BuildReport();
            if (generate && report.Succeeded)
                m_LastReport += report.CodeFileUpdated ? "\n已保存所属场景或 Prefab，并更新代码。" : "\n代码内容未变化，已跳过写入。";
            if (report.Succeeded)
            {
                if (report.ValidationResult.WarningCount > 0) Debug.LogWarning(m_LastReport, m_Target);
                else Debug.Log(m_LastReport, m_Target);
            }
            else Debug.LogError(m_LastReport, m_Target);
            if (showDialog && !Application.isBatchMode) EditorUtility.DisplayDialog(title, m_LastReport, "确定");
            return report;
        }
    }
}
