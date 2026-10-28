using Third_Party.ComponentAutoBindTool.Scripts.Core;
using UnityEditor;

#if UNITY_EDITOR
namespace ComponentAutoBindTool.Editor
{
    [CustomEditor(typeof(AutoBindKeyMapSetting))]
    public class AutoBindKeyMapSettingEditor : UnityEditor.Editor
    {
        private SerializedProperty _extraComponentKeyMapProperty;
        private bool _showDefaultKeyMap = true; // 控制折叠状态

        private void OnEnable()
        {
            _extraComponentKeyMapProperty = serializedObject.FindProperty("extraComponentKeyMap");
        }

        public override void OnInspectorGUI()
        {
            var autoBindKeyMapSetting = (AutoBindKeyMapSetting)target;
            
            serializedObject.Update();

            // 显示可折叠的字典
            _showDefaultKeyMap = EditorGUILayout.Foldout(_showDefaultKeyMap, 
                "Default Component Key Map", true);
            if (_showDefaultKeyMap)
            {
                EditorGUI.indentLevel++; // 增加缩进
                foreach (var keyValuePair in autoBindKeyMapSetting.DefaultComponentKeyMap)
                {                    
                    EditorGUILayout.LabelField($"组件缩写: {keyValuePair.Key}", 
                        $"组件名称: {keyValuePair.Value}");
                }
                EditorGUI.indentLevel--; // 恢复缩进
            }

            // 显示可编辑字典
            EditorGUILayout.PropertyField(_extraComponentKeyMapProperty, true);

            serializedObject.ApplyModifiedProperties();
        }
    }
}
#endif