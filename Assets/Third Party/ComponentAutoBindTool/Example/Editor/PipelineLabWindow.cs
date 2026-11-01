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
        private PipelineLabState _state;
        private VisualElement _writes;
        private TextField _hierarchy, _diff, _report;
        private Label _busy;
        [MenuItem("自动绑定UI组件/框架实验台/编辑器绑定实验台")]
        public static void Open() => GetWindow<PipelineLabWindow>("自动绑定实验台");

        public void CreateGUI()
        {
            _state = PipelineLabState.Load();
            rootVisualElement.Clear();
            var style = AssetDatabase.LoadAssetAtPath<StyleSheet>(AutoBindExampleSetup.ExampleRoot + "/Editor/PipelineLab.uss");
            if (style != null) rootVisualElement.styleSheets.Add(style);
            rootVisualElement.AddToClassList("lab");
            rootVisualElement.Add(new Label("AUTOBIND / 编辑器流水线实验台") { name = "title" });
            rootVisualElement.Add(new Label("真实 Inspector 入口 · 独立本地资产 · 报告跨脚本重载保留"));
            _writes = new VisualElement(); _writes.AddToClassList("toolbar");
            var choice = new PopupField<string>("实验", new System.Collections.Generic.List<string>(PipelineLabExperiment.Names), _state.Experiment);
            choice.RegisterValueChangedCallback(evt =>
            {
                _state.Experiment = Array.IndexOf(PipelineLabExperiment.Names, evt.newValue);
                _state.TargetId = null;
                _state.Report = "选择已更改，请恢复实验初始状态。";
                _state.Save(); Render();
            });
            _writes.Add(choice);
            AddButton(_writes, "恢复实验初始状态", () => { _state = PipelineLabExperiment.Prepare((PipelineLabCase)_state.Experiment); Render(); });
            AddButton(_writes, "重扫与校验", () => Run(false));
            AddButton(_writes, "生成代码", () => Run(true));
            rootVisualElement.Add(_writes);
            var links = new VisualElement(); links.AddToClassList("toolbar");
            AddButton(links, "定位对象", () => { var target = PipelineLabExperiment.Resolve(_state); Selection.activeObject = target; EditorGUIUtility.PingObject(target); });
            AddButton(links, "打开生成文件", () => { if (File.Exists(_state.GeneratedPath)) AssetDatabase.OpenAsset(AssetDatabase.LoadAssetAtPath<MonoScript>(_state.GeneratedPath)); });
            AddButton(links, "打开运行时示例", () =>
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling) return;
                if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) EditorSceneManager.OpenScene(AutoBindExampleSetup.ScenePath);
            });
            rootVisualElement.Add(links);
            _busy = new Label(); rootVisualElement.Add(_busy);
            var panels = new VisualElement(); panels.AddToClassList("panels");
            _hierarchy = Panel(panels, "目标层级与绑定表");
            _diff = Panel(panels, "源码变化");
            _report = Panel(panels, "统一报告与恢复检查");
            rootVisualElement.Add(panels);
            rootVisualElement.schedule.Execute(() =>
            {
                bool busy = EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode;
                _writes.SetEnabled(!busy);
                _busy.text = busy ? "编译 / 导入 / Play Mode：写入已禁用" : "本地实验目录：" + PipelineLabExperiment.Workspace;
            }).Every(200);
            Render();
        }

        private void Run(bool generate) { PipelineLabExperiment.Execute(_state, generate); Render(); }
        private void Render()
        {
            _hierarchy.SetValueWithoutNotify(_state.Hierarchy ?? "尚未准备实验");
            _diff.SetValueWithoutNotify(_state.Diff ?? "尚未生成");
            _report.SetValueWithoutNotify(_state.Report ?? "请选择实验，然后恢复初始状态。");
        }
        private void AddButton(VisualElement parent, string text, Action action)
        {
            parent.Add(new Button(() =>
            {
                try
                {
                    if (parent == _writes && (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode))
                        throw new InvalidOperationException("编译、导入或 Play Mode 期间禁止写入实验资产。");
                    action();
                }
                catch (Exception ex) { _state.Report = ex.Message; _state.Save(); Render(); Debug.LogException(ex); }
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
