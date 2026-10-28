using UnityEngine;

// ReSharper disable once CheckNamespace
namespace ComponentAutoBindTool.ViewCore
{
    public interface IAutoBindHost
    {
        void EnsureAutoBind(GameObject go);
    }
}