#if UNITY_EDITOR
using ComponentAutoBindTool.ViewCore;
using UnityEngine;

namespace ComponentAutoBindTool.Tests
{
    public class ExplicitHostBase : MonoBehaviour, IAutoBindHost
    {
        void IAutoBindHost.EnsureAutoBind(GameObject go) { }
    }

    public partial class AutoBindExplicitHost : ExplicitHostBase
    {
    }
}
#endif
