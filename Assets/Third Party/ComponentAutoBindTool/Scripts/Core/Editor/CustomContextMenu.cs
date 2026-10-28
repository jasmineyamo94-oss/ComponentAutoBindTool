#if UNITY_EDITOR
using ComponentAutoBindTool.Core;
using UnityEditor;
using UnityEngine;

namespace ComponentAutoBindTool.Editor
{
    public class CustomContextMenu
    {
        [MenuItem("CONTEXT/MonoBehaviour/Add AutoBindComponentTool", false, 0)]
        private static void AddAutoBindComponentTool(MenuCommand command)
        {
            // 获取右键点击的对象
            var mono = command.context as MonoBehaviour;
            if (mono != null)
            {
                var selectedObject = mono.gameObject;

                // 检查是否已经挂载了 MyScript
                if (selectedObject != null)
                    if (selectedObject.GetComponent<Core.ComponentAutoBindTool>() == null)
                    {
                        // 挂载脚本
                        var com = selectedObject.AddComponent<Core.ComponentAutoBindTool>();
                        com.m_targetScript = mono;
                        Debug.Log("MyScript has been added to: " + selectedObject.name);
                    }
                    else
                    {
                        Debug.LogWarning("MyScript is already attached to: " + selectedObject.name);
                    }
            }
        }
        
        
        // 添加一个方法来控制菜单项的可用性
        [MenuItem("CONTEXT/MonoBehaviour/Add AutoBindComponentTool", true)]
        private static bool ValidateAddAutoBindComponentTool(MenuCommand command)
        {
            var mono = command.context as MonoBehaviour;
            // 如果当前选中的 MonoBehaviour 是 ComponentAutoBindTool，则返回 false
            return mono != null && !(mono is Core.ComponentAutoBindTool);
        }
        
        
        
        
        
        [MenuItem("CONTEXT/MonoBehaviour/Add Component To AutoBindKeyMapSetting", false, 0)]
        private static void AddComponentToAutoBindKeyMapSetting(MenuCommand command)
        {
            // 获取右键点击的对象
            var mono = command.context as MonoBehaviour;
            if (mono != null)
            {
                // 获取当前字符串中 每个大写 的字母
                var str = "";
                var typeName = mono.GetType().Name;
                foreach (var t in typeName)
                {
                    if (char.IsUpper(t))
                    {
                        str += t;
                    }
                }

                var lowerStr = str.ToLower();
                Debug.Log($"{typeName}, 缩写:{lowerStr}");
                
                var autoBindKeyMapInputWizard = ScriptableWizard.DisplayWizard<AutoBindKeyMapInputWizard>("缩写前缀和类型名", "确定");
                autoBindKeyMapInputWizard.key = lowerStr;
                autoBindKeyMapInputWizard.typeName = typeName;
                autoBindKeyMapInputWizard.minSize = new Vector2(300, 200);
                autoBindKeyMapInputWizard.maxSize = new Vector2(300, 200);
            }
        }
        
        
        // 添加一个方法来控制菜单项的可用性
        [MenuItem("CONTEXT/MonoBehaviour/Add Component To AutoBindKeyMapSetting", true)]
        private static bool ValidateAddComponentToAutoBindKeyMapSetting(MenuCommand command)
        {
            var mono = command.context as MonoBehaviour;
            // 如果当前选中的 MonoBehaviour 是 ComponentAutoBindTool，则返回 false
            return mono != null && !(mono is Core.ComponentAutoBindTool);
        }
    }
}
#endif