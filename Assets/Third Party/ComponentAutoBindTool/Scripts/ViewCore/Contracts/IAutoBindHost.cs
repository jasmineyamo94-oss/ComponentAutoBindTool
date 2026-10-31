using UnityEngine;

// ReSharper disable once CheckNamespace
namespace ComponentAutoBindTool.ViewCore
{
    public interface IAutoBindHost
    {
        void EnsureAutoBind(GameObject go);

        /// <summary>生成宿主只有在全部绑定成功后才返回 true；默认实现兼容旧宿主。</summary>
        bool IsAutoBound => true;
    }
}