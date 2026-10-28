#if UNITY_EDITOR
using UnityEngine;

namespace ComponentAutoBindTool.Tests
{
    public partial class AutoBindPlainHost : MonoBehaviour
    {
        public void LocalVariableIsNotAField()
        {
            var view = new object();
            System.GC.KeepAlive(view);
        }
    }
}
#endif
