using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace PsdUiWorkbench.AiNaming
{
    [CreateAssetMenu(fileName = "PsdAiNamingSettings", menuName = "PsdUiWorkbench/AI 命名设置")]
    public sealed class PsdAiNamingSettings : ScriptableObject
    {
        public const string DefaultAssetPath = "Assets/PsdUiWorkbench/Configs/AI/PsdAiNamingSettings.asset";
        [SerializeField, InspectorName("供应商")] private PsdAiProvider _provider;
        [SerializeField, InspectorName("智谱模型")] private string _zhipuModel = "glm-4.6v";
        [SerializeField, InspectorName("智谱推理")] private PsdAiReasoningEffort _zhipuReasoningEffort;
        [SerializeField, InspectorName("模型")] private string _model = "deepseek-flash";
        [SerializeField, InspectorName("推理强度")] private PsdAiReasoningEffort _reasoningEffort;
        [SerializeField, InspectorName("超时秒数"), Range(10, 600)] private int _timeoutSeconds = 120;
        [SerializeField, InspectorName("命名规则"), TextArea(3, 8)] private string _rules = "";
        public PsdAiProvider Provider { get => _provider; set => _provider = value; }
        public string Model
        {
            get => _provider == PsdAiProvider.Zhipu ? _zhipuModel : _model;
            set { if (_provider == PsdAiProvider.Zhipu) _zhipuModel = value; else _model = value; }
        }
        public PsdAiReasoningEffort ReasoningEffort
        {
            get => _provider == PsdAiProvider.Zhipu ? _zhipuReasoningEffort : _reasoningEffort;
            set { if (_provider == PsdAiProvider.Zhipu) _zhipuReasoningEffort = value; else _reasoningEffort = value; }
        }
        public int TimeoutSeconds { get => _timeoutSeconds; set => _timeoutSeconds = value; }
        public string Rules { get => _rules; set => _rules = value; }
        public PsdAiRequestSettings Snapshot() => new PsdAiRequestSettings(_provider, Model, ReasoningEffort, _timeoutSeconds, _rules);
        public static PsdAiNamingSettings LoadDefault() => AssetDatabase.LoadAssetAtPath<PsdAiNamingSettings>(DefaultAssetPath);
        public static PsdAiNamingSettings CreateDefault()
        {
            // 孤立 meta 或同名目录不能通过 AssetDatabase 类型检查发现，先拒绝这些占用。
            if (Directory.Exists(DefaultAssetPath) || Directory.Exists(DefaultAssetPath + ".meta") ||
                (!File.Exists(DefaultAssetPath) && File.Exists(DefaultAssetPath + ".meta")))
                throw new PsdAiNamingException("默认设置路径或其 meta 已被占用。");
            var existing = LoadDefault();
            if (existing != null) return existing;
            RequireVacantTarget();
            var createdFolders = new List<string>();
            PsdAiNamingSettings settings = null;
            bool ownsTarget = false;
            try
            {
                string folder = "Assets";
                foreach (string part in new[] { "PsdUiWorkbench", "Configs", "AI" })
                {
                    string child = folder + "/" + part;
                    if (!AssetDatabase.IsValidFolder(child))
                    {
                        if (File.Exists(child) || Directory.Exists(child) || File.Exists(child + ".meta") || Directory.Exists(child + ".meta"))
                            throw new PsdAiNamingException("默认设置目录被未导入文件或目录占用。");
                        createdFolders.Add(child);
                        if (string.IsNullOrEmpty(AssetDatabase.CreateFolder(folder, part)))
                            throw new PsdAiNamingException("无法创建默认设置目录。");
                    }
                    folder = child;
                }
                settings = CreateInstance<PsdAiNamingSettings>();
                // 仅在写入前再次确认目标及 meta 都不存在，后续清理只拥有本次新建路径。
                RequireVacantTarget();
                ownsTarget = true;
                AssetDatabase.CreateAsset(settings, DefaultAssetPath);
                AssetDatabase.SaveAssetIfDirty(settings);
                return settings;
            }
            catch (Exception)
            {
                if (ownsTarget)
                {
                    try
                    {
                        if (settings != null && AssetDatabase.GetAssetPath(settings) == DefaultAssetPath)
                            AssetDatabase.DeleteAsset(DefaultAssetPath);
                        if (File.Exists(DefaultAssetPath)) File.Delete(DefaultAssetPath);
                        if (File.Exists(DefaultAssetPath + ".meta")) File.Delete(DefaultAssetPath + ".meta");
                    }
                    catch (Exception) { /* 不触碰其他资产；清理失败也不泄漏底层异常信息。 */ }
                }
                if (settings != null)
                {
                    try { UnityEngine.Object.DestroyImmediate(settings, ownsTarget); }
                    catch (Exception) { }
                }
                for (int i = createdFolders.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        string folder = createdFolders[i];
                        if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                        {
                            AssetDatabase.DeleteAsset(folder);
                            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
                        }
                        if (!Directory.Exists(folder) && File.Exists(folder + ".meta")) File.Delete(folder + ".meta");
                    }
                    catch (Exception) { }
                }
                throw new PsdAiNamingException("无法创建默认设置，请检查路径占用和写入权限。");
            }
        }
        private static void RequireVacantTarget()
        {
            if (File.Exists(DefaultAssetPath) || Directory.Exists(DefaultAssetPath) ||
                File.Exists(DefaultAssetPath + ".meta") || Directory.Exists(DefaultAssetPath + ".meta") ||
                AssetDatabase.LoadMainAssetAtPath(DefaultAssetPath) != null)
                throw new PsdAiNamingException("默认设置路径或其 meta 已被其他内容占用。");
        }
    }

    public enum PsdAiCredentialSource { None, Local, Environment }

    public static class PsdAiCredentials
    {
        private const string LocalKey = "PsdUiWorkbench.AiNaming.LocalApiKey.v1";
        private const string ZhipuLocalKey = "PsdUiWorkbench.AiNaming.Zhipu.LocalApiKey.v1";
        public static PsdAiCredentialSource Source => GetSource(PsdAiProvider.DeepSeek);
        public static PsdAiCredentialSource GetSource(PsdAiProvider provider) => ResolveSource(GetLocalKey(provider),
            Environment.GetEnvironmentVariable(EnvironmentVariable(provider)));
        public static string EnvironmentVariable(PsdAiProvider provider)
        {
            switch (provider)
            {
                case PsdAiProvider.DeepSeek: return "DEEPSEEK_API_KEY";
                case PsdAiProvider.Zhipu: return "ZHIPU_API_KEY";
                default: throw new PsdAiNamingException("供应商无效。");
            }
        }
        private static string PreferenceKey(PsdAiProvider provider)
        {
            switch (provider)
            {
                case PsdAiProvider.DeepSeek: return LocalKey;
                case PsdAiProvider.Zhipu: return ZhipuLocalKey;
                default: throw new PsdAiNamingException("供应商无效。");
            }
        }
        public static string GetLocalKey() => GetLocalKey(PsdAiProvider.DeepSeek);
        public static string GetLocalKey(PsdAiProvider provider) => EditorPrefs.GetString(PreferenceKey(provider), "");
        public static void SetLocalKey(string value) => SetLocalKey(PsdAiProvider.DeepSeek, value);
        public static void SetLocalKey(PsdAiProvider provider, string value)
        {
            string key = PreferenceKey(provider);
            if (string.IsNullOrWhiteSpace(value)) EditorPrefs.DeleteKey(key);
            else EditorPrefs.SetString(key, value.Trim());
        }
        public static string ResolveKey() => ResolveKey(PsdAiProvider.DeepSeek);
        public static string ResolveKey(PsdAiProvider provider)
        {
            string local = GetLocalKey(provider);
            string environment = Environment.GetEnvironmentVariable(EnvironmentVariable(provider));
            return (ResolveSource(local, environment) == PsdAiCredentialSource.Local ? local : environment)?.Trim() ?? "";
        }

        private static PsdAiCredentialSource ResolveSource(string local, string environment)
        {
            if (!string.IsNullOrWhiteSpace(local)) return PsdAiCredentialSource.Local;
            return string.IsNullOrWhiteSpace(environment) ? PsdAiCredentialSource.None : PsdAiCredentialSource.Environment;
        }
    }
}
