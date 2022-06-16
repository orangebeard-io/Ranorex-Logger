/*
 * Copyright 2020 Orangebeard.io (https://www.orangebeard.io)
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using Orangebeard.Client;
using Orangebeard.Client.Abstractions.Models;
using Orangebeard.Client.Entities;
using Orangebeard.Client.OrangebeardProperties;
using Ranorex;
using Ranorex.Core;
using Ranorex.Core.Reporting;
using Ranorex.Core.Testing;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using ItemAttribute = Orangebeard.Client.Entities.Attribute;
using LogLevel = Orangebeard.Client.Entities.LogLevel;

namespace RanorexOrangebeardListener
{
    // ReSharper disable once UnusedMember.Global
    public class OrangebeardLogger : IReportLogger
    {

        /// <summary>
        /// A very simple console logger.
        /// The ultimate purpose is to implement ILogger or use an existing ILogger implementation.
        /// </summary>
        class SimpleConsoleLogger
        {
            public void LogError(string str) => Console.WriteLine(str);
        }

        private static readonly SimpleConsoleLogger logger = new SimpleConsoleLogger();

        private readonly OrangebeardV2Client _orangebeard;

        private Guid testRunUuid;

        private readonly OrangebeardConfiguration _config;
        private readonly List<string> _reportedErrorScreenshots = new List<string>();

        /// <summary>
        /// Context information required for properly converting Ranorex items to Orangebeard items.
        /// When converting Ranorex items to Orangebeard items, the context is important.
        /// A Ranorex Smart Folder can become an Orangebeard Suite or an Orangebeard Step, depending on where it appears.
        /// </summary>
        private TypeTree _tree = null;

        /// <summary>
        /// Indicates if the current Ranorex item maps to an Orangebeard Test, or a descendant of an Orangebeard Test.
        /// </summary>
        private bool _isTestCaseOrDescendant = false;

        private ISet<ChangedComponent> _changedComponents = new HashSet<ChangedComponent>();

        internal const string CHANGED_COMPONENTS_PATH = @".\changedComponents.json";
        internal const string CHANGED_COMPONENTS_VARIABLE = "orangebeard.changedComponents";

        private const string FILE_PATH_PATTERN = @"((((?<!\w)[A-Z,a-z]:)|(\.{0,2}\\))([^\b%\/\|:\n<>""']*))";
        private const string TESTSUITE = "testsuite";
        private const string TESTCONTAINER = "testcontainer";
        private const string SMARTFOLDER_DATAITERATION = "smartfolder_dataiteration";
        private const string TESTCASE_DATAITERATION = "testcase_dataiteration";
        private const string TESTMODULE = "testmodule";

        public OrangebeardLogger()
        {
            _config = new OrangebeardConfiguration()
                .WithListenerIdentification(
                    "Ranorex Logger/" +
                    typeof(OrangebeardLogger).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion
                    );
            _orangebeard = new OrangebeardV2Client(_config, true);

            _changedComponents = ChangedComponentsList.Load();
        }

        public bool PreFilterMessages => false;

        public void Start()
        {
            StartTestRun startTestRun = new StartTestRun(_config.TestSetName, _config.Description, _config.Attributes, _changedComponents);
            var possibleTestRunUuid = _orangebeard.StartTestRun(startTestRun);
            if (possibleTestRunUuid == null)
            {
                logger.LogError("Failed to start test run.");
            }
            else
            {
                testRunUuid = possibleTestRunUuid.Value;
                Console.WriteLine($"Started a test run with UUID {testRunUuid}.");
                // We added a new type to the enum, TestItemType.TEST_RUN .
                // Note, however, that when StartTestItem is called, it looks for the parent node in _tree .
                // A StartTestItem that is a top-level Suite, should NOT have a value for "parent item ID" filled in.
                // We need to check this in the code that starts a new Test Item.
                _tree = new TypeTree(TestItemType.TEST_RUN, _config.TestSetName, testRunUuid);
            }
        }

        public void End()
        {
            Report.SystemSummary();
            if (testRunUuid != null)
            {
                FinishTestRun finishTestRun = new FinishTestRun();
                while (_tree != null && _tree.GetItemId().Value != testRunUuid)
                {
                    var finishTestItem = new FinishTestItem(testRunUuid, Status.STOPPED);
                    _orangebeard.FinishTestItem(_tree.GetItemId().Value, finishTestItem);
                    _tree = _tree.GetParent();
                }

                _orangebeard.FinishTestRun(testRunUuid, finishTestRun);
            }
        }

        // This method is part of the IReportLogger interface.
        public void LogData(ReportLevel level, string category, string message, object data,
            IDictionary<string, string> metaInfos)
        {
            //Currently only screenshot attachments are supported. Can Ranorex attach anything else?
            if (!(data is Image)) return;

            var img = (Image)data;
            using (var ms = new MemoryStream())
            {
                img.Save(ms, ImageFormat.Jpeg);
                metaInfos.TryGetValue("attachmentFileName", out string filename);
                var dataBytes = ms.ToArray();
                LogToOrangebeard(level, category, message, dataBytes, "image/jpeg", filename, metaInfos);
            }
        }

        // This method is part of the IReportLogger interface.
        public void LogText(ReportLevel level, string category, string message, bool escape,
            IDictionary<string, string> metaInfos)
        {
            if (category.Equals("System Summary", StringComparison.InvariantCultureIgnoreCase))
            {
                UpdateTestrunWithSystemInfo(message);
            }
            else if (!HandlePotentialStartFinishLog(metaInfos))
            {
                string attachmentMimeType = null;
                byte[] attachmentData = null;
                string attachmentFileName = null;
                PopulateAttachmentData(ref message, ref attachmentMimeType, ref attachmentData, ref attachmentFileName);
                LogToOrangebeard(level, category, message, attachmentData, attachmentMimeType, attachmentFileName, metaInfos);
            }
        }

        private void PopulateAttachmentData(ref string message, ref string attachmentMimeType, ref byte[] attachmentData, ref string attachmentFileName)
        {
            if (_config.FileUploadPatterns == null || _config.FileUploadPatterns.Count == 0)
            {
                //nothing to look for!
                return;
            }
            Match match = Regex.Match(message, FILE_PATH_PATTERN);
            if (match.Success) //Look only at first match, as we support max 1 attachment per log entry
            {
                string filePath = match.Value;
                Match patternMatch;
                foreach (string pattern in _config.FileUploadPatterns)
                {
                    patternMatch = Regex.Match(filePath, pattern);
                    if (patternMatch.Success && Path.IsPathRooted(filePath)) //Ignore relative paths, as they are likely user-generated and tool dependent in html logs
                    {
                        try
                        {
                            attachmentData = File.ReadAllBytes(filePath);
                            attachmentFileName = Path.GetFullPath(filePath);
                            attachmentMimeType = Orangebeard.Shared.MimeTypes.MimeTypeMap.GetMimeType(Path.GetExtension(filePath));
                            return;
                        }
                        catch (Exception e)
                        {
                            attachmentMimeType = null;
                            attachmentData = null;
                            attachmentFileName = null;
                            message = $"{message}\r\nFailed to attach {filePath} ({e.Message})";
                        }
                    }
                }
            }
        }

        private void LogToOrangebeard(ReportLevel level, string category, string message, byte[] attachmentData, string mimeType, string attachmentFileName,
            IDictionary<string, string> metaInfos)
        {
            if (category == null)
            {
                category = string.Empty;
            }
            var logLevel = DetermineLogLevel(level.Name);
            string logMessage = $"[{category}]: {message}";

            var possibleItemId = _tree.GetItemId();
            if (possibleItemId == null)
            {
                logger.LogError("No item ID found for log message. Discarding the following message:\r\n{logMessage}");
                return;
            }

            var itemId = possibleItemId.Value;
            if (itemId.Equals(testRunUuid))
            {
                logger.LogError($"Cannot log message at Test Run level. Discarded message:\r\n{logMessage}");
                return;
            }

            if (attachmentData != null && attachmentFileName != null)
            {
                FileInfo fileInfo = new FileInfo(attachmentFileName);
                Attachment.AttachmentFile file = new Attachment.AttachmentFile(fileInfo);
                Attachment logAndAttachment = new Attachment(testRunUuid, itemId, logLevel, logMessage, file);
                _orangebeard.SendAttachment(logAndAttachment);
            }
            else
            {
                Log log = new Log(testRunUuid, itemId, logLevel, logMessage, LogFormat.PLAIN_TEXT);
                _orangebeard.Log(log);
            }

            if (MeetsMinimumSeverity(logLevel, LogLevel.warn) && metaInfos.Count >= 1)
            {
                var meta = new StringBuilder().Append("Meta Info:").Append("\r\n");

                foreach (var key in metaInfos.Keys)
                {
                    meta.Append("\t").Append(key).Append(" => ").Append(metaInfos[key]).Append("\r\n");
                }

                Log metaRq = new Log(testRunUuid, itemId, LogLevel.debug, meta.ToString(), LogFormat.PLAIN_TEXT);
                _orangebeard.Log(metaRq);
            }


        }

        /// <summary>
        /// We have a log item; it can be a Start log item, a Finish log item, or another type of log item.
        /// This method checks if the item is a Start log item or a Finish log item, and if so, handles them accordingly.
        /// In this case, the method returns <code>true</code> to tell the caller that the log item has was a Start log item or a Finish log item.
        /// Otherwise (in other words, if we did not find a Start log item or a Finish log item) this method returns <code>false</code>.
        /// </summary>
        /// <param name="info">Information obtained from Ranorex that describes the log item.</param>
        /// <returns><code>true</code> if a Start Test Item or Finish Test Item were found; <code>false</code> otherwise.</returns>
        private bool HandlePotentialStartFinishLog(IDictionary<string, string> info)
        {
            if (!info.ContainsKey("activity")) return false;

            if (!info.ContainsKey("result"))
            {
                var startTestItem = DetermineStartTestItem(info["activity"], info);
                Guid? parentItemId = null;
                if (_tree != null && _tree.ItemType != TestItemType.TEST_RUN)
                {
                    parentItemId = _tree?.GetItemId();
                }
                var testItemId = _orangebeard.StartTestItem(parentItemId, startTestItem);
                UpdateTree(startTestItem, testItemId);
                return true;
            }
            else
            {
                var finishTestItem = DetermineFinishTestItem(info["result"]);
                var itemId = _tree?.GetItemId();
                if (itemId == null)
                {
                    logger.LogError($"Cannot find the Item ID for a FinishTestItem request! The FinishTestItem request is:\r\n{finishTestItem}");
                    return true; // We return `true` because what we found was a Finish Test Item; even if we failed to handle it the way we wanted.
                }

                _orangebeard.FinishTestItem(itemId.Value, finishTestItem);
                if (_tree.ItemType == TestItemType.TEST)
                {
                    _isTestCaseOrDescendant = false;
                }
                _tree = _tree.GetParent();
                return true;
            }

        }

        private void UpdateTree(StartTestItem startTestItem, Guid? testItemId)
        {
            if (_tree == null)
            {
                _tree = new TypeTree(startTestItem.Type, startTestItem.Name, testItemId);
            }
            else
            {
                _tree = _tree.Add(startTestItem.Type, startTestItem.Name, testItemId);
            }

            if (startTestItem.Type == TestItemType.TEST)
            {
                _isTestCaseOrDescendant = true;
            }
        }

        private StartTestItem DetermineStartTestItem(string activityType, IDictionary<string, string> info)
        {
            TestItemType type = TestItemType.STEP;
            string name = "", namePostfix = "";
            string description = "";
            var attributes = new HashSet<ItemAttribute>();

            switch (activityType)
            {
                case TESTSUITE:
                    var suite = (TestSuite)TestSuite.Current;
                    type = TestItemType.SUITE;
                    name = info["modulename"];
                    attributes.Add(new ItemAttribute("Suite"));
                    description = suite.Children.First().Comment;
                    break;

                case TESTCONTAINER:
                    name = info["testcontainername"];
                    if (TestSuite.CurrentTestContainer.IsSmartFolder)
                    {
                        type = _isTestCaseOrDescendant ? TestItemType.STEP : TestItemType.SUITE;
                        attributes.Add(new ItemAttribute("Smart folder"));
                    }
                    else
                    {
                        type = TestItemType.TEST;
                        attributes.Add(new ItemAttribute("Test Case"));
                        _isTestCaseOrDescendant = true;
                    }

                    description = DescriptionForCurrentContainer();
                    break;

                case SMARTFOLDER_DATAITERATION:
                    type = _isTestCaseOrDescendant ? TestItemType.STEP : TestItemType.SUITE;
                    name = info["testcontainername"];
                    namePostfix = " (data iteration #" + info["smartfolderdataiteration"] + ")";
                    attributes.Add(new ItemAttribute("Smart folder"));
                    description = DescriptionForCurrentContainer();
                    break;

                case TESTCASE_DATAITERATION:
                    type = TestItemType.TEST;
                    _isTestCaseOrDescendant = true;
                    name = info["testcontainername"];
                    namePostfix = " (data iteration #" + info["testcasedataiteration"] + ")";
                    attributes.Add(new ItemAttribute("Test Case"));
                    description = DescriptionForCurrentContainer();
                    break;

                case TESTMODULE:
                    type = TestItemType.STEP;
                    name = info["modulename"];
                    attributes.Add(new ItemAttribute("Module"));
                    var currentLeaf = (TestModuleLeaf)TestModuleLeaf.Current;
                    if (currentLeaf.Parent is ModuleGroupNode)
                    {
                        attributes.Add(new ItemAttribute("Module Group", currentLeaf.Parent.DisplayName));
                    }
                    if (currentLeaf.IsDescendantOfSetupNode)
                    {
                        attributes.Add(new ItemAttribute("Setup"));
                        type = TestItemType.BEFORE_METHOD;
                    }

                    if (currentLeaf.IsDescendantOfTearDownNode)
                    {
                        attributes.Add(new ItemAttribute("TearDown"));
                        type = TestItemType.AFTER_METHOD;
                    }

                    description = currentLeaf.Comment;

                    break;
            }

            return new StartTestItem(testRunUuid, name + namePostfix, type, description, attributes);
        }

        private FinishTestItem DetermineFinishTestItem(string result)
        {
            Status status;

            switch (result.ToLower())
            {
                case "success":
                    status = Status.PASSED;
                    break;
                case "ignored":
                    status = Status.SKIPPED;
                    break;
                default:
                    status = Status.FAILED;
                    LogErrorScreenshots(ActivityStack.Current.Children);
                    break;
            }

            // Note that the FinishTestItem constructor automatically fills in its end time as the current time.
            return new FinishTestItem(testRunUuid, status);
        }

        private void LogErrorScreenshots(IEnumerable<IReportItem> reportItems)
        {
            foreach (var reportItem in reportItems)
            {
                if (reportItem.GetType() == typeof(ReportItem))
                {
                    var item = (ReportItem)reportItem;
                    if (item.ScreenshotFileName != null && !_reportedErrorScreenshots.Contains(item.ScreenshotFileName))
                    {
                        var fullPathName = $"{TestReport.ReportEnvironment.ReportFileDirectory}\\{item.ScreenshotFileName}";
                        try
                        {
                            LogData(
                                level     : item.Level,
                                category  : "Screenshot",
                                message   : $"{item.Message}\r\nScreenshot file name: {item.ScreenshotFileName}",
                                data      : Image.FromFile($"{TestReport.ReportEnvironment.ReportFileDirectory}\\{item.ScreenshotFileName}"),
                                metaInfos : new IndexedDictionary<string, string>()
                                            {
                                                //new KeyValuePair<string, string>("attachmentFileName", Path.GetFileName(item.ScreenshotFileName))
                                                new KeyValuePair<string, string>("attachmentFileName", fullPathName)
                                            });

                            _reportedErrorScreenshots.Add(item.ScreenshotFileName);
                        }
                        catch (Exception e)
                        {
                            LogToOrangebeard(
                                level              : item.Level,
                                category           : "Screenshot",
                                message            : $"Exception getting screenshot: {e.Message}\r\n{e.GetType()}: {e.StackTrace}",
                                attachmentData     : null,
                                mimeType           : null,
                                attachmentFileName : null,
                                metaInfos          : new IndexedDictionary<string, string>()
                                );
                        }
                    }
                }
                else if (reportItem.GetType() == typeof(Activity) || reportItem.GetType().IsSubclassOf(typeof(Activity)))
                {
                    LogErrorScreenshots(((Activity)reportItem).Children);
                }
            }
        }

        private static string DescriptionForCurrentContainer()
        {
            var entry = (TestSuiteEntry)TestSuite.CurrentTestContainer;
            return entry.Comment;
        }

        private void UpdateTestrunWithSystemInfo(string message)
        {
            var launchAttrEntries = message.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

            var attrs = (
                from entry in launchAttrEntries
                select entry.Split(new[] { ": " }, StringSplitOptions.None)
                into attr
                where attr.Length > 1 &&
                      !attr[0].Contains("displays") &&
                      !attr[0].Contains("CPUs") &&
                      !attr[0].Contains("Ranorex version") &&
                      !attr[0].Contains("Memory") &&
                      !attr[0].Contains("Runtime version")
                select new ItemAttribute(attr[0], attr[1])
                );
            var attrSet = new HashSet<ItemAttribute>(attrs);

            string testRunDescription = _config.Description;
            var newDescription = attrs.FirstOrDefault(x => String.Compare(x.Key, "Description", ignoreCase: true) == 0);
            if (newDescription != null)
            {
                testRunDescription = newDescription.Value;
            }
            var update = new UpdateTestRun(testRunDescription, attrSet);
            _orangebeard.UpdateTestRun(testRunUuid, update);
        }

        private static LogLevel DetermineLogLevel(string levelStr)
        {
            LogLevel level;

            // Case-insensitive test to see if the given "levelStr" exists in our LogLevel enum.
            string[] logLevelNames = Enum.GetNames(typeof(LogLevel));
            bool levelStrContained = logLevelNames.Any(logLevelName => string.Compare(logLevelName, levelStr, StringComparison.InvariantCultureIgnoreCase) == 0);


            if (levelStrContained)
            {
                level = (LogLevel)Enum.Parse(typeof(LogLevel), levelStr.ToLower());
            }
            else if (levelStr.Equals("Success", StringComparison.InvariantCultureIgnoreCase))
            {
                level = LogLevel.info;
            }
            else if (levelStr.Equals("Failure", StringComparison.InvariantCultureIgnoreCase))
            {
                level = LogLevel.error;
            }
            else if (levelStr.Equals("Warn", StringComparison.InvariantCultureIgnoreCase))
            {
                level = LogLevel.warn;
            }
            else
            {
                logger.LogError($"Unknown log level: {levelStr}");
                level = LogLevel.unknown;
            }
            return level;
        }

        /// Determine if a LogLevel has at least a minimum severity.
        /// For example, <code>LogLevel.Error</code> is more severe than <code>LogLevel.Warning</code>; so <code>MeetsMinimumSeverity(Error, Warning)</code> is <code>true</code>.
        /// Similarly, <code>LogLevel.Warning</code> is at severity level <code>Warning</code>; so <code>MeetsMinimumSeverity(Warning, Warning)</code> is also <code>true</code>.
        /// But <code>LogLevel.Info</code> is less severe than <code>LogLevel.Warning</code>, so <code>MeetsMinimumSeverity(Warning, Info)</code> is <code>false</code>.
        /// <param name="level">The LogLevel whose severity must be checked.</param>
        /// <param name="threshold">The severity level to check against.</param>
        /// <returns>The boolean value <code>true</code> if and only if the given log level has at least the same level of severity as the threshold value.</returns>
        private static bool MeetsMinimumSeverity(LogLevel level, LogLevel threshold)
        {
            return ((int)level) >= (int)threshold;
        }
    }
}