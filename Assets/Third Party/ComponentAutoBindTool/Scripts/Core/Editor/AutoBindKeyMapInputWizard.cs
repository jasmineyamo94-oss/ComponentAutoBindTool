#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using Third_Party.ComponentAutoBindTool.Scripts.Core;

namespace ComponentAutoBindTool.Editor
{
    public class AutoBindKeyMapInputWizard : ScriptableWizard
    {
        public string key;
        public string typeName;
        
        private string warningMessage;
        
        protected void OnGUI()
        {
            // 绘制 lowerStr 字段（可编辑）
            key = EditorGUILayout.TextField("缩写前缀:", key);
            
            // 绘制 typeName 字段（只读）
            EditorGUILayout.LabelField("类型名称:", typeName);
            
            // 如果有警告信息，显示帮助框
            if (!string.IsNullOrEmpty(warningMessage))
            {
                EditorGUILayout.HelpBox(warningMessage, MessageType.Warning);
            }

            // 如果需要，添加其他 GUI 控件，例如按钮
            if (GUILayout.Button("确定"))
            {
                OnWizardCreate();
            }
        }

        private void OnWizardCreate()
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(typeName))
            {
                warningMessage = "组件前缀和类型名称不能为空。";
                return;
            }

            AutoBindKeyMapSetting obj = AutoBindEditorAssetLocator.LoadSingletonAsset<AutoBindKeyMapSetting>();
            if (obj != null)
            {
                if (obj.TryGetComponentTypeName(key, out string existingTypeName))
                {
                    // 前缀一样，但是类型不一样
                    warningMessage = $"警告：键 '{key}' 的类型已存在，当前类型为 '{existingTypeName}', 新类型为 '{typeName}'。";
                    return;
                }

                var targetKey = "";
                foreach (var (strKey, value) in obj.GetAllComponentKeyMaps())
                {
                    if (value == typeName)
                    {
                        targetKey = strKey;
                        break;
                    }
                }
                if (!string.IsNullOrEmpty(targetKey))
                {
                    warningMessage = $"警告：'{typeName}' 的类型已存在，当前类型的 键 为 '{targetKey}'。"; // 显示警告信息
                    return;
                }
                
                warningMessage = null;

                obj.extraComponentKeyMap ??= new GenericDictionary<string, string>();
                obj.extraComponentKeyMap.Add(key, typeName);
                EditorUtility.SetDirty(obj);
                AssetDatabase.SaveAssets();
                // 关闭窗口
                Close();
            }
            else
            {
                Debug.LogError("未能加载指定的 ScriptableObject。");
            }
        }
    }
}

#endif
