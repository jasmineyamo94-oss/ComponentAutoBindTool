using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace ComponentAutoBindTool.Editor
{
    public sealed class AutoBindViewTemplateSettingsWindow : EditorWindow
    {
        [SerializeField] private bool _hasDraft;
        [SerializeField] private string _prefabDraft;
        [SerializeField] private string _scriptDraft;
        private HelpBox _status;

        internal static void Open(string prefabRoot = null, string scriptRoot = null)
        {
            var window = GetWindow<AutoBindViewTemplateSettingsWindow>("UI 模板路径设置");
            if (prefabRoot != null && scriptRoot != null)
            {
                window._prefabDraft = prefabRoot;
                window._scriptDraft = scriptRoot;
                window._hasDraft = true;
                window.CreateGUI();
            }
            window.Show();
        }

        public void CreateGUI()
        {
            minSize = new Vector2(600, 280);
            rootVisualElement.Clear();
            AutoBindViewTemplateUI.Load(rootVisualElement, this);
            var body = new VisualElement(); body.AddToClassList("template-body"); rootVisualElement.Add(body);
            var title = new Label("UI 模板路径设置"); title.AddToClassList("template-title"); body.Add(title);
            var description = new Label("仅配置输出根目录。脚本自动放入基础名子目录，设置保存到 ProjectSettings，供项目共享。");
            description.AddToClassList("template-description"); body.Add(description);
            if (!_hasDraft)
            {
                _prefabDraft = AutoBindViewTemplateSettings.instance.PrefabRoot;
                _scriptDraft = AutoBindViewTemplateSettings.instance.ScriptRoot;
                _hasDraft = true;
            }
            AddFolder(body, "Prefab 根目录", "prefabRoot", _prefabDraft, value => _prefabDraft = value);
            AddFolder(body, "脚本根目录", "scriptRoot", _scriptDraft, value => _scriptDraft = value);
            _status = new HelpBox("文件夹须位于 Assets 内；不存在的目录在创建模板时建立。", HelpBoxMessageType.Info); body.Add(_status);
            var actions = new VisualElement(); actions.AddToClassList("template-actions"); body.Add(actions);
            actions.Add(new Button(() =>
            {
                try
                {
                    AutoBindViewTemplateSettings.instance.Store(_prefabDraft, _scriptDraft);
                    AutoBindViewTemplateWindow.UseSavedPaths();
                    Close();
                }
                catch (Exception error) { _status.text = error.Message; _status.messageType = HelpBoxMessageType.Error; }
            }) { text = "保存设置", name = "saveTemplateSettings" });
            actions.Add(new Button(Close) { text = "取消" });
        }

        private void AddFolder(VisualElement root, string label, string name, string value, Action<string> changed)
        {
            var row = new VisualElement(); row.AddToClassList("template-folder"); root.Add(row);
            var field = new TextField(label) { name = name, value = value }; row.Add(field);
            field.RegisterValueChangedCallback(evt => changed(evt.newValue));
            row.Add(new Button(() =>
            {
                try
                {
                    string current = Application.dataPath;
                    if (!string.IsNullOrWhiteSpace(field.value) && Directory.Exists(field.value)) current = Path.GetFullPath(field.value);
                    string selected = EditorUtility.OpenFolderPanel(label, current, "");
                    if (!string.IsNullOrEmpty(selected)) field.value = AutoBindViewTemplateDefinition.NormalizeFolder(selected);
                }
                catch (Exception error) { _status.text = error.Message; _status.messageType = HelpBoxMessageType.Error; }
            }) { text = "选择…" });
        }
    }
}
