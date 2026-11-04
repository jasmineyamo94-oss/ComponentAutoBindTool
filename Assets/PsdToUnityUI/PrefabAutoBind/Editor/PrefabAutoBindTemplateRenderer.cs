using System;
using System.Collections.Generic;
using System.Text;

namespace PrefabAutoBind
{
    internal static class PrefabAutoBindTemplateRenderer
    {
        public static string RenderTemplate(string template, IReadOnlyDictionary<string, string> variables)
        {
            if (string.IsNullOrEmpty(template))
            {
                return string.Empty;
            }

            var result = template;
            foreach (var pair in variables)
            {
                result = result.Replace("${" + pair.Key + "}", pair.Value ?? string.Empty);
            }

            return result;
        }

        public static string RenderMultilinePlaceholder(string template, string placeholder, string content)
        {
            if (string.IsNullOrEmpty(template) || string.IsNullOrEmpty(placeholder))
            {
                return template ?? string.Empty;
            }

            var token = "${" + placeholder + "}";
            if (template.IndexOf(token, StringComparison.Ordinal) < 0)
            {
                return template;
            }

            var lineEnding = template.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
            var normalizedTemplate = template.Replace("\r\n", "\n").Replace('\r', '\n');
            var templateHasTrailingLineEnding = normalizedTemplate.EndsWith("\n", StringComparison.Ordinal);
            var templateLines = normalizedTemplate.Split('\n');
            var normalizedContent = (content ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim('\n');
            var contentLines = string.IsNullOrEmpty(normalizedContent)
                ? Array.Empty<string>()
                : normalizedContent.Split('\n');
            var outputLines = new List<string>();

            for (var lineIndex = 0; lineIndex < templateLines.Length; lineIndex++)
            {
                if (templateHasTrailingLineEnding && lineIndex == templateLines.Length - 1)
                {
                    continue;
                }

                var line = templateLines[lineIndex];
                if (!string.Equals(line.Trim(), token, StringComparison.Ordinal))
                {
                    outputLines.Add(line);
                    continue;
                }

                var tokenIndex = line.IndexOf(token, StringComparison.Ordinal);
                var indentation = tokenIndex > 0 ? line.Substring(0, tokenIndex) : string.Empty;
                for (var contentLineIndex = 0; contentLineIndex < contentLines.Length; contentLineIndex++)
                {
                    var contentLine = contentLines[contentLineIndex];
                    outputLines.Add(contentLine.Length == 0 ? string.Empty : indentation + contentLine);
                }
            }

            if (outputLines.Count == 0)
            {
                return string.Empty;
            }

            var result = string.Join(lineEnding, outputLines);
            return templateHasTrailingLineEnding ? result + lineEnding : result;
        }

        public static bool ContainsPlaceholder(string template, string placeholder)
        {
            return !string.IsNullOrEmpty(template) &&
                   !string.IsNullOrEmpty(placeholder) &&
                   template.IndexOf("${" + placeholder + "}", StringComparison.Ordinal) >= 0;
        }

        public static bool IsStandalonePlaceholder(string template, string placeholder)
        {
            if (string.IsNullOrEmpty(template) || string.IsNullOrEmpty(placeholder))
            {
                return false;
            }

            var token = "${" + placeholder + "}";
            var searchIndex = 0;
            var found = false;
            while (searchIndex < template.Length)
            {
                var tokenIndex = template.IndexOf(token, searchIndex, StringComparison.Ordinal);
                if (tokenIndex < 0)
                {
                    break;
                }

                found = true;
                var lineStart = tokenIndex;
                while (lineStart > 0 && template[lineStart - 1] != '\r' && template[lineStart - 1] != '\n')
                {
                    lineStart--;
                }

                var lineEnd = tokenIndex + token.Length;
                while (lineEnd < template.Length && template[lineEnd] != '\r' && template[lineEnd] != '\n')
                {
                    lineEnd++;
                }

                if (!string.IsNullOrWhiteSpace(template.Substring(lineStart, tokenIndex - lineStart)) ||
                    !string.IsNullOrWhiteSpace(template.Substring(tokenIndex + token.Length, lineEnd - tokenIndex - token.Length)))
                {
                    return false;
                }

                searchIndex = tokenIndex + token.Length;
            }

            return found;
        }

        public static string RenderEventHandlerName(
            string template,
            string eventObj,
            string eventName)
        {
            return RenderTemplate(template, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "event_obj", eventObj ?? string.Empty },
                { "event_name", eventName ?? string.Empty },
            });
        }

        public static int GetNodePathDepth(string nodePath)
        {
            if (string.IsNullOrEmpty(nodePath))
            {
                return 0;
            }

            var depth = 1;
            for (var index = 0; index < nodePath.Length; index++)
            {
                if (nodePath[index] == '/')
                {
                    depth++;
                }
            }

            return depth;
        }

        public static bool TryFindNearestBoundAncestor(
            string memberNodePath,
            IReadOnlyList<ExportMemberBinding> boundMembers,
            out ExportMemberBinding ancestor)
        {
            ancestor = null;
            if (string.IsNullOrEmpty(memberNodePath))
            {
                return false;
            }

            var bestDepth = -1;
            for (var index = 0; index < boundMembers.Count; index++)
            {
                var candidate = boundMembers[index];
                if (candidate == null || string.IsNullOrEmpty(candidate.NodePath))
                {
                    continue;
                }

                if (!IsAncestorPath(candidate.NodePath, memberNodePath))
                {
                    continue;
                }

                var depth = GetNodePathDepth(candidate.NodePath);
                if (depth <= bestDepth)
                {
                    continue;
                }

                bestDepth = depth;
                ancestor = candidate;
            }

            return ancestor != null;
        }

        public static string GetRelativePathFromAncestor(string ancestorNodePath, string memberNodePath)
        {
            if (string.IsNullOrEmpty(ancestorNodePath))
            {
                return memberNodePath ?? string.Empty;
            }

            if (string.IsNullOrEmpty(memberNodePath))
            {
                return string.Empty;
            }

            if (string.Equals(ancestorNodePath, memberNodePath, StringComparison.Ordinal))
            {
                return string.Empty;
            }

            var prefix = ancestorNodePath + "/";
            if (memberNodePath.StartsWith(prefix, StringComparison.Ordinal))
            {
                return memberNodePath.Substring(prefix.Length);
            }

            return memberNodePath;
        }

        public static bool ContainsRequiredPlaceholders(string template, params string[] placeholders)
        {
            if (string.IsNullOrWhiteSpace(template))
            {
                return false;
            }

            for (var index = 0; index < placeholders.Length; index++)
            {
                if (template.IndexOf("${" + placeholders[index] + "}", StringComparison.Ordinal) < 0)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsAncestorPath(string ancestorPath, string nodePath)
        {
            if (string.IsNullOrEmpty(ancestorPath))
            {
                return true;
            }

            if (string.IsNullOrEmpty(nodePath))
            {
                return false;
            }

            return string.Equals(ancestorPath, nodePath, StringComparison.Ordinal) ||
                   nodePath.StartsWith(ancestorPath + "/", StringComparison.Ordinal);
        }
    }

    internal sealed class ExportMemberBinding
    {
        public string NodePath;
        public string FieldName;
        public string ComponentTypeName;
        public bool IsChildView;
        public string ChildClassName;
    }
}
