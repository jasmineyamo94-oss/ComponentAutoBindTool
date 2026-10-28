#if UNITY_EDITOR
using UnityEngine;
namespace ComponentAutoBindTool.Tests
{
    public partial class AutoBindOverloadedHost : MonoBehaviour
    {
        public void EnsureAutoBind(int value) { }
        public object GetGeneratedUIView(int value) => value;
    }
}
#endif
