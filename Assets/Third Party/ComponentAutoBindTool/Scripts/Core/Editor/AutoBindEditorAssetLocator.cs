using UnityEditor;
using UnityEngine;

namespace ComponentAutoBindTool.Editor
{
    internal static class AutoBindEditorAssetLocator
    {
        public static T LoadSingletonAsset<T>() where T : ScriptableObject
        {
            string[] assetGuids = AssetDatabase.FindAssets($"t:{typeof(T).Name}");
            if (assetGuids.Length == 0)
            {
                Debug.LogError($"不存在 {typeof(T).Name}");
                return null;
            }

            if (assetGuids.Length > 1)
            {
                Debug.LogError($"{typeof(T).Name} 数量大于1");
                return null;
            }

            string assetPath = AssetDatabase.GUIDToAssetPath(assetGuids[0]);
            return AssetDatabase.LoadAssetAtPath<T>(assetPath);
        }
    }
}
