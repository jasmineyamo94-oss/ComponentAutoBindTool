using System;
using System.IO;
using System.Text.RegularExpressions;
using ComponentAutoBindTool.Example.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace ComponentAutoBindTool.Tests
{
    public class PipelineLabTests
    {
        [SetUp]
        public void SetUp()
        {
            EditorSceneManager.OpenScene(AutoBindExampleSetup.ScenePath);
            AssetDatabase.DisallowAutoRefresh();
            EditorApplication.LockReloadAssemblies();
        }
        [TearDown]
        public void TearDown()
        {
            var scene = SceneManager.GetSceneByPath(PipelineLabExperiment.ScenePath);
            if (scene.IsValid() && scene.isLoaded) EditorSceneManager.CloseScene(scene, true);
            if (Directory.Exists(PipelineLabExperiment.Workspace))
            {
                foreach (string file in Directory.GetFiles(PipelineLabExperiment.Workspace, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                AssetDatabase.DeleteAsset(PipelineLabExperiment.Workspace);
            }
            AssetDatabase.AllowAutoRefresh();
            EditorApplication.UnlockReloadAssemblies();
        }
        [TestCase(PipelineLabCase.FirstGeneration)]
        [TestCase(PipelineLabCase.RenameThenGenerate)]
        [TestCase(PipelineLabCase.NestedWidgetAndTMP)]
        [TestCase(PipelineLabCase.NoOpGeneration)]
        [TestCase(PipelineLabCase.SceneSave)]
        [TestCase(PipelineLabCase.PrefabSave)]
        [TestCase(PipelineLabCase.InstanceOverride)]
        public void LabUsesRealInspectorPipelineAndPersistsReport(PipelineLabCase experiment)
        {
            var state = PipelineLabExperiment.Prepare(experiment);
            var report = PipelineLabExperiment.Execute(state, true);
            Assert.That(report.Succeeded, Is.True, state.Report);
            Assert.That(File.Exists(state.GeneratedPath), Is.True);
            var restored = PipelineLabState.Load();
            Assert.That(restored.TargetId, Is.EqualTo(state.TargetId));
            Assert.That(restored.Report, Is.EqualTo(state.Report));
            Assert.That(PipelineLabExperiment.Resolve(restored), Is.Not.Null);
            if (experiment == PipelineLabCase.NoOpGeneration)
            {
                Assert.That(state.CodeChanged, Is.False);
                Assert.That(state.AfterTime, Is.EqualTo(state.BeforeTime));
            }
            if (experiment == PipelineLabCase.RenameThenGenerate)
                Assert.That(File.ReadAllText(state.GeneratedPath), Does.Contain("btnRenamed"));
            if (experiment == PipelineLabCase.NestedWidgetAndTMP)
                Assert.That(PipelineLabExperiment.Resolve(state).BindDatas.Count, Is.EqualTo(2));
            if (experiment == PipelineLabCase.InstanceOverride)
            {
                Assert.That(state.Report, Does.Contain("源 Prefab 保持不变：True"));
                var current = SceneManager.GetSceneByPath(PipelineLabExperiment.ScenePath);
                EditorSceneManager.CloseScene(current, true);
                EditorSceneManager.OpenScene(PipelineLabExperiment.ScenePath, OpenSceneMode.Additive);
                var loaded = PipelineLabExperiment.Resolve(state);
                Assert.That(loaded.transform.GetChild(0).name, Is.EqualTo("Btn_Override"));
                Assert.That(loaded.BindDatas[0].Name, Is.EqualTo("Btn_Override"));
            }
        }
        [TestCase(PipelineLabCase.UnknownPrefix)]
        [TestCase(PipelineLabCase.MissingComponent)]
        [TestCase(PipelineLabCase.DuplicateField)]
        [TestCase(PipelineLabCase.InvalidName)]
        [TestCase(PipelineLabCase.InvalidPath)]
        [TestCase(PipelineLabCase.Rollback)]
        public void FailureReportMatchesConsoleAndPreservesPriorState(PipelineLabCase experiment)
        {
            var state = PipelineLabExperiment.Prepare(experiment);
            LogAssert.Expect(UnityEngine.LogType.Error, new Regex("操作失败"));
            var attributes = File.Exists(PipelineLabExperiment.CodePath) ? File.GetAttributes(PipelineLabExperiment.CodePath) : FileAttributes.Normal;
            var report = PipelineLabExperiment.Execute(state, true);
            Assert.That(report.Succeeded, Is.False);
            Assert.That(state.ComponentRestored && state.CodeRestored && state.AssetRestored, Is.True, state.Report);
            if (experiment == PipelineLabCase.Rollback)
            {
                Assert.That(state.Report, Does.Contain("既有编辑保留：True"));
                Assert.That(File.GetAttributes(PipelineLabExperiment.CodePath).HasFlag(FileAttributes.ReadOnly), Is.False);
                Assert.That(File.GetAttributes(PipelineLabExperiment.CodePath), Is.EqualTo(attributes));
            }
        }
    }
}
