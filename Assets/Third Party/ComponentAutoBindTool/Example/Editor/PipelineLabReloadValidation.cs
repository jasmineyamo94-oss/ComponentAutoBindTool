using System;
using System.IO;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace ComponentAutoBindTool.Example.Editor
{
    [InitializeOnLoad]
    public static class PipelineLabReloadValidation
    {
        private const string Phase = "AutoBind.ReloadValidation";
        static PipelineLabReloadValidation()
        {
            if (SessionState.GetInt(Phase, 0) == 1) SessionState.SetInt(Phase, 2);
            EditorApplication.update += Finish;
        }
        public static void Run()
        {
            EditorSceneManager.OpenScene(AutoBindExampleSetup.ScenePath);
            var state = PipelineLabExperiment.Prepare(PipelineLabCase.RenameThenGenerate);
            var result = PipelineLabExperiment.Execute(state, true);
            if (!result.Succeeded) throw new InvalidOperationException(state.Report);
            SessionState.SetString(Phase + ".expected", state.Report);
            SessionState.SetInt(Phase, 1);
            CompilationPipeline.RequestScriptCompilation();
        }
        private static void Finish()
        {
            if (SessionState.GetInt(Phase, 0) != 2 || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            SessionState.SetInt(Phase, 0);
            try
            {
                var state = PipelineLabState.Load();
                if (state.Report != SessionState.GetString(Phase + ".expected", "") || PipelineLabExperiment.Resolve(state) == null)
                    throw new InvalidOperationException("Domain Reload 后报告或目标未恢复。");
                var window = EditorWindow.GetWindow<PipelineLabWindow>();
                window.CreateGUI();
                bool found = false;
                window.rootVisualElement.Query<TextField>().ForEach(field => { if (field.value == state.Report) found = true; });
                if (!found) throw new InvalidOperationException("Domain Reload 后 UI Toolkit 面板未恢复报告。");
                var again = PipelineLabExperiment.Execute(state, true);
                if (!again.Succeeded || again.CodeFileUpdated || state.BeforeTime != state.AfterTime)
                    throw new InvalidOperationException("重载后重复生成发生非预期改写。");
                Directory.CreateDirectory("Validation/FrameworkLab");
                File.WriteAllText("Validation/FrameworkLab/domain-reload.txt", "PASS: real assembly reload, target resolution, UI Toolkit report restoration and no-op generation.\n" + state.Report);
                window.Close();
                Debug.Log("PIPELINE_LAB_DOMAIN_RELOAD_PASS");
                EditorApplication.Exit(0);
            }
            catch (Exception ex) { Debug.LogException(ex); EditorApplication.Exit(1); }
        }
    }
}
