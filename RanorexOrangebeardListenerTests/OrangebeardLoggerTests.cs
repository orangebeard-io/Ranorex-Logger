using Microsoft.VisualStudio.TestTools.UnitTesting;
using RanorexOrangebeardListener;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RanorexOrangebeardListener.Tests
{
    [TestClass()]
    public class OrangebeardLoggerTests
    {
        [TestMethod()]
        [DataRow(@"[{""componentName"": ""myComponent1"", ""componentVersion"":""myVersion1""}, {""componentName"": ""myComponent2"", ""componentVersion"":""myVersion2""}]")]
        public void ParseJsonTest_normalInput(string json)
        {
            List<ChangedComponent> parsedJson = OrangebeardLogger.ParseJson(json);
            Assert.AreEqual(2, parsedJson.Count);

            var expectedFirstElement = new ChangedComponent("myComponent1", "myVersion1");
            Assert.AreEqual(expectedFirstElement, parsedJson[0]);

            var expectedSecondElement = new ChangedComponent("myComponent2", "myVersion2");
            Assert.AreEqual(expectedSecondElement, parsedJson[1]);
        }

        [TestMethod()]
        [DataRow(@"[{""componentName"": ""myComponent1""}]")]
        public void ParseJsonTest_noComponentVersion(string json)
        {
            List<ChangedComponent> parsedJson = OrangebeardLogger.ParseJson(json);
            Assert.AreEqual(0, parsedJson.Count);
        }

        [TestMethod()]
        [DataRow(@"[{""componentName"": ""myComponent1"", ""componentVersion"":null}]")]
        public void ParseJsonTest_componentVersionHasValueNull(string json)
        {
            List<ChangedComponent> parsedJson = OrangebeardLogger.ParseJson(json);
            Assert.AreEqual(1, parsedJson.Count);

            var expectedFirstElement = new ChangedComponent("myComponent1", null);
            Assert.AreEqual(expectedFirstElement, parsedJson[0]);
        }
    }
}