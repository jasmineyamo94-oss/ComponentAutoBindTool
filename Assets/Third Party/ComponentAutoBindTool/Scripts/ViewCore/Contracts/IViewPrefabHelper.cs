using UnityEngine;

// ReSharper disable once CheckNamespace
namespace ComponentAutoBindTool.ViewCore
{
    public interface IViewPrefabHelper
    {
        GameObject GetViewPrefab(string viewName);

        /// <summary>
        /// 释放窗口实例（解决 A-P10：支持接入 Addressables.ReleaseInstance 或对象池，默认直接 Destroy）
        /// </summary>
        void ReleaseViewInstance(string viewName, GameObject instance)
        {
            if (instance != null)
            {
                Object.Destroy(instance);
            }
        }
    }
}