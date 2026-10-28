using System;
using System.IO;
using System.Text;
using Third_Party.ComponentAutoBindTool.Scripts.Core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ComponentAutoBindTool.Editor
{
    public sealed class AutoBindPipelineReport
    {
        public bool Succeeded { get; internal set; }
        public AutoBindScanResult ScanResult { get; internal set; }
        public AutoBindValidationResult ValidationResult { get; } = new AutoBindValidationResult();
        public string GeneratedFilePath { get; internal set; }
        public bool BindingsUpdated { get; internal set; }
        public bool CodeFileUpdated { get; internal set; }
    }

    public static class AutoBindBuildPipeline
    {
        public static AutoBindPipelineReport RescanAndValidate(Core.ComponentAutoBindTool target,
            AutoBindGlobalSetting setting, AutoBindKeyMapSetting keyMap)
        {
            return Execute(target, setting, keyMap, false);
        }

        public static AutoBindPipelineReport GenerateCode(Core.ComponentAutoBindTool target,
            AutoBindGlobalSetting setting, AutoBindKeyMapSetting keyMap)
        {
            return Execute(target, setting, keyMap, true);
        }

        private static AutoBindPipelineReport Execute(Core.ComponentAutoBindTool target,
            AutoBindGlobalSetting setting, AutoBindKeyMapSetting keyMap, bool generate)
        {
            var report = new AutoBindPipelineReport();
            try
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode)
                    report.ValidationResult.AddError("请退出运行模式后再生成绑定。");
                if (target == null) report.ValidationResult.AddError("AutoBind 目标为空。");
                if (setting == null) report.ValidationResult.AddError("AutoBindGlobalSetting 为空。");
                report.ScanResult = AutoBindHierarchyScanner.CollectBindings(target, keyMap);
                foreach (string error in report.ScanResult.Errors)
                    report.ValidationResult.AddError("[扫描] " + error);
                if (target == null || setting == null) return report;

                var input = new AutoBindBuildInput(target, setting, report.ScanResult.BindDatas);
                AutoBindValidator.Validate(input, report.ValidationResult);
                if (!report.ValidationResult.IsValid) return report;
                if (generate)
                {
                    string source = AutoBindCodeGenerator.BuildCode(input);
                    using (var transaction = new AutoBindGenerationTransaction(target, input.OutputPath, source))
                    {
                        transaction.Commit(input, report);
                    }
                    report.GeneratedFilePath = input.OutputPath;
                }
                else
                {
                    Undo.IncrementCurrentGroup();
                    int group = Undo.GetCurrentGroup();
                    Undo.SetCurrentGroupName("重扫 AutoBind 绑定");
                    try
                    {
                        report.BindingsUpdated = AutoBindSerializedBindingUtility.ApplyCandidate(input);
                        if (report.BindingsUpdated && PrefabUtility.IsPartOfPrefabInstance(target))
                            PrefabUtility.RecordPrefabInstancePropertyModifications(target);
                        Undo.CollapseUndoOperations(group);
                    }
                    catch
                    {
                        Undo.RevertAllDownToGroup(group);
                        throw;
                    }
                }
                report.Succeeded = true;
            }
            catch (Exception ex)
            {
                report.BindingsUpdated = false;
                report.CodeFileUpdated = false;
                report.ValidationResult.AddError("AutoBind 操作失败：" + ex.Message);
            }
            return report;
        }
    }
}
