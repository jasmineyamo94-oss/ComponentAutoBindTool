#if UNITY_EDITOR
using UnityEngine;
namespace ComponentAutoBindTool.Tests
{
    public class AutoBindHiddenBase : VirtualHostBase
    {
        public new void EnsureAutoBind(GameObject root) { }
    }
    public partial class AutoBindHiddenHost : AutoBindHiddenBase { }
}
#endif
