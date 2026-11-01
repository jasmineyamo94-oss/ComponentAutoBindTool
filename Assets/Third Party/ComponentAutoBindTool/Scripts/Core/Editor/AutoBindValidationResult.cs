using System.Collections.Generic;
using System.Text;

namespace ComponentAutoBindTool.Editor
{
    public sealed class AutoBindValidationResult
    {
        private readonly List<string> _errors = new List<string>();
        private readonly List<string> _warnings = new List<string>();

        public bool IsValid => _errors.Count == 0;
        public IReadOnlyList<string> Errors => _errors;
        public IReadOnlyList<string> Warnings => _warnings;
        public int ErrorCount => _errors.Count;
        public int WarningCount => _warnings.Count;

        public void AddError(string message)
        {
            _errors.Add(message);
        }

        public void AddWarning(string message)
        {
            _warnings.Add(message);
        }

        public string BuildReport()
        {
            var builder = new StringBuilder();
            bool hasPreviousSection = false;

            if (_errors.Count > 0)
            {
                builder.AppendLine("错误：");
                AppendMessages(builder, _errors);
                hasPreviousSection = true;
            }

            if (_warnings.Count > 0)
            {
                if (hasPreviousSection)
                {
                    builder.AppendLine();
                }

                builder.AppendLine("警告：");
                AppendMessages(builder, _warnings);
                hasPreviousSection = true;
            }

            if (!hasPreviousSection)
            {
                builder.Append("未发现问题。");
            }

            return builder.ToString();
        }

        private static void AppendMessages(StringBuilder builder, List<string> messages)
        {
            for (int i = 0; i < messages.Count; i++)
            {
                builder.Append("- ").AppendLine(messages[i]);
            }
        }
    }
}
