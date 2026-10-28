using System;
using System.IO;
using ComponentAutoBindTool.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace ComponentAutoBindTool.Example.Editor
{
    public sealed class PipelineLabWindow : EditorWindow
    {
        private PipelineLabState m_State;
        private VisualElement m_Writes;
        private TextField m_Hierarchy, m_Diff, m_Report;
        private Label m_Busy;
        [MenuItem("自动绑定UI组件/框架实验台/编辑器绑定实验台")]
        public static void Open() => GetWindow<PipelineLabWindow>("自动绑定实验台");

        public void CreateGUI()
        {
            m_State = PipelineLabState.Load();
            rootVisualElement.Clear();
            var style = AssetDatabase.LoadAssetAtPath<StyleSheet>(AutoBindExampleSetup.ExampleRoot + "/Editor/PipelineLab.uss");
            if (style != null) rootVisualElement.styleSheets.Add(style);
            rootVisualElement.AddToClassList("lab");
            rootVisualElement.Add(new Label("AUTOBIND / 编辑器流水线实验台") { name = "title" });
            rootVisualElement.Add(new Label("真实 Inspector 入口 · 独立本地资产 · 报告跨脚本重载保留"));
            m_Writes = new VisualElement(); m_Writes.AddToClassList("toolbar");
            var choice = new PopupField<string>("实验", new System.Collections.Generic.List<string>(PipelineLabExperiment.Names), m_State.Experiment);
            choice.RegisterValueChangedCallback(evt =>
            {
                m_State.Experiment = Array.IndexOf(PipelineLabExperiment.Names, evt.newValue);
                m_State.TargetId = null;
                m_State.Report = "选择已更改，请恢复实验初始状态。";
                m_State.Save(); Render();
            });
            m_Writes.Add(choice);
            AddButton(m_Writes, "恢复实验初始状态", () => { m_State = PipelineLabExperiment.Prepare((PipelineLabCase)m_State.Experiment); Render(); });
            AddButton(m_Writes, "重扫与校验", () => Run(false));
            AddButton(m_Writes, "生成并保存", () => Run(true));
            rootVisualElement.Add(m_Writes);
            var links = new VisualElement(); links.AddToClassList("toolbar");
            AddButton(links, "定位对象", () => { var target = PipelineLabExperiment.Resolve(m_State); Selection.activeObject = target; EditorGUIUtility.PingObject(target); });
            AddButton(links, "打开生成文件", () => { if (File.Exists(m_State.GeneratedPath)) AssetDatabase.OpenAsset(AssetDatabase.LoadAssetAtPath<MonoScript>(m_State.GeneratedPath)); });
            AddButton(links, "打开运行时示例", () =>
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling) return;
                if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) EditorSceneManager.OpenScene(AutoBindExampleSetup.ScenePath);
            });
            rootVisualElement.Add(links);
            m_Busy = new Label(); rootVisualElement.Add(m_Busy);
            var panels = new VisualElement(); panels.AddToClassList("panels");
            m_Hierarchy = Panel(panels, "目标层级与绑定表");
            m_Diff = Panel(panels, "源码变化");
            m_Report = Panel(panels, "统一报告与恢复检查");
            rootVisualElement.Add(panels);
            rootVisualElement.schedule.Execute(() =>
            {
                bool busy = EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode;
                m_Writes.SetEnabled(!busy);
                m_Busy.text = busy ? "编译 / 导入 / Play Mode：写入已禁用" : "本地实验目录：" + PipelineLabExperiment.Workspace;
            }).Every(200);
            Render();
        }

        private void Run(bool generate) { PipelineLabExperiment.Execute(m_State, generate); Render(); }
        private void Render()
        {
            m_Hierarchy.SetValueWithoutNotify(m_State.Hierarchy ?? "尚未准备实验");
            m_Diff.SetValueWithoutNotify(m_State.Diff ?? "尚未生成");
            m_Report.SetValueWithoutNotify(m_State.Report ?? "请选择实验，然后恢复初始状态。");
        }
        private void AddButton(VisualElement parent, string text, Action action)
        {
            parent.Add(new Button(() =>
            {
                try
                {
                    if (parent == m_Writes && (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode))
                        throw new InvalidOperationException("编译、导入或 Play Mode 期间禁止写入实验资产。");
                    action();
                }
                catch (Exception ex) { m_State.Report = ex.Message; m_State.Save(); Render(); Debug.LogException(ex); }
            }) { text = text });
        }
        private static TextField Panel(VisualElement parent, string label)
        {
            var scroll = new ScrollView(); scroll.AddToClassList("panel");
            scroll.Add(new Label(label));
            var field = new TextField { multiline = true, isReadOnly = true };
            field.AddToClassList("content"); scroll.Add(field); parent.Add(scroll);
            return field;
        }
    }
}
