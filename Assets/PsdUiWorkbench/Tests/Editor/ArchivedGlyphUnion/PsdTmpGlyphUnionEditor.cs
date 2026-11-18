using PsdUiWorkbench.Runtime;
using TMPro;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

namespace PsdUiWorkbench.Presentation
{
    [CustomEditor(typeof(PsdTmpGlyphUnion))]
    public sealed class PsdTmpGlyphUnionEditor : Editor
    {
        public override VisualElement CreateInspectorGUI()
        {
            var root = new VisualElement();
            var shader = new PropertyField(serializedObject.FindProperty("_unionShader"), "邻字合成 Shader");
            shader.SetEnabled(false);
            root.Add(shader);
            var status = new HelpBox("", HelpBoxMessageType.Info);
            root.Add(status);
            var component = (PsdTmpGlyphUnion)target;
            var text = component.GetComponent<TextMeshProUGUI>();
            void Refresh()
            {
                if (component == null) return;
                bool warning = !string.IsNullOrEmpty(component.Diagnostic);
                status.messageType = warning ? HelpBoxMessageType.Warning : HelpBoxMessageType.Info;
                status.text = warning ? component.Diagnostic : EditorUtility.IsPersistent(component)
                    ? "实例激活后自动同步邻字数据。支持单行、统一样式、最多 32 个可见字形；其他布局回退为单字形描边。"
                    : $"合成字形：{component.ActiveGlyphCount}；布局上传：{component.LayoutUploadCount}。文字或排版重建时同步，无 Update。";
            }
            void OnText(TMP_TextInfo _) => Refresh();
            root.RegisterCallback<AttachToPanelEvent>(_ => { if (text != null) text.OnPreRenderText += OnText; Refresh(); });
            root.RegisterCallback<DetachFromPanelEvent>(_ => { if (text != null) text.OnPreRenderText -= OnText; });
            root.TrackSerializedObjectValue(serializedObject, _ => Refresh());
            Refresh();
            return root;
        }
    }
}
