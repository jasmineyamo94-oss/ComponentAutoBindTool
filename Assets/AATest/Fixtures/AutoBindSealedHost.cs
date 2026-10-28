#if UNITY_EDITOR
using UnityEngine;
namespace ComponentAutoBindTool.Tests
{
    public class AutoBindSealedBase : VirtualHostBase
    {
        public sealed override void EnsureAutoBind(GameObject root) { }
    }
    public partial class AutoBindSealedHost : AutoBindSealedBase { }
}
#endif
