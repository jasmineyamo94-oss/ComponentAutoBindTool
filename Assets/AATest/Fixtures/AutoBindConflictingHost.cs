#if UNITY_EDITOR
using ComponentAutoBindTool.ViewCore;
using UnityEngine;

namespace ComponentAutoBindTool.Tests
{
    public partial class AutoBindConflictingHost : MonoBehaviour, IAutoBindHost
    {
        void IAutoBindHost.EnsureAutoBind(GameObject go) { }
    }
}
#endif
