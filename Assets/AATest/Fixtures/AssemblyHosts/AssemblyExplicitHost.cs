using ComponentAutoBindTool.ViewCore;
using UnityEngine;

namespace ComponentAutoBindTool.Tests.AssemblyHosts
{
    public class AssemblyExplicitBase : MonoBehaviour, IAutoBindHost
    {
        // 生成器必须重新实现接口，调用不能落入这个旧实现。
        void IAutoBindHost.EnsureAutoBind(GameObject go)
        {
            throw new System.InvalidOperationException("接口没有执行生成绑定。");
        }
    }

    public partial class AssemblyExplicitHost : AssemblyExplicitBase { }
}
