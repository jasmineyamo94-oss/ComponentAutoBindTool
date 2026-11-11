using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AAGame.Editor.EnvironmentSetup
{
    [Serializable]
    public sealed class ToolStatus
    {
        public string Name;
        public string Path;
        public string Version;
        public string Error;
        public string Details;
        public string[] Candidates = Array.Empty<string>();
        public bool Available => !string.IsNullOrEmpty(Version) && string.IsNullOrEmpty(Error);
    }

    public static class EnvironmentProbe
    {
        public static string RefreshedPath()
        {
            var paths = new[]
            {
                System.Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "",
                System.Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "",
                System.Environment.GetEnvironmentVariable("PATH") ?? "",
                System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "npm"),
                System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles), "nodejs")
            };
            return string.Join(";", paths.SelectMany(p => p.Split(';')).Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => System.Environment.ExpandEnvironmentVariables(p.Trim().Trim('"'))).Distinct(StringComparer.OrdinalIgnoreCase));
        }

        public static string[] FindExecutables(string name)
        {
            return RefreshedPath().Split(';').SelectMany(folder => new[] { name + ".exe", name + ".cmd" }
                .Select(file => System.IO.Path.Combine(folder.Trim().Trim('"'), file)))
                .Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        public static Task<ToolStatus[]> ScanAsync()
        {
            return Task.Run(async () =>
            {
                var results = new List<ToolStatus>();
                foreach (var name in new[] { "node", "npm", "openupm", "winget" })
                {
                    var candidates = FindExecutables(name);
                    var result = new ToolStatus { Name = name, Candidates = candidates, Path = candidates.FirstOrDefault() ?? "" };
                    if (name == "node")
                    {
                        result.Details = string.Join("；", new[] { "NVM_HOME", "NVM_SYMLINK", "VOLTA_HOME", "FNM_DIR" }
                            .Where(key => !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable(key)))
                            .Select(key => "检测到版本管理环境 " + key + "=" + System.Environment.GetEnvironmentVariable(key)));
                    }
                    if (candidates.Length == 0)
                    {
                        result.Error = "未找到可执行文件";
                    }
                    else
                    {
                        try
                        {
                            var output = await RunAsync(result.Path, "--version", 15000);
                            var match = Regex.Match(output, @"(?m)^v?\d+\.\d+\.\d+(?:[-+][\w.-]+)?\s*$");
                            result.Version = match.Success ? match.Value.Trim() : "";
                            if (result.Version.Length == 0) result.Error = "版本命令未返回有效版本：" + output.Trim();
                        }
                        catch (Exception exception) { result.Error = "版本检测失败：" + exception.Message; }
                    }
                    results.Add(result);
                }
                return results.ToArray();
            });
        }

        public static async Task<string> RunAsync(string executable, string arguments, int timeoutMilliseconds)
        {
            var isCommand = executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
            var start = new ProcessStartInfo
            {
                FileName = isCommand ? System.Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe" : executable,
                Arguments = isCommand ? "/d /s /c \"\"" + executable + "\" " + arguments + "\"" : arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.EnvironmentVariables["PATH"] = RefreshedPath();
            using var process = Process.Start(start);
            if (process == null) throw new IOException("无法启动检测进程");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            var exited = await Task.Run(() => process.WaitForExit(timeoutMilliseconds));
            if (!exited)
            {
                process.Kill();
                throw new TimeoutException("版本检测超时");
            }
            var output = await stdout;
            var error = await stderr;
            if (process.ExitCode != 0) throw new IOException("退出码 " + process.ExitCode + "：" + error);
            return output;
        }
    }
}
