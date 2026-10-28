#if UNITY_EDITOR
using UE = UnityEngine;
namespace ComponentAutoBindTool.Tests
{
    public partial class AutoBindNamespaceAliasConflictHost : UE.MonoBehaviour
    {
        public void EnsureAutoBind(UE.GameObject root) { }
    }
}
#endif
