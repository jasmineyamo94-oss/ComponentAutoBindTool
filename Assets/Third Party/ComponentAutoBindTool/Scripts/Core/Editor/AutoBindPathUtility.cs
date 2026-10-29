using System;
using System.Collections.Generic;
using UnityEngine;

namespace ComponentAutoBindTool.Editor
{
    internal static class AutoBindPathUtility
    {
        private static bool WindowsPaths => Application.platform == RuntimePlatform.WindowsEditor;

        public static string GetResolvedCodePath(AutoBindGlobalSetting setting, Core.ComponentAutoBindTool target)
        {
            if (setting == null || target == null) return string.Empty;
            return NormalizeCodePath(setting.UseGlobalDefaultSavePath ? target.CodePath : setting.RawCodePath);
        }

        public static string GetResolvedCodePath(AutoBindGlobalSetting setting, string scriptAssetPath)
        {
            if (setting == null) return string.Empty;
            if (!setting.UseGlobalDefaultSavePath) return NormalizeCodePath(setting.RawCodePath);
            if (string.IsNullOrWhiteSpace(scriptAssetPath)) return string.Empty;

            string scriptPath = CleanPath(scriptAssetPath);
            int separator = scriptPath.LastIndexOf('/');
            if (separator < 0) return string.Empty;
            string directory = scriptPath.Substring(0, separator);
            // MonoScript 路径是项目相对路径；Packages 不能被误加到 Assets 下。
            return NormalizeAbsolutePath(IsAbsolute(directory) ? directory : ProjectPath(Application.dataPath) + "/" + directory, WindowsPaths);
        }

        public static string GetInspectorCodePath(AutoBindGlobalSetting setting, string scriptAssetPath)
        {
            return ToAssetRelativePath(GetResolvedCodePath(setting, scriptAssetPath));
        }

        public static string NormalizeCodePath(string codePath)
        {
            return NormalizeCodePath(codePath, Application.dataPath, WindowsPaths);
        }

        // 只解释配置字符串。绝对路径检查和路径选择器不再进入这条兼容分支。
        internal static string NormalizeCodePath(string codePath, string assetsPath, bool windowsPaths)
        {
            if (string.IsNullOrWhiteSpace(codePath)) return string.Empty;
            string assets = NormalizeAbsolutePath(assetsPath, windowsPaths);
            string path = CleanPath(codePath);
            StringComparison comparison = Comparison(windowsPaths);
            if (HasDrive(path) || path.StartsWith("//", StringComparison.Ordinal))
                return NormalizeAbsolutePath(path, windowsPaths);
            if (string.Equals(path, "Assets", comparison) || path.StartsWith("Assets/", comparison))
                return NormalizeAbsolutePath(assets + path.Substring("Assets".Length), windowsPaths);
            if (path.StartsWith("/", StringComparison.Ordinal))
            {
                string absolute = NormalizeAbsolutePath(path, windowsPaths);
                // 保留项目 Assets 绝对前缀及其近似前缀，再由严格边界检查拒绝 AssetsBackup。
                if (path.StartsWith(assets, comparison) || absolute.StartsWith(assets, comparison)) return absolute;
                path = path.TrimStart('/');
            }
            return NormalizeAbsolutePath(assets + "/" + path, windowsPaths);
        }

        public static bool IsPathUnderAssets(string path)
        {
            return IsPathUnderAssets(path, Application.dataPath, WindowsPaths);
        }

        internal static bool IsPathUnderAssets(string path, string assetsPath, bool windowsPaths)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                return IsWithin(NormalizeAbsolutePath(path, windowsPaths), NormalizeAbsolutePath(assetsPath, windowsPaths), Comparison(windowsPaths));
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        public static string ToAssetRelativePath(string path)
        {
            return ToAssetRelativePath(path, Application.dataPath, WindowsPaths);
        }

        internal static string ToAssetRelativePath(string path, string assetsPath, bool windowsPaths)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            string absolute = NormalizeAbsolutePath(path, windowsPaths);
            string assets = NormalizeAbsolutePath(assetsPath, windowsPaths);
            return IsWithin(absolute, assets, Comparison(windowsPaths)) ? "Assets" + absolute.Substring(assets.Length) : absolute;
        }

        private static bool IsWithin(string path, string assets, StringComparison comparison)
        {
            return string.Equals(path, assets, comparison) || path.StartsWith(assets + "/", comparison);
        }

        private static StringComparison Comparison(bool windowsPaths)
        {
            return windowsPaths ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        }

        private static string ProjectPath(string assetsPath)
        {
            string assets = NormalizeAbsolutePath(assetsPath, WindowsPaths);
            return assets.Substring(0, assets.LastIndexOf('/'));
        }

        private static string CleanPath(string path)
        {
            if (path.IndexOf('\0') >= 0) throw new ArgumentException("路径不能包含空字符。", nameof(path));
            return path.Trim().Replace('\\', '/');
        }

        private static bool HasDrive(string path)
        {
            return path.Length >= 2 && ((path[0] >= 'A' && path[0] <= 'Z') || (path[0] >= 'a' && path[0] <= 'z')) && path[1] == ':';
        }

        private static bool IsAbsolute(string path) => HasDrive(path) || path.StartsWith("/", StringComparison.Ordinal);

        // 词法规范化不依赖执行机器的 Path 规则，供 Windows 上的 Unix 规则测试使用。
        private static string NormalizeAbsolutePath(string input, bool windowsPaths)
        {
            string path = CleanPath(input);
            if (windowsPaths) ValidateWindowsCharacters(path);
            string root;
            string remainder;
            if (HasDrive(path))
            {
                if (path.Length < 3 || path[2] != '/') throw new ArgumentException("盘符路径必须是完整绝对路径。", nameof(input));
                root = path.Substring(0, 3);
                remainder = path.Substring(3);
            }
            else if (path.StartsWith("//", StringComparison.Ordinal))
            {
                string[] unc = path.Substring(2).Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (unc.Length < 2 || unc[0] == "." || unc[0] == ".." || unc[1] == "." || unc[1] == "..")
                    throw new ArgumentException("UNC 路径必须包含服务器和共享名称。", nameof(input));
                root = "//" + unc[0] + "/" + unc[1];
                remainder = string.Join("/", unc, 2, unc.Length - 2);
            }
            else if (path.StartsWith("/", StringComparison.Ordinal))
            {
                root = "/";
                remainder = path.Substring(1);
            }
            else throw new ArgumentException("此入口要求已经解析的绝对路径。", nameof(input));

            var segments = new List<string>();
            foreach (string segment in remainder.Split('/'))
            {
                if (segment.Length == 0 || segment == ".") continue;
                if (segment == "..")
                {
                    if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
                }
                else segments.Add(segment);
            }
            return segments.Count == 0 ? root : root.TrimEnd('/') + "/" + string.Join("/", segments);
        }

        private static void ValidateWindowsCharacters(string path)
        {
            for (int index = 0; index < path.Length; index++)
            {
                char character = path[index];
                bool driveSeparator = character == ':' && index == 1 && HasDrive(path);
                if (character < ' ' || character == '<' || character == '>' || character == '"' ||
                    character == '|' || character == '?' || character == '*' || character == ':' && !driveSeparator)
                    throw new ArgumentException("Windows 路径包含非法字符或备用数据流分隔符。", nameof(path));
            }
        }
    }
}
