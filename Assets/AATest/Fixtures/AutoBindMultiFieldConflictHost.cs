#if UNITY_EDITOR
using UnityEngine;
namespace ComponentAutoBindTool.Tests
{
    public partial class AutoBindMultiFieldConflictHost : MonoBehaviour
    {
        private int first = 1 < 2 ? 1 : 0, view = 1;
    }
}
#endif
