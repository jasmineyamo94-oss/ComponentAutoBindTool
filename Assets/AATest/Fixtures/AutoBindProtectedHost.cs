#if UNITY_EDITOR
using UnityEngine;

namespace ComponentAutoBindTool.Tests
{
    public class ProtectedHostBase : MonoBehaviour
    {
        protected internal virtual void EnsureAutoBind(GameObject go) { }
    }

    public partial class AutoBindProtectedHost : ProtectedHostBase
    {
    }
}
#endif
