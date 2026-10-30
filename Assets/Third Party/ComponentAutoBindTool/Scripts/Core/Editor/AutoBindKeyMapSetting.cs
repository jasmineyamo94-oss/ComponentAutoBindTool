#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;

namespace ComponentAutoBindTool.Core
{
    [MovedFrom(true, "Third_Party.ComponentAutoBindTool.Scripts.Core", "Assembly-CSharp", "AutoBindKeyMapSetting")]
    public class AutoBindKeyMapSetting : ScriptableObject
    {
        /// <summary>
        /// 默认组件映射
        /// </summary>
        public readonly Dictionary<string, string> DefaultComponentKeyMap = new()
        {
            { "Tf", "Transform" },
            { "OAni", "Animation" },
            { "NAni", "Animator" },

            { "Rtf", "RectTransform" },
            { "Cav", "Canvas" },
            { "CGroup", "CanvasGroup" },
            { "VLGroup", "UnityEngine.UI.VerticalLayoutGroup" },
            { "HLGroup", "UnityEngine.UI.HorizontalLayoutGroup" },
            { "GLGroup", "UnityEngine.UI.GridLayoutGroup" },
            { "TGroup", "UnityEngine.UI.ToggleGroup" },

            { "Btn", "UnityEngine.UI.Button" },
            { "Img", "UnityEngine.UI.Image" },
            { "RImg", "UnityEngine.UI.RawImage" },
            { "Txt", "UnityEngine.UI.Text" },
            { "Inf", "UnityEngine.UI.InputField" },
            { "Sld", "UnityEngine.UI.Slider" },
            { "Mask", "UnityEngine.UI.Mask" },
            { "Mask2D", "UnityEngine.UI.RectMask2D" },
            { "Tog", "UnityEngine.UI.Toggle" },
            { "Sbr", "UnityEngine.UI.Scrollbar" },
            { "SRect", "UnityEngine.UI.ScrollRect" },
            { "Drop", "UnityEngine.UI.Dropdown" },
                
            { "TMTxt", "TMPro.TextMeshProUGUI" },
            { "TMDrop", "TMPro.TMP_Dropdown" },
            { "TMInf", "TMPro.TMP_InputField" },
            { "Widget", "ComponentAutoBindTool.ViewCore.WidgetViewBase" },
            { "Sub", "ComponentAutoBindTool.Core.ComponentAutoBindTool" },
        };
        
        public GenericDictionary<string, string> extraComponentKeyMap;

        public bool TryGetComponentTypeName(string key, out string componentTypeName)
        {
            if (DefaultComponentKeyMap.TryGetValue(key, out componentTypeName))
            {
                return true;
            }

            if (extraComponentKeyMap != null && extraComponentKeyMap.TryGetValue(key, out componentTypeName))
            {
                return true;
            }

            componentTypeName = null;
            return false;
        }

        public IEnumerable<KeyValuePair<string, string>> GetAllComponentKeyMaps()
        {
            foreach (KeyValuePair<string, string> item in DefaultComponentKeyMap.OrderBy(pair => pair.Key))
            {
                yield return item;
            }

            if (extraComponentKeyMap == null)
            {
                yield break;
            }

            foreach (KeyValuePair<string, string> item in extraComponentKeyMap.Where(pair => !DefaultComponentKeyMap.ContainsKey(pair.Key)).OrderBy(pair => pair.Key))
            {
                yield return item;
            }
        }

        [MenuItem("自动绑定UI组件/创建组件映射文件")]
        private static void CreateAutoBindKeyMapSetting()
        {
            string[] paths = AssetDatabase.FindAssets("t:AutoBindKeyMapSetting");
            if (paths.Length >= 1)
            {
                string path = AssetDatabase.GUIDToAssetPath(paths[0]);
                EditorUtility.DisplayDialog("警告", $"已存在AutoBindKeyMapSetting，路径:{path}", "确认");
                return;
            }

            AutoBindKeyMapSetting setting = CreateInstance<AutoBindKeyMapSetting>();
            AssetDatabase.CreateAsset(setting, "Assets/Third Party/ComponentAutoBindTool/AutoBindKeyMapSetting.asset");
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }
    }
}
#endif
