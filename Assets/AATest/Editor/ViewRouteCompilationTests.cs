using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace ComponentAutoBindTool.Tests
{
    public sealed class ViewRouteCompilationTests
    {
        [Test]
        public void CorrectCallsCompileAndWrongArgsFailForInterfaceAndConcreteManager()
        {
            string directory = Path.Combine(Path.GetTempPath(), "ComponentAutoBindTool.RouteCompilation", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string project = Directory.GetCurrentDirectory();
            string response = Directory.GetFiles("Library/Bee/artifacts", "Assembly-CSharp-Editor.rsp", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc).First();
            string[] references = File.ReadAllLines(response).Where(line => line.StartsWith("-r:", StringComparison.Ordinal)).ToArray();
            Assert.That(references.Length, Is.GreaterThan(0), "应使用 Unity 的真实程序集引用。");

            string prefix = "using ComponentAutoBindTool.ViewCore; using ComponentAutoBindTool.Tests; " +
                "public static class RouteCompileProbe { public static void Run(IViewManager manager, ViewManager concrete) { " +
                "var route = new ViewRoute<RouteViewHost, RouteArgs>(\"explicit-key\"); ";
            string suffix = " } }";
            string correct = prefix +
                "manager.ShowView(route, new RouteArgs()); concrete.ShowView(route, new RouteArgs()); " +
                "manager.EnqueueView(route, new RouteArgs()); manager.ReplaceTopView(route, new RouteArgs()); manager.CloseView(route);" +
                "manager.ShowViewAsync(route, new RouteArgs()); concrete.ShowViewAsync(route, new RouteArgs()); " +
                "manager.ReplaceTopViewAsync(route, new RouteArgs()); concrete.ReplaceTopViewAsync(route, new RouteArgs());" + suffix;
            var positive = Compile("correct", correct);
            Assert.That(positive.exitCode, Is.Zero, positive.output);
            foreach (string receiver in new[] { "manager", "concrete" })
            {
                foreach (string method in new[] { "ShowView", "ShowViewAsync", "ReplaceTopViewAsync" })
                {
                    var negative = Compile(receiver + method, prefix + receiver + "." + method + "(route, new OtherRouteArgs());" + suffix);
                    Assert.That(negative.exitCode, Is.Not.Zero, "错误参数应由真实 C# 编译器拒绝。");
                    Assert.That(Regex.IsMatch(negative.output, @"error CS(0411|1503)\b"), Is.True, negative.output);
                    TestContext.WriteLine(receiver + ": " + negative.output);
                }
            }
            TestContext.WriteLine("编译产物：" + directory);

            (int exitCode, string output) Compile(string name, string source)
            {
                string sourcePath = Path.Combine(directory, name + ".cs");
                string responsePath = Path.Combine(directory, name + ".rsp");
                File.WriteAllText(sourcePath, source, new UTF8Encoding(false));
                var options = references.Concat(new[] { "-target:library", "-nostdlib+", "-langversion:9.0", "-nologo",
                    "-out:\"" + Path.Combine(directory, name + ".dll") + "\"", "\"" + sourcePath + "\"" });
                File.WriteAllLines(responsePath, options, new UTF8Encoding(false));
                string data = EditorApplication.applicationContentsPath;
                string executable = Path.Combine(data, "NetCoreRuntime", Application.platform == RuntimePlatform.WindowsEditor ? "dotnet.exe" : "dotnet");
                string compiler = Path.Combine(data, "DotNetSdkRoslyn", "csc.dll");
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = "\"" + compiler + "\" @\"" + responsePath + "\"",
                    WorkingDirectory = project,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(30000))
                {
                    process.Kill();
                    Assert.Fail("Unity C# 编译器超时。");
                }
                string output = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
                File.WriteAllText(Path.Combine(directory, name + ".txt"), output, new UTF8Encoding(false));
                return (process.ExitCode, output);
            }
        }
    }
}
