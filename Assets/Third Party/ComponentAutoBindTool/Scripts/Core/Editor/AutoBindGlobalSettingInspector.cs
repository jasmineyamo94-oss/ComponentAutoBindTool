using UnityEditor;
using UnityEngine;

namespace ComponentAutoBindTool.Editor
{
    [CustomEditor(typeof(AutoBindGlobalSetting))]
    public class AutoBindGlobalSettingInspector : UnityEditor.Editor
    {
        private SerializedProperty _namespace;
        private SerializedProperty _codePath;
        private SerializedProperty _useGlobalDefaultSavePath;

        private void OnEnable()
        {
            _namespace = serializedObject.FindProperty("_namespace");
            _codePath = serializedObject.FindProperty("_codePath");
            _useGlobalDefaultSavePath = serializedObject.FindProperty("_useGlobalDefaultSavePath");
        }

        // public override void OnInspectorGUI()
        // {
        //     _namespace.stringValue = EditorGUILayout.TextField(new GUIContent("默认命名空间"), _namespace.stringValue);
        //
        //     EditorGUILayout.LabelField("默认代码保存路径：");
        //     
        //     EditorGUILayout.LabelField("默认代码保存路径：");
        //     EditorGUILayout.LabelField(_codePath.stringValue);
        //     if (GUILayout.Button("选择路径", GUILayout.Width(140f)))
        //     {
        //         _codePath.stringValue = EditorUtility.OpenFolderPanel("选择代码保存路径", Application.dataPath, "");
        //     }
        //
        //     serializedObject.ApplyModifiedProperties();
        //
        // }
        
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            // 默认命名空间输入框
            _namespace.stringValue = EditorGUILayout.TextField("默认命名空间", _namespace.stringValue);

            // 布尔值复选框
            bool useGlobalPath = _useGlobalDefaultSavePath.boolValue;
            useGlobalPath = EditorGUILayout.Toggle("跟随目标脚本的路径", useGlobalPath);
            _useGlobalDefaultSavePath.boolValue = useGlobalPath;

            // 根据布尔值动态显示路径选择
            EditorGUILayout.LabelField("默认代码保存路径：");
            EditorGUILayout.LabelField(_useGlobalDefaultSavePath.boolValue
                ? string.Empty
                : _codePath.stringValue);
            if (GUILayout.Button("选择路径", GUILayout.Width(140f)))
            {
                var folderPath = EditorUtility.OpenFolderPanel("选择代码保存路径", Application.dataPath, "");
                if (!string.IsNullOrEmpty(folderPath) && AutoBindPathUtility.IsPathUnderAssets(folderPath))
                {
                    _codePath.stringValue = AutoBindPathUtility.ToAssetRelativePath(folderPath);
                }
                else if (!string.IsNullOrEmpty(folderPath))
                {
                    EditorUtility.DisplayDialog("路径无效", "请选择当前项目 Assets 目录内的文件夹。", "确定");
                }
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}
