using System;
using System.Collections.Generic;
using System.IO;
using ComponentAutoBindTool.Core;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace ComponentAutoBindTool.Editor
{
    public sealed class AutoBindViewTemplateWindow : EditorWindow
    {
        [SerializeField] private string m_BaseName = "";
        [SerializeField] private bool m_UseArgs = true;
        [SerializeField] private GameObject m_Context;
        [SerializeReference] private AutoBindViewTemplateDefinition m_RecreateRecipe;
        private AutoBindGlobalSetting m_Global;
        private TextField m_Name;
        private Toggle m_Args;
        private HelpBox m_Status;
        private Button m_Create, m_Retry, m_Cancel, m_Clear, m_Backup, m_Prefab, m_Script, m_DeleteLocated;
        private string m_Error;
        private string m_PreviewKey;
        private readonly List<TextField> m_Outputs = new();
        private readonly List<Label> m_OutputNames = new();

        [MenuItem("GameObject/UI (Canvas)/UI View Template", false, 10)]
        private static void Open(MenuCommand command)
        {
            var window = GetWindow<AutoBindViewTemplateWindow>("UI View Template");
            if (AutoBindViewTemplateCreation.instance.RecreationRecipe == null)
                window.m_Context = command.context as GameObject ?? Selection.activeGameObject;
            window.Show();
            window.Focus();
        }

        public void CreateGUI()
        {
            var restored = AutoBindViewTemplateCreation.instance;
            if (m_RecreateRecipe?.IsInitialized != true) m_RecreateRecipe = null;
            if (m_RecreateRecipe == null && restored.RecreationRecipe != null)
            {
                m_RecreateRecipe = restored.RecreationRecipe;
                m_BaseName = m_RecreateRecipe.BaseName;
                m_UseArgs = m_RecreateRecipe.UseArgs;
                m_Context = restored.RecreationParent;
            }
            minSize = new Vector2(680, 630);
            rootVisualElement.Clear();
            AutoBindViewTemplateUI.Load(rootVisualElement, this);
            var scroll = new ScrollView();
            scroll.AddToClassList("template-body");
            rootVisualElement.Add(scroll);
            var title = new Label("创建 UI 界面模板"); title.AddToClassList("template-title"); scroll.Add(title);
            var hint = new Label("输入基础名，其余名称按固定规则生成。例：Inventory → InventoryView。");
            hint.AddToClassList("template-description"); scroll.Add(hint);
            m_Name = new TextField("界面基础名") { name = "baseName", value = m_BaseName };
            m_Name.RegisterValueChangedCallback(evt => { m_BaseName = evt.newValue; m_Error = null; Refresh(); });
            scroll.Add(m_Name);
            m_Args = new Toggle("使用 Args 传递数据") { name = "useArgs", value = m_UseArgs };
            m_Args.RegisterValueChangedCallback(evt => { m_UseArgs = evt.newValue; m_Error = null; Refresh(); });
            scroll.Add(m_Args);
            m_Outputs.Clear();
            m_OutputNames.Clear();
            m_PreviewKey = null;
            foreach (string label in new[] { "View 脚本", "Presenter 脚本", "Args 脚本", "Routes 脚本", "绑定脚本", "界面 Prefab" })
            {
                var name = new Label(label) { name = "outputName" + m_Outputs.Count };
                name.AddToClassList("template-file-name"); scroll.Add(name); m_OutputNames.Add(name);
                var field = new TextField { isReadOnly = true, name = "output" + m_Outputs.Count };
                field.AddToClassList("template-output"); scroll.Add(field); m_Outputs.Add(field);
            }
            var paths = new VisualElement(); paths.AddToClassList("template-actions");
            paths.Add(new Button(() => AutoBindViewTemplateSettingsWindow.Open(
                m_RecreateRecipe == null ? null : Path.GetDirectoryName(m_RecreateRecipe.PrefabPath)?.Replace('\\', '/'),
                m_RecreateRecipe == null ? null : Path.GetDirectoryName(m_RecreateRecipe.ScriptDirectory)?.Replace('\\', '/'))) { text = "路径设置" });
            scroll.Add(paths);
            m_Status = new HelpBox("", HelpBoxMessageType.Info) { name = "creationStatus" }; scroll.Add(m_Status);
            var actions = new VisualElement(); actions.AddToClassList("template-actions");
            m_Create = new Button(() => Execute(() => AutoBindViewTemplateCreation.instance.Begin(CurrentDefinition(), m_Context))) { text = "创建", name = "createTemplate" };
            m_Retry = new Button(() => Execute(() => AutoBindViewTemplateCreation.instance.Retry())) { text = "重试", name = "retryTemplate" };
            m_Cancel = new Button(() => Execute(() => AutoBindViewTemplateCreation.instance.Cancel())) { text = "取消并清理本次产物", name = "cancelTemplate" };
            actions.Add(m_Create); actions.Add(m_Retry); actions.Add(m_Cancel); scroll.Add(actions);
            var cleanup = new VisualElement(); cleanup.AddToClassList("template-actions");
            m_Clear = new Button(() => Execute(ClearAndPrepareRecreation)) { text = "清除并重新创建", name = "clearTemplate" };
            m_Backup = new Button(() => EditorUtility.RevealInFinder(AutoBindViewTemplateCreation.instance.ClearBackupPath)) { text = "打开清除备份", name = "clearBackup" };
            cleanup.Add(m_Clear); cleanup.Add(m_Backup); scroll.Add(cleanup);
            m_DeleteLocated = new Button(() => Execute(OpenLocatedDeletion)) { text = "按位置删除已有模板…", name = "deleteLocatedTemplate" };
            var located = new VisualElement(); located.AddToClassList("template-actions"); located.Add(m_DeleteLocated); scroll.Add(located);
            var deletionHint = new Label("没有本次生成记录时，可按位置定位文件；仅删除磁盘资产，不清除场景实例。");
            deletionHint.AddToClassList("template-description"); scroll.Add(deletionHint);
            var links = new VisualElement(); links.AddToClassList("template-actions");
            m_Prefab = new Button(() => EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<GameObject>(AutoBindViewTemplateCreation.instance.Definition.PrefabPath))) { text = "定位 Prefab" };
            m_Script = new Button(() => AssetDatabase.OpenAsset(AssetDatabase.LoadAssetAtPath<MonoScript>(AutoBindViewTemplateCreation.instance.Definition.ViewPath))) { text = "打开 View 脚本" };
            links.Add(m_Prefab); links.Add(m_Script); scroll.Add(links);
            ReadGlobal();
            Refresh();
            rootVisualElement.schedule.Execute(Refresh).Every(200);
        }

        private void OnFocus() { ReadGlobal(); Refresh(); }
        private void OnProjectChange() { ReadGlobal(); Refresh(); }
        private void ReadGlobal()
        {
            try { m_Global = AutoBindViewTemplateCreation.ReadConfiguration(); m_Error = null; }
            catch (Exception error) { m_Global = null; m_Error = error.Message; }
        }

        private AutoBindViewTemplateDefinition CurrentDefinition()
        {
            var settings = AutoBindViewTemplateSettings.instance;
            return AutoBindViewTemplateDefinition.Create(m_BaseName,
                m_RecreateRecipe == null ? settings.PrefabRoot : Path.GetDirectoryName(m_RecreateRecipe.PrefabPath),
                m_RecreateRecipe == null ? settings.ScriptRoot : Path.GetDirectoryName(m_RecreateRecipe.ScriptDirectory),
                m_RecreateRecipe == null ? m_Global != null ? m_Global.Namespace : "" : m_RecreateRecipe.Namespace, m_UseArgs);
        }

        private void ClearAndPrepareRecreation()
        {
            var job = AutoBindViewTemplateCreation.instance;
            var paths = job.PreviewCompletedClear();
            var recipe = job.LastCreatedDefinition;
            var parent = job.LastCreatedParent;
            if (!EditorUtility.DisplayDialog("清除并重新创建 " + recipe.ViewName,
                "将删除最近一次成功生成的以下产物及其 .meta：\n\n" + string.Join("\n", paths) +
                "\n\n清除不可通过 Undo 恢复，备份保存在 Library。不会保存场景；清除并等待编译后，可调整 Args 再点击创建。", "清除并返回表单", "取消")) return;
            job.ClearCompleted();
            m_RecreateRecipe = recipe;
            m_BaseName = recipe.BaseName;
            m_UseArgs = recipe.UseArgs;
            m_Context = parent;
            m_Name.SetValueWithoutNotify(m_BaseName);
            m_Args.SetValueWithoutNotify(m_UseArgs);
        }

        private void OpenLocatedDeletion()
        {
            string basis = AutoBindViewTemplateDefinition.ValidateBaseName(m_BaseName);
            var settings = AutoBindViewTemplateSettings.instance;
            string scripts = m_RecreateRecipe == null ? settings.ScriptRoot : Path.GetDirectoryName(m_RecreateRecipe.ScriptDirectory);
            string prefabs = m_RecreateRecipe == null ? settings.PrefabRoot : Path.GetDirectoryName(m_RecreateRecipe.PrefabPath);
            AutoBindViewTemplateDeletionWindow.Open(basis, scripts + "/" + basis, prefabs + "/" + basis + "View.prefab");
        }

        internal static void UseSavedPaths()
        {
            AutoBindViewTemplateCreation.instance.UseSavedPaths();
            foreach (var window in Resources.FindObjectsOfTypeAll<AutoBindViewTemplateWindow>())
            {
                window.m_RecreateRecipe = null;
                window.Refresh();
            }
        }

        private void Execute(Action action)
        {
            m_Error = null;
            try { action(); }
            catch (Exception error) { m_Error = error.Message; }
            Refresh();
        }

        private void Refresh()
        {
            if (m_Name == null) return;
            var job = AutoBindViewTemplateCreation.instance;
            AutoBindViewTemplateDefinition definition = null;
            string validation = null;
            try { definition = job.HasPending ? job.Definition : CurrentDefinition(); }
            catch (Exception error) { validation = error.Message; }
            if (m_Global == null) validation = "请先创建唯一的 AutoBind 全局配置和组件映射配置；配置就绪后才能创建。";
            string key = definition == null ? "" : string.Join("\n", definition.OutputPaths);
            if (key != m_PreviewKey)
            {
                m_PreviewKey = key;
                string[] paths = definition == null ? new string[6] : new[]
                {
                    definition.ViewPath, definition.PresenterPath, definition.UseArgs ? definition.ArgsPath : null,
                    definition.RoutesPath, definition.BindingPath, definition.PrefabPath
                };
                for (int i = 0; i < m_Outputs.Count; i++)
                {
                    bool omittedArgs = definition != null && !definition.UseArgs && i == 2;
                    m_Outputs[i].SetValueWithoutNotify(omittedArgs ? "不生成专属 Args，使用 EmptyViewArgs" : paths[i] ?? "—");
                    m_OutputNames[i].text = omittedArgs ? "Args 脚本（不生成）" : paths[i] == null ? "—" : Path.GetFileName(paths[i]);
                }
            }
            m_Name.SetEnabled(!job.HasPending);
            m_Args.SetEnabled(!job.HasPending);
            if (job.HasPending && definition != null)
            {
                m_BaseName = definition.BaseName;
                m_UseArgs = definition.UseArgs;
                m_Name.SetValueWithoutNotify(m_BaseName);
                m_Args.SetValueWithoutNotify(m_UseArgs);
            }
            bool idle = !EditorApplication.isCompiling && !EditorApplication.isUpdating;
            string blocked = AutoBindViewTemplateCreation.BlockReason();
            m_Create.SetEnabled(!job.HasPending && blocked == null && definition != null && m_Global != null);
            m_Retry.SetEnabled(idle && job.Phase == AutoBindTemplatePhase.Failed);
            m_Cancel.SetEnabled(idle && job.CanCancel && !EditorApplication.isPlayingOrWillChangePlaymode && UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage() == null);
            m_Clear.SetEnabled(job.CanClearCompleted && AutoBindViewTemplateCreation.BlockReason(true) == null);
            m_Backup.SetEnabled(!string.IsNullOrEmpty(job.ClearBackupPath) && Directory.Exists(job.ClearBackupPath));
            bool validName;
            try { AutoBindViewTemplateDefinition.ValidateBaseName(m_BaseName); validName = true; }
            catch (ArgumentException) { validName = false; }
            m_DeleteLocated.SetEnabled(validName && AutoBindViewTemplateDeletion.BlockReason() == null);
            m_Prefab.SetEnabled(job.Phase == AutoBindTemplatePhase.Completed && job.Definition != null && File.Exists(job.Definition.PrefabPath));
            m_Script.SetEnabled(job.Definition != null && File.Exists(job.Definition.ViewPath));
            m_Status.text = m_Error ?? (job.HasPending || job.Phase == AutoBindTemplatePhase.Completed ? job.Message : validation ?? blocked ?? job.Message);
            m_Status.messageType = m_Error != null || job.Phase == AutoBindTemplatePhase.Failed ? HelpBoxMessageType.Error : HelpBoxMessageType.Info;
        }
    }

    internal static class AutoBindViewTemplateUI
    {
        internal static void Load(VisualElement root, ScriptableObject owner)
        {
            string scriptPath = AssetDatabase.GetAssetPath(MonoScript.FromScriptableObject(owner));
            string stylePath = Path.GetDirectoryName(scriptPath)?.Replace('\\', '/') + "/AutoBindViewTemplate.uss";
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(stylePath);
            if (sheet != null) root.styleSheets.Add(sheet);
            root.AddToClassList("view-template");
        }
    }
}
