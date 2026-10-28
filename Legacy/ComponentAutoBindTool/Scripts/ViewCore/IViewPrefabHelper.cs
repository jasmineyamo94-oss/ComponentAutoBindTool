using UnityEngine;

namespace Third_Party.ComponentAutoBindTool.Scripts.ViewCore
{
    public interface IViewPrefabHelper
    {
        GameObject GetViewPrefab(string viewName);
    }
}