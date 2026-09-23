using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RanorexOrangebeardListener.RunContext;

namespace RanorexOrangebeardListener.Tests
{
    [TestClass]
    public class TypeTreeTests
    {
        [TestMethod]
        public void Constructor_SetsTypeNameAndActivity()
        {
            var tree = new TypeTree("suite", "root", null);

            Assert.AreEqual("suite", tree.Type);
            Assert.AreEqual("root", tree.Name);
            Assert.IsNull(tree.RanorexActivity);
            Assert.IsNull(tree.GetParent());
            Assert.AreEqual(0, tree.Children.Count);
        }

        [TestMethod]
        public void Add_CreatesChildLinkedToParent()
        {
            var root = new TypeTree("suite", "root", null);

            var child = root.Add("test", "child", null);

            Assert.AreEqual("test", child.Type);
            Assert.AreEqual("child", child.Name);
            Assert.AreSame(root, child.GetParent());
            Assert.AreEqual(1, root.Children.Count);
            Assert.AreSame(child, root.Children[0]);
        }

        [TestMethod]
        public void Add_MultipleChildren_AreTrackedInInsertionOrder()
        {
            var root = new TypeTree("suite", "root", null);

            var first = root.Add("step", "first", null);
            var second = root.Add("step", "second", null);
            var third = root.Add("step", "third", null);

            Assert.AreEqual(3, root.Children.Count);
            Assert.AreSame(first, root.Children[0]);
            Assert.AreSame(second, root.Children[1]);
            Assert.AreSame(third, root.Children[2]);
        }

        [TestMethod]
        public void GetParent_OnRootNode_ReturnsNull()
        {
            var root = new TypeTree("suite", "root", null);

            Assert.IsNull(root.GetParent());
        }

        [TestMethod]
        public void GetRoot_OnRootItself_ReturnsSelf()
        {
            var root = new TypeTree("suite", "root", null);

            Assert.AreSame(root, root.GetRoot());
        }

        [TestMethod]
        public void GetRoot_FromDeeplyNestedNode_ReturnsTopLevelRoot()
        {
            var root = new TypeTree("suite", "root", null);
            var test = root.Add("test", "myTest", null);
            var step = test.Add("step", "myStep", null);
            var nestedStep = step.Add("step", "nestedStep", null);

            Assert.AreSame(root, nestedStep.GetRoot());
            Assert.AreSame(root, step.GetRoot());
            Assert.AreSame(root, test.GetRoot());
        }

        [TestMethod]
        public void Print_WritesIndentedTreeToFile()
        {
            var root = new TypeTree("suite", "root", null);
            var child = root.Add("test", "child", null);
            child.Add("step", "grandchild", null);

            var folder = Path.Combine(Path.GetTempPath(), "OrangebeardTypeTreeTests_" + System.Guid.NewGuid());
            Directory.CreateDirectory(folder);

            try
            {
                root.Print(folder);

                var files = Directory.GetFiles(folder, "*tree.log");
                Assert.AreEqual(1, files.Length);

                var content = File.ReadAllText(files[0]);
                StringAssert.Contains(content, "suite root");
                StringAssert.Contains(content, "test child");
                StringAssert.Contains(content, "step grandchild");
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }
    }
}
