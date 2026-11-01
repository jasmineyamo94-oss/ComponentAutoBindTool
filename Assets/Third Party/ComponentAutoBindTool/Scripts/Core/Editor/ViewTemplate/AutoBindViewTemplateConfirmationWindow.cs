using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace ComponentAutoBindTool.Editor
{
    internal sealed class AutoBindViewTemplateConfirmationWindow : EditorWindow
    {
        [SerializeField] private string _message;
        [SerializeField] private string _continueLabel;
        [NonSerialized] private bool _accepted;

        internal static bool Confirm(string title, string message, string continueLabel)
        {
            var window = CreateInstance<AutoBindViewTemplateConfirmationWindow>();
            window.titleContent = new GUIContent(title);
            window._message = message;
            window._continueLabel = continueLabel;
            window.ShowModalUtility();
            return window._accepted;
        }

        public void CreateGUI()
        {
            minSize = new Vector2(660, 360);
            rootVisualElement.Clear();
            AutoBindViewTemplateUI.Load(rootVisualElement, this);
            var body = new VisualElement(); body.AddToClassList("template-body"); rootVisualElement.Add(body);
            var scroll = new ScrollView(); scroll.style.flexGrow = 1; body.Add(scroll);
            var message = new Label(_message ?? ""); message.style.whiteSpace = WhiteSpace.Normal; scroll.Add(message);
            var actions = new VisualElement(); actions.AddToClassList("template-actions"); body.Add(actions);
            var cancel = new Button(Close) { text = "取消", name = "cancelDeletionConfirmation" };
            var accept = new Button(() => { _accepted = true; Close(); }) { text = _continueLabel ?? "继续", name = "acceptDeletionConfirmation" };
            actions.Add(cancel); actions.Add(accept);
            // 不依赖系统弹窗的 ok/cancel 按钮顺序；Enter 和 Escape 都明确取消。
            rootVisualElement.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.KeypadEnter && evt.keyCode != KeyCode.Escape) return;
                evt.PreventDefault();
                evt.StopImmediatePropagation();
                Close();
            }, TrickleDown.TrickleDown);
            rootVisualElement.schedule.Execute(cancel.Focus);
        }
    }
}
