using System;
using System.IO;
using UnityEngine;

namespace ComponentAutoBindTool.Editor
{
    internal static class AutoBindPathUtility
    {
        private static string AssetsPath => Path.GetFullPath(Application.dataPath).Replace('\\', '/').TrimEnd('/');
        private static StringComparison PathComparison => Application.platform == RuntimePlatform.WindowsEditor
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        public static string GetResolvedCodePath(AutoBindGlobalSetting setting, Core.ComponentAutoBindTool target)
        {
            if (setting == null || target == null) return string.Empty;
            return NormalizeCodePath(setting.UseGlobalDefaultSavePath ? target.CodePath : setting.CodePath);
        }

        public static string GetInspectorCodePath(AutoBindGlobalSetting setting, string scriptAssetPath)
        {
            if (setting == null) return string.Empty;
            if (!setting.UseGlobalDefaultSavePath) return ToAssetRelativePath(setting.CodePath);
            return string.IsNullOrEmpty(scriptAssetPath)
                ? string.Empty : Path.GetDirectoryName(scriptAssetPath)?.Replace('\\', '/');
        }

        public static string NormalizeCodePath(string codePath)
        {
            if (string.IsNullOrWhiteSpace(codePath)) return string.Empty;
            string path = codePath.Trim().Replace('\\', '/');
            // 兼容原配置中以 / 开头、相对于 Assets 的路径。
            if (path.StartsWith("/") && !path.StartsWith("//") &&
                Application.platform == RuntimePlatform.WindowsEditor)
            {
                path = path.TrimStart('/');
            }
            if (path == "Assets" || path.StartsWith("Assets/", StringComparison.Ordinal))
            {
                path = Path.Combine(Path.GetDirectoryName(AssetsPath), path);
            }
            else if (!Path.IsPathRooted(path))
            {
                path = Path.Combine(AssetsPath, path);
            }
            return Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/');
        }

        public static bool IsPathUnderAssets(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string fullPath = NormalizeCodePath(path);
            return string.Equals(fullPath, AssetsPath, PathComparison) ||
                fullPath.StartsWith(AssetsPath + "/", PathComparison);
        }

        public static string ToAssetRelativePath(string path)
        {
            string fullPath = NormalizeCodePath(path);
            return IsPathUnderAssets(fullPath) ? "Assets" + fullPath.Substring(AssetsPath.Length) : fullPath;
        }
    }
}
