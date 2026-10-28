using UnityEditor;
using UnityEngine;

namespace ComponentAutoBindTool.Editor
{
    [CustomEditor(typeof(AutoBindGlobalSetting))]
    public class AutoBindGlobalSettingInspector : UnityEditor.Editor
    {
        private SerializedProperty m_Namespace;
        private SerializedProperty m_CodePath;
        private SerializedProperty m_UseGlobalDefaultSavePath;

        private void OnEnable()
        {
            m_Namespace = serializedObject.FindProperty("m_Namespace");
            m_CodePath = serializedObject.FindProperty("m_CodePath");
            m_UseGlobalDefaultSavePath = serializedObject.FindProperty("m_UseGlobalDefaultSavePath");
        }

        // public override void OnInspectorGUI()
        // {
        //     m_Namespace.stringValue = EditorGUILayout.TextField(new GUIContent("默认命名空间"), m_Namespace.stringValue);
        //
        //     EditorGUILayout.LabelField("默认代码保存路径：");
        //     
        //     EditorGUILayout.LabelField("默认代码保存路径：");
        //     EditorGUILayout.LabelField(m_CodePath.stringValue);
        //     if (GUILayout.Button("选择路径", GUILayout.Width(140f)))
        //     {
        //         m_CodePath.stringValue = EditorUtility.OpenFolderPanel("选择代码保存路径", Application.dataPath, "");
        //     }
        //
        //     serializedObject.ApplyModifiedProperties();
        //
        // }
        
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            // 默认命名空间输入框
            m_Namespace.stringValue = EditorGUILayout.TextField("默认命名空间", m_Namespace.stringValue);

            // 布尔值复选框
            bool useGlobalPath = m_UseGlobalDefaultSavePath.boolValue;
            useGlobalPath = EditorGUILayout.Toggle("跟随目标脚本的路径", useGlobalPath);
            m_UseGlobalDefaultSavePath.boolValue = useGlobalPath;

            // 根据布尔值动态显示路径选择
            EditorGUILayout.LabelField("默认代码保存路径：");
            EditorGUILayout.LabelField(m_UseGlobalDefaultSavePath.boolValue
                ? string.Empty
                : m_CodePath.stringValue);
            if (GUILayout.Button("选择路径", GUILayout.Width(140f)))
            {
                var folderPath = EditorUtility.OpenFolderPanel("选择代码保存路径", Application.dataPath, "");
                if (!string.IsNullOrEmpty(folderPath) && AutoBindPathUtility.IsPathUnderAssets(folderPath))
                {
                    // Debug.Log($"dataPath: {Application.dataPath}");
                    m_CodePath.stringValue = AutoBindPathUtility.ToAssetRelativePath(folderPath);
                }
                // Debug.Log($"m_CodePath: {m_CodePath.stringValue}");
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}
