using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace ComponentAutoBindTool.Editor
{
    internal enum AutoBindTemplateDeletionPhase { None, WaitingForCompilation, Completed, CompletedWithCompileErrors, Failed }

    internal sealed class AutoBindViewTemplateDeletion : ScriptableSingleton<AutoBindViewTemplateDeletion>
    {
        [SerializeField] private AutoBindTemplateDeletionPhase _phase;
        [SerializeField] private string _message;
        [SerializeField] private string _backupPath;
        [SerializeField] private bool _filesDeleted;
        [SerializeField] private bool _compilationFinished;
        [SerializeField] private bool _reloaded;
        [SerializeField] private List<string> _errors = new();
        internal bool IsBusy => _phase == AutoBindTemplateDeletionPhase.WaitingForCompilation;
        internal AutoBindTemplateDeletionPhase Phase => _phase;
        internal string Message => _message;
        internal string BackupPath => _backupPath;

        internal static string BlockReason()
        {
            if (AutoBindViewTemplateCreation.instance.HasPending) return "请先完成或取消当前创建/清除任务，再按位置删除。";
            return AutoBindViewTemplateCreation.BlockReason(true);
        }

        internal static AutoBindTemplateAssetDeletion.Snapshot Locate(string name, string scriptDirectory, string prefabPath)
        {
            string basis = AutoBindViewTemplateDefinition.ValidateBaseName(name);
            string folder = AutoBindViewTemplateDefinition.NormalizeFolder(scriptDirectory);
            string prefab = AutoBindTemplateAssetDeletion.NormalizeFile(prefabPath);
            if (Path.GetFileName(prefab) != basis + "View.prefab") throw new ArgumentException("Prefab 文件名必须为 " + basis + "View.prefab。");
            var definition = new AutoBindViewTemplateDefinition { BaseName = basis, ScriptDirectory = folder, PrefabPath = prefab };
            // 仅允许移除同名界面的空叶目录，不移除共享 Prefab 根目录。
            bool leaf = Path.GetFileName(folder) == basis && !string.Equals(folder, Path.GetDirectoryName(prefab)?.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
            var settings = AutoBindViewTemplateSettings.instance;
            if (string.Equals(folder, settings.PrefabRoot?.Replace('\\', '/').TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(folder, settings.ScriptRoot?.Replace('\\', '/').TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) leaf = false;
            return AutoBindTemplateAssetDeletion.Capture(definition.OutputPaths, leaf ? folder : null);
        }

        internal void Begin(AutoBindTemplateAssetDeletion.Snapshot snapshot)
        {
            string blocked = BlockReason();
            if (blocked != null) throw new InvalidOperationException(blocked);
            snapshot.AssertUnchanged();
            _filesDeleted = false;
            PrepareCompilation();
            AssetDatabase.DisallowAutoRefresh();
            AssetDatabase.StartAssetEditing();
            try
            {
                AutoBindTemplateAssetDeletion.Delete(snapshot, out _backupPath);
                _filesDeleted = true;
                AutoBindViewTemplateCreation.instance.InvalidateDeletedOutputs(snapshot.ExistingAssets);
                _message = "磁盘产物已删除，等待 Unity 完成编译/重载。场景实例未清除。";
                if (!snapshot.DeletesScripts) Complete();
            }
            catch (Exception error)
            {
                _phase = AutoBindTemplateDeletionPhase.Failed;
                _message = error.Message;
                throw;
            }
            finally
            {
                try { AssetDatabase.StopAssetEditing(); }
                finally { AssetDatabase.AllowAutoRefresh(); }
            }
            AssetDatabase.Refresh();
            if (snapshot.DeletesScripts) CompilationPipeline.RequestScriptCompilation();
        }

        private void PrepareCompilation()
        {
            _phase = AutoBindTemplateDeletionPhase.WaitingForCompilation;
            _compilationFinished = false;
            _reloaded = false;
            _errors.Clear();
        }

        internal void CompilationStarted()
        {
            if (!IsBusy) return;
            _compilationFinished = false;
            _errors.Clear();
        }

        internal void AssemblyFinished(CompilerMessage[] messages)
        {
            if (!IsBusy) return;
            foreach (var message in messages)
                if (message.type == CompilerMessageType.Error) _errors.Add(message.file + ":" + message.line + " " + message.message);
        }

        internal void CompilationFinished()
        {
            if (!IsBusy) return;
            _compilationFinished = true;
            if (_errors.Count == 0) return;
            ReportCompileErrors();
        }

        private void ReportCompileErrors()
        {
            _phase = AutoBindTemplateDeletionPhase.CompletedWithCompileErrors;
            _message = "磁盘删除已完成，但项目存在编译错误。请修复相关引用；文件不会自动恢复。备份：" + _backupPath + "\n" + (_errors.Count > 0 ? string.Join("\n", _errors.Take(8)) : "请查看 Console 中已有的编译错误。");
        }

        internal void Reloaded() { if (IsBusy) _reloaded = true; }
        internal void Tick()
        {
            if (IsBusy && _filesDeleted && !EditorApplication.isCompiling && !EditorApplication.isUpdating && (_compilationFinished || _reloaded)) Complete();
        }

        private void Complete()
        {
            if (EditorUtility.scriptCompilationFailed) { ReportCompileErrors(); return; }
            _phase = AutoBindTemplateDeletionPhase.Completed;
            _message = "磁盘删除完成。场景实例未清除，场景未保存；可按需调整 Args 后重新创建。备份：" + _backupPath;
        }

        internal void RetryCompilation()
        {
            if (_phase != AutoBindTemplateDeletionPhase.CompletedWithCompileErrors) return;
            string blocked = BlockReason();
            if (blocked != null) throw new InvalidOperationException(blocked);
            if (!EditorUtility.scriptCompilationFailed) { Complete(); return; }
            PrepareCompilation();
            _message = "仅重新检测编译，不会重复删除文件……";
            CompilationPipeline.RequestScriptCompilation(RequestScriptCompilationOptions.CleanBuildCache);
        }
    }

    [InitializeOnLoad]
    internal static class AutoBindViewTemplateDeletionRunner
    {
        static AutoBindViewTemplateDeletionRunner()
        {
            var restored = AutoBindViewTemplateDeletion.instance;
            if (restored.IsBusy) EditorApplication.delayCall += restored.Reloaded;
            EditorApplication.update += () => AutoBindViewTemplateDeletion.instance.Tick();
            CompilationPipeline.compilationStarted += _ => AutoBindViewTemplateDeletion.instance.CompilationStarted();
            CompilationPipeline.assemblyCompilationFinished += (_, messages) => AutoBindViewTemplateDeletion.instance.AssemblyFinished(messages);
            CompilationPipeline.compilationFinished += _ => AutoBindViewTemplateDeletion.instance.CompilationFinished();
        }
    }
}
