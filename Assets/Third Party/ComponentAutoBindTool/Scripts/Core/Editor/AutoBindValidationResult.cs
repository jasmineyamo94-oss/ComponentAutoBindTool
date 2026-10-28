using System.Collections.Generic;
using System.Text;

namespace ComponentAutoBindTool.Editor
{
    public sealed class AutoBindValidationResult
    {
        private readonly List<string> m_Errors = new List<string>();
        private readonly List<string> m_Warnings = new List<string>();

        public bool IsValid => m_Errors.Count == 0;
        public IReadOnlyList<string> Errors => m_Errors;
        public IReadOnlyList<string> Warnings => m_Warnings;
        public int ErrorCount => m_Errors.Count;
        public int WarningCount => m_Warnings.Count;

        public void AddError(string message)
        {
            m_Errors.Add(message);
        }

        public void AddWarning(string message)
        {
            m_Warnings.Add(message);
        }

        public string BuildReport()
        {
            var builder = new StringBuilder();
            bool hasPreviousSection = false;

            if (m_Errors.Count > 0)
            {
                builder.AppendLine("错误：");
                AppendMessages(builder, m_Errors);
                hasPreviousSection = true;
            }

            if (m_Warnings.Count > 0)
            {
                if (hasPreviousSection)
                {
                    builder.AppendLine();
                }

                builder.AppendLine("警告：");
                AppendMessages(builder, m_Warnings);
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
