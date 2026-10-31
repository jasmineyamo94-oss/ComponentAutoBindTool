using System.Linq;
using ComponentAutoBindTool.Editor;
using NUnit.Framework;

namespace ComponentAutoBindTool.Tests
{
    public class AutoBindLiteralTests
    {
        [TestCase("\"\"\" } class Fake { \"\"\"")]
        [TestCase("\"\"\"\" } \"\"\" class Fake { \"\"\"\"")]
        [TestCase("$\"{new { a = 1, b = \"}\" }}\"")]
        [TestCase("$@\"{new { b = @\"}\" }}\"")]
        [TestCase("$\"\"\"{ \"\"\"} class Fake {\"\"\" }\"\"\"")]
        [TestCase("$$\"\"\"literal { } {{ new { b = \"}\" } }}\"\"\"")]
        public void LiteralsCannotHideTheFollowingHostMember(string literal)
        {
            string source = "namespace ComponentAutoBindTool.Tests { public partial class AutoBindPlainHost { string Text = " +
                literal + "; public int view; } }";
            var declarations = AutoBindSourceInspector.FindDeclarations(source, typeof(AutoBindPlainHost)).ToArray();
            Assert.That(declarations, Has.Length.EqualTo(1));
            Assert.That(declarations[0], Does.Contain("public int view"));
            Assert.That(declarations[0], Does.Not.Contain("Fake"));
        }
    }
}
