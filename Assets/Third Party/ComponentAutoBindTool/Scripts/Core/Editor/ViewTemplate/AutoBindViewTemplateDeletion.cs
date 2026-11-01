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
        [SerializeField] private AutoBindTemplateDeletionPhase m_Phase;
        [SerializeField] private string m_Message, m_BackupPath;
        [SerializeField] private bool m_FilesDeleted, m_CompilationFinished, m_Reloaded;
        [SerializeField] private List<string> m_Errors = new();
        internal bool IsBusy => m_Phase == AutoBindTemplateDeletionPhase.WaitingForCompilation;
        internal AutoBindTemplateDeletionPhase Phase => m_Phase;
        internal string Message => m_Message;
        internal string BackupPath => m_BackupPath;

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
            m_FilesDeleted = false;
            PrepareCompilation();
            AssetDatabase.DisallowAutoRefresh();
            AssetDatabase.StartAssetEditing();
            try
            {
                AutoBindTemplateAssetDeletion.Delete(snapshot, out m_BackupPath);
                m_FilesDeleted = true;
                AutoBindViewTemplateCreation.instance.InvalidateDeletedOutputs(snapshot.ExistingAssets);
                m_Message = "磁盘产物已删除，等待 Unity 完成编译/重载。场景实例未清除。";
                if (!snapshot.DeletesScripts) Complete();
            }
            catch (Exception error)
            {
                m_Phase = AutoBindTemplateDeletionPhase.Failed;
                m_Message = error.Message;
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
            m_Phase = AutoBindTemplateDeletionPhase.WaitingForCompilation;
            m_CompilationFinished = false;
            m_Reloaded = false;
            m_Errors.Clear();
        }

        internal void CompilationStarted()
        {
            if (!IsBusy) return;
            m_CompilationFinished = false;
            m_Errors.Clear();
        }

        internal void AssemblyFinished(CompilerMessage[] messages)
        {
            if (!IsBusy) return;
            foreach (var message in messages)
                if (message.type == CompilerMessageType.Error) m_Errors.Add(message.file + ":" + message.line + " " + message.message);
        }

        internal void CompilationFinished()
        {
            if (!IsBusy) return;
            m_CompilationFinished = true;
            if (m_Errors.Count == 0) return;
            ReportCompileErrors();
        }

        private void ReportCompileErrors()
        {
            m_Phase = AutoBindTemplateDeletionPhase.CompletedWithCompileErrors;
            m_Message = "磁盘删除已完成，但项目存在编译错误。请修复相关引用；文件不会自动恢复。备份：" + m_BackupPath + "\n" + (m_Errors.Count > 0 ? string.Join("\n", m_Errors.Take(8)) : "请查看 Console 中已有的编译错误。");
        }

        internal void Reloaded() { if (IsBusy) m_Reloaded = true; }
        internal void Tick()
        {
            if (IsBusy && m_FilesDeleted && !EditorApplication.isCompiling && !EditorApplication.isUpdating && (m_CompilationFinished || m_Reloaded)) Complete();
        }

        private void Complete()
        {
            if (EditorUtility.scriptCompilationFailed) { ReportCompileErrors(); return; }
            m_Phase = AutoBindTemplateDeletionPhase.Completed;
            m_Message = "磁盘删除完成。场景实例未清除，场景未保存；可按需调整 Args 后重新创建。备份：" + m_BackupPath;
        }

        internal void RetryCompilation()
        {
            if (m_Phase != AutoBindTemplateDeletionPhase.CompletedWithCompileErrors) return;
            string blocked = BlockReason();
            if (blocked != null) throw new InvalidOperationException(blocked);
            if (!EditorUtility.scriptCompilationFailed) { Complete(); return; }
            PrepareCompilation();
            m_Message = "仅重新检测编译，不会重复删除文件……";
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
