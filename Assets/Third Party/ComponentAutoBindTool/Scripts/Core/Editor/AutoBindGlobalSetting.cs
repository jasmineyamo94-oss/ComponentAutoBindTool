using UnityEditor;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;

namespace ComponentAutoBindTool.Editor
{
    /// <summary>
    /// 自动绑定全局设置
    /// </summary>
    [MovedFrom(true, "ComponentAutoBindTool.Editor", "Assembly-CSharp-Editor", "AutoBindGlobalSetting")]
    public class AutoBindGlobalSetting : ScriptableObject
    {
        [SerializeField] 
        private string _codePath = "Assets/Third Party/ComponentAutoBindTool/Scripts/ViewBindComponents";

        [SerializeField]
        private string _namespace;
        
        [SerializeField]
        private bool _useGlobalDefaultSavePath = true;

        public string CodePath => AutoBindPathUtility.NormalizeCodePath(_codePath);
        internal string RawCodePath => _codePath;
        public string Namespace => _namespace;
        public bool UseGlobalDefaultSavePath => _useGlobalDefaultSavePath;

        [MenuItem("自动绑定UI组件/创建配置文件")]
        private static void CreateAutoBindGlobalSetting()
        {
            string[] paths = AssetDatabase.FindAssets("t:AutoBindGlobalSetting");
            if (paths.Length >= 1)
            {
                string path = AssetDatabase.GUIDToAssetPath(paths[0]);
                EditorUtility.DisplayDialog("警告", $"已存在AutoBindGlobalSetting，路径:{path}", "确认");
                return;
            }

            AutoBindGlobalSetting setting = CreateInstance<AutoBindGlobalSetting>();
            AssetDatabase.CreateAsset(setting, "Assets/Third Party/ComponentAutoBindTool/AutoBindGlobalSetting.asset");
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }
    }
}
