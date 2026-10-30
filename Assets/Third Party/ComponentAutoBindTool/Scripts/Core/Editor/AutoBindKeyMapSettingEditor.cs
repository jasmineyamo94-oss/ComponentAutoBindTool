using System;
using System.Collections.Generic;
using ComponentAutoBindTool.Core;
using UnityEditor;
using UnityEngine;

#if UNITY_EDITOR
namespace ComponentAutoBindTool.Editor
{
    [CustomEditor(typeof(AutoBindKeyMapSetting))]
    public class AutoBindKeyMapSettingEditor : UnityEditor.Editor
    {
        private SerializedProperty _extraComponentKeyMapProperty;
        private bool _showDefaultKeyMap = true; // 控制折叠状态
        private static HashSet<string> s_ComponentNames;

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
            DrawInvalidMappings();
        }

        private void DrawInvalidMappings()
        {
            serializedObject.Update();
            var list = _extraComponentKeyMapProperty.FindPropertyRelative("list");
            if (list == null) return;
            var invalidIndices = new List<int>();
            for (int i = 0; i < list.arraySize; i++)
            {
                var item = list.GetArrayElementAtIndex(i);
                string typeName = item.FindPropertyRelative("Value").stringValue;
                if (!IsComponentTypeName(typeName))
                {
                    invalidIndices.Add(i);
                    string key = item.FindPropertyRelative("Key").stringValue;
                    EditorGUILayout.HelpBox($"映射 {key} 的组件类型无法解析：{typeName}", MessageType.Warning);
                }
            }

            using (new EditorGUI.DisabledScope(invalidIndices.Count == 0 || EditorApplication.isCompiling || EditorApplication.isPlayingOrWillChangePlaymode))
            {
                if (GUILayout.Button("清理失效映射"))
                {
                    RemoveInvalidMappings(serializedObject);
                }
            }
        }

        internal static int RemoveInvalidMappings(SerializedObject settings)
        {
            settings.Update();
            var list = settings.FindProperty("extraComponentKeyMap").FindPropertyRelative("list");
            if (list == null) return 0;
            int count = 0;
            for (int i = list.arraySize - 1; i >= 0; i--)
            {
                var value = list.GetArrayElementAtIndex(i).FindPropertyRelative("Value").stringValue;
                if (IsComponentTypeName(value)) continue;
                list.DeleteArrayElementAtIndex(i);
                count++;
            }
            if (count > 0) settings.ApplyModifiedProperties();
            return count;
        }

        internal static bool IsComponentTypeName(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return false;
            if (s_ComponentNames == null)
            {
                s_ComponentNames = new HashSet<string>(StringComparer.Ordinal)
                {
                    typeof(Component).Name,
                    typeof(Component).FullName
                };
                foreach (var type in TypeCache.GetTypesDerivedFrom<Component>())
                {
                    s_ComponentNames.Add(type.Name);
                    if (type.FullName != null) s_ComponentNames.Add(type.FullName);
                }
            }
            return s_ComponentNames.Contains(typeName);
        }
    }
}
#endif
