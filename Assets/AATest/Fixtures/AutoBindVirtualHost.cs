#if UNITY_EDITOR
using UnityEngine;

namespace ComponentAutoBindTool.Tests
{
    public class VirtualHostBase : MonoBehaviour
    {
        public virtual void EnsureAutoBind(GameObject go) { }
    }

    public partial class AutoBindVirtualHost : VirtualHostBase
    {
    }
}
#endif
