using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orangebeard.Client.Abstractions.Models;
using RanorexOrangebeardListener;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RanorexOrangebeardListener.Tests
{
    [TestClass()]
    public class ChangedComponentsListTests
    {
        [TestMethod()]
        [DataRow(@"[{""componentName"": ""myComponent1"", ""componentVersion"":""myVersion1""}, {""componentName"": ""myComponent2"", ""componentVersion"":""myVersion2""}]")]
        public void ParseJsonTest_normalInput(string json)
        {
            ISet<ChangedComponent> parsedJson = ChangedComponentsList.ParseJson(json);
            Assert.AreEqual(2, parsedJson.Count);

            var expectedFirstElement = new ChangedComponent("myComponent1", "myVersion1");
            Assert.IsTrue(parsedJson.Contains(expectedFirstElement));

            var expectedSecondElement = new ChangedComponent("myComponent2", "myVersion2");
            Assert.IsTrue(parsedJson.Contains(expectedSecondElement));
        }

        [TestMethod()]
        [DataRow(@"[{""componentName"": ""myComponent1""}]")]
        public void ParseJsonTest_noComponentVersion(string json)
        {
            ISet<ChangedComponent> parsedJson = ChangedComponentsList.ParseJson(json);
            Assert.AreEqual(0, parsedJson.Count);
        }

        [TestMethod()]
        [DataRow(@"[{""componentName"": ""myComponent1"", ""componentVersion"":null}]")]
        public void ParseJsonTest_componentVersionHasValueNull(string json)
        {
            ISet<ChangedComponent> parsedJson = ChangedComponentsList.ParseJson(json);
            Assert.AreEqual(1, parsedJson.Count);

            var expectedFirstElement = new ChangedComponent("myComponent1", null);
            Assert.IsTrue(parsedJson.Contains(expectedFirstElement));
        }
    }
}