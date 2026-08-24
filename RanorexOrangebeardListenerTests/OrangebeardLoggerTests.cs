using System;
using System.Collections.Generic;
using System.Drawing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Orangebeard.Client.V3.Entity;
using Ranorex.Core.Testing;
using RanorexOrangebeardListener;

namespace RanorexOrangebeardListener.Tests
{
    [TestClass]
    public class OrangebeardLoggerTests
    {
        // Shared across tests in this class purely to avoid re-paying OrangebeardLogger's
        // construction cost; TryEncodeAndDedupeScreenshot tests below use distinct colors per
        // test so the shared dedup state can't leak between them.
        private static OrangebeardLogger _logger;

        [ClassInitialize]
        public static void ClassInitialize(TestContext context)
        {
            // Fixed, fake values so construction never depends on a developer machine's real
            // environment variables or an orangebeard.json found by scanning up the directory tree.
            // accessToken must be GUID-formatted - OrangebeardAsyncV3Client's constructor parses
            // it via Guid.Parse and throws FormatException otherwise.
            Environment.SetEnvironmentVariable("orangebeard.endpoint", "https://example.invalid");
            Environment.SetEnvironmentVariable("orangebeard.accessToken", "11111111-1111-1111-1111-111111111111");
            Environment.SetEnvironmentVariable("orangebeard.project", "unit-test-project");
            Environment.SetEnvironmentVariable("orangebeard.testrun", "unit-test-run");

            _logger = new OrangebeardLogger();
        }

        [TestMethod]
        public void TryEncodeAndDedupeScreenshot_FirstOccurrence_ReturnsTrueWithEncodedBytes()
        {
            using (var image = CreateSolidColorImage(Color.Red))
            {
                var isNew = _logger.TryEncodeAndDedupeScreenshot(image, out var dataBytes);

                Assert.IsTrue(isNew);
                Assert.IsNotNull(dataBytes);
                Assert.IsTrue(dataBytes.Length > 0);
            }
        }

        [TestMethod]
        public void TryEncodeAndDedupeScreenshot_SameImageLoggedTwice_SecondCallReturnsFalse()
        {
            using (var image = CreateSolidColorImage(Color.Blue))
            {
                var first = _logger.TryEncodeAndDedupeScreenshot(image, out _);
                var second = _logger.TryEncodeAndDedupeScreenshot(image, out _);

                Assert.IsTrue(first);
                Assert.IsFalse(second);
            }
        }

        [TestMethod]
        public void TryEncodeAndDedupeScreenshot_DifferentImages_BothReturnTrue()
        {
            using (var image1 = CreateSolidColorImage(Color.Green))
            using (var image2 = CreateSolidColorImage(Color.Yellow))
            {
                var first = _logger.TryEncodeAndDedupeScreenshot(image1, out _);
                var second = _logger.TryEncodeAndDedupeScreenshot(image2, out _);

                Assert.IsTrue(first);
                Assert.IsTrue(second);
            }
        }

        private static Bitmap CreateSolidColorImage(Color color)
        {
            var bitmap = new Bitmap(4, 4);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(color);
            }

            return bitmap;
        }

        [TestMethod]
        public void FormatParameters_NullDictionary_ReturnsEmptyString()
        {
            Assert.AreEqual(string.Empty, OrangebeardLogger.FormatParameters(null));
        }

        [TestMethod]
        public void FormatParameters_EmptyDictionary_ReturnsEmptyString()
        {
            Assert.AreEqual(string.Empty, OrangebeardLogger.FormatParameters(new Dictionary<string, string>()));
        }

        [TestMethod]
        public void FormatParameters_SingleParameter_ReturnsMarkdownTable()
        {
            var parameters = new Dictionary<string, string> { { "username", "alice" } };

            var result = OrangebeardLogger.FormatParameters(parameters);

            Assert.AreEqual("| Parameter | Value |\r\n|---|---|\r\n| username | alice |", result);
        }

        [TestMethod]
        public void FormatParameters_MultipleParameters_IncludesAllRowsInOrder()
        {
            var parameters = new Dictionary<string, string>
            {
                { "username", "alice" },
                { "env", "staging" }
            };

            var result = OrangebeardLogger.FormatParameters(parameters);

            Assert.AreEqual(
                "| Parameter | Value |\r\n|---|---|\r\n| username | alice |\r\n| env | staging |",
                result);
        }

        [TestMethod]
        public void FormatContainerParameters_NullTestContainer_ReturnsEmptyString()
        {
            Assert.AreEqual(string.Empty, OrangebeardLogger.FormatContainerParameters((ITestContainer)null));
        }

        [TestMethod]
        public void FormatContainerParameters_TestContainerWithParameters_ReturnsFormattedTable()
        {
            var containerMock = new Mock<ITestContainer>();
            containerMock.Setup(c => c.Parameters)
                .Returns(new Dictionary<string, string> { { "browser", "chrome" } });

            var result = OrangebeardLogger.FormatContainerParameters(containerMock.Object);

            StringAssert.Contains(result, "| browser | chrome |");
        }

        [TestMethod]
        public void FormatContainerParameters_NullTestSuite_ReturnsEmptyString()
        {
            Assert.AreEqual(string.Empty, OrangebeardLogger.FormatContainerParameters((ITestSuite)null));
        }

        [TestMethod]
        public void FormatContainerParameters_TestSuiteWithParameters_ReturnsFormattedTable()
        {
            var suiteMock = new Mock<ITestSuite>();
            suiteMock.Setup(s => s.Parameters)
                .Returns(new Dictionary<string, string> { { "env", "staging" } });

            var result = OrangebeardLogger.FormatContainerParameters(suiteMock.Object);

            StringAssert.Contains(result, "| env | staging |");
        }

        [TestMethod]
        public void PrependParameters_BothNull_ReturnsNull()
        {
            Assert.IsNull(OrangebeardLogger.PrependParameters(null, null));
        }

        [TestMethod]
        public void PrependParameters_ParametersEmpty_ReturnsDescriptionUnchanged()
        {
            Assert.AreEqual("some description", OrangebeardLogger.PrependParameters("", "some description"));
        }

        [TestMethod]
        public void PrependParameters_DescriptionEmpty_ReturnsParametersUnchanged()
        {
            Assert.AreEqual("| Parameter | Value |", OrangebeardLogger.PrependParameters("| Parameter | Value |", ""));
        }

        [TestMethod]
        public void PrependParameters_BothPresent_ConcatenatesWithBlankLineBetween()
        {
            var result = OrangebeardLogger.PrependParameters("PARAMS", "DESCRIPTION");

            Assert.AreEqual("PARAMS\r\n\r\nDESCRIPTION", result);
        }

        [TestMethod]
        public void StripHtml_PlainTextWithoutTags_ReturnsUnchanged()
        {
            Assert.AreEqual("just plain text", OrangebeardLogger.StripHtml("just plain text"));
        }

        [TestMethod]
        public void StripHtml_LineBreakTag_ConvertsToCarriageReturnLineFeed()
        {
            Assert.AreEqual("Line1\r\nLine2", OrangebeardLogger.StripHtml("Line1<br>Line2"));
        }

        [TestMethod]
        public void StripHtml_ClosingParagraphTag_ConvertsToCarriageReturnLineFeed()
        {
            Assert.AreEqual("Para1\r\nPara2", OrangebeardLogger.StripHtml("Para1</p>Para2"));
        }

        [TestMethod]
        public void StripHtml_GenericTags_AreStripped()
        {
            Assert.AreEqual("bold text", OrangebeardLogger.StripHtml("<b>bold</b> text"));
        }

        [TestMethod]
        public void StripHtml_HtmlEntity_IsDecodedEvenWithoutTags()
        {
            Assert.AreEqual("Tom & Jerry", OrangebeardLogger.StripHtml("Tom &amp; Jerry"));
        }

        [TestMethod]
        public void StripHtml_TagsAndEntityCombined_StripsAndDecodes()
        {
            Assert.AreEqual("Tom & Jerry", OrangebeardLogger.StripHtml("<b>Tom &amp; Jerry</b>"));
        }

        [DataTestMethod]
        [DataRow("ERROR", LogLevel.ERROR)]
        [DataRow("error", LogLevel.ERROR)]
        [DataRow("WARN", LogLevel.WARN)]
        [DataRow("Info", LogLevel.INFO)]
        [DataRow("DEBUG", LogLevel.DEBUG)]
        public void DetermineLogLevel_KnownLevelNames_ParsesCaseInsensitively(string input, LogLevel expected)
        {
            Assert.AreEqual(expected, OrangebeardLogger.DetermineLogLevel(input));
        }

        [TestMethod]
        public void DetermineLogLevel_RanorexFailureLevel_MapsToError()
        {
            Assert.AreEqual(LogLevel.ERROR, OrangebeardLogger.DetermineLogLevel("Failure"));
        }

        [TestMethod]
        public void DetermineLogLevel_UnrecognizedLevel_DefaultsToInfo()
        {
            Assert.AreEqual(LogLevel.INFO, OrangebeardLogger.DetermineLogLevel("SomeUnknownLevel"));
        }

        [TestMethod]
        public void MeetsMinimumSeverity_MoreSevereThanThreshold_ReturnsTrue()
        {
            Assert.IsTrue(OrangebeardLogger.MeetsMinimumSeverity(LogLevel.ERROR, LogLevel.WARN));
        }

        [TestMethod]
        public void MeetsMinimumSeverity_EqualToThreshold_ReturnsTrue()
        {
            Assert.IsTrue(OrangebeardLogger.MeetsMinimumSeverity(LogLevel.WARN, LogLevel.WARN));
        }

        [TestMethod]
        public void MeetsMinimumSeverity_LessSevereThanThreshold_ReturnsFalse()
        {
            Assert.IsFalse(OrangebeardLogger.MeetsMinimumSeverity(LogLevel.INFO, LogLevel.WARN));
        }

        [TestMethod]
        public void LogCorrelationKey_SameInputs_ProduceSameKey()
        {
            var key1 = OrangebeardLogger.LogCorrelationKey("Failure", "General", "Element not found");
            var key2 = OrangebeardLogger.LogCorrelationKey("Failure", "General", "Element not found");

            Assert.AreEqual(key1, key2);
        }

        [TestMethod]
        public void LogCorrelationKey_DifferentMessage_ProducesDifferentKey()
        {
            var key1 = OrangebeardLogger.LogCorrelationKey("Failure", "General", "Element not found");
            var key2 = OrangebeardLogger.LogCorrelationKey("Failure", "General", "A different message");

            Assert.AreNotEqual(key1, key2);
        }

        [TestMethod]
        public void LogCorrelationKey_FieldBoundaries_DoNotCollideAcrossConcatenation()
        {
            // Without a real separator between fields, "A"+"BC" would equal "AB"+"C". This
            // verifies the key format keeps such inputs distinct.
            var key1 = OrangebeardLogger.LogCorrelationKey("A", "BC", "msg");
            var key2 = OrangebeardLogger.LogCorrelationKey("AB", "C", "msg");

            Assert.AreNotEqual(key1, key2);
        }
    }
}
