/*
 * Copyright 2020 Orangebeard.io (https://www.orangebeard.io)
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Orangebeard.Client;
using Orangebeard.Client.Entities;
using Orangebeard.Client.OrangebeardProperties;
using Ranorex;
using Ranorex.Core;
using Ranorex.Core.Reporting;
using Ranorex.Core.Testing;
using DateTime = System.DateTime;
using ItemAttribute = Orangebeard.Client.Entities.Attribute;
using LogLevel = Orangebeard.Client.Entities.LogLevel;

namespace RanorexOrangebeardListener
{
    // ReSharper disable once UnusedMember.Global
    public class OrangebeardLogger : IReportLogger
    {
        //TODO!+ Add some logging. Unfortunately Microsoft's documentation is sloppy on how to do this properly unless you use .NET Core.
        //public static Func<string, Microsoft.Extensions.Logging.LogLevel, bool> filter = (str, lvl) => true;
        //public ILogger logger; // = new Microsoft.Extensions.Logging.Console.ConsoleLogger("ROLLogger", filter, null);
        //private static ConsoleLoggerOptions options = new ConsoleLoggerOptions();
        //private static IOptionsMonitor<ConsoleLoggerOptions> monitor = new OptionsMonitor<ConsoleLoggerOptions>();
        //private static ConsoleLoggerProvider provider = new ConsoleLoggerProvider(options);
        //public ILogger logger = provider.CreateLogger("ConsoleLogger");


        private bool useOldCode = false;
        //private readonly OrangebeardClient _orangebeard;
        private readonly OrangebeardV2Client _orangebeard;

        private Guid? testRunUuid;
        
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
            testRunUuid = _orangebeard.StartTestRun(startTestRun);
            if (testRunUuid == null)
            {
                //TODO!~ Use a logger.
                Console.WriteLine("Failed to start test run.");
            }
            else
            {
                // We added a new type to the enum, TestItemType.TEST_RUN .
                // Note, however, that when StartTestItem is called, it looks for the parent node in _tree .
                // A StartTestItem that is a top-level Suite, should NOT have a value for "parent item ID" filled in.
                // We need to check this in the code that starts a new Test Item.
                _tree = new TypeTree(TestItemType.TEST_RUN, _config.TestSetName, testRunUuid);
            }
        }

        public void End()
        {
            if (testRunUuid != null)
            {
                FinishTestRun finishTestRun = new FinishTestRun(); //TODO?~ What's the Status of the test run...? 
                while (_tree != null && _tree.GetItemId().Value != testRunUuid.Value)
                {
                    var finishTestItem = new FinishTestItem(testRunUuid.Value, Status.STOPPED);
                    _orangebeard.FinishTestItem(_tree.GetItemId().Value, finishTestItem);
                    _tree = _tree.GetParent();
                }
                _orangebeard.FinishTestRun(testRunUuid.Value, finishTestRun);
            }
            //TODO?+ Old code had a "launchReporter.Sync()" call. Check if we need something similar here.
        }

        // This method is part of the IReportLogger interface.
        public void LogData(ReportLevel level, string category, string message, object data,
            IDictionary<string, string> metaInfos)
        {
            //Currently only screenshot attachments are supported. Can Ranorex attach anything else?
            if (!(data is Image)) return;

            var img = (Image) data;
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
                //if (useOldCode)
                //{
                    string attachmentMimeType = null;
                    byte[] attachmentData = null;
                    string attachmentFileName = null;
                    PopulateAttachmentData(ref message, ref attachmentMimeType, ref attachmentData, ref attachmentFileName);
                    LogToOrangebeard(level, category, message, attachmentData, attachmentMimeType, attachmentFileName, metaInfos);
                //}
                //else
                //{
                //    var fileInfo = RetrieveAttachmentData(ref message);
                //    var logLevel = DetermineLogLevel(level.ToString()); //TODO?~ Is this correct?
                //    var log = new Log(testRunUuid.Value, _tree.GetItemId().Value, logLevel, message);
                //    _orangebeard.Log(log); //TODO?~ Should we send a Log and an SendAttachment with the SAME Test Item ID?!?!
                //    if (fileInfo != null)
                //    {
                //        var attachmentFile = new Attachment.AttachmentFile(fileInfo);
                //        var attachment = new Attachment(testRunUuid.Value, _tree.GetItemId().Value, logLevel, fileInfo.Name, attachmentFile);
                //        _orangebeard.SendAttachment(attachment);
                //    }
                //}
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
                            attachmentFileName = Path.GetFileName(filePath);
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

        // If a message contains a filename, return the FileInfo for that file. Note that null is a valid result value here!
        /// <summary>
        /// If a message contains a file name, return a FileInfo instance for that file.
        /// If the message does not contain a filename, return a <code>null</code>.
        /// If creating the FileInfo instance fails, an error message is written into the <paramref name="message">message</paramref> parameter.
        /// </summary>
        /// <param name="message">Log message that may be a filename.</param>
        /// <returns>A FileInfo object for the file specified in the <paramref name="message">message</paramref> parameter, or <code>null</code> if the parameter does not contain a file name.</returns>
        private FileInfo RetrieveAttachmentData(ref string message)
        {
            if (_config.FileUploadPatterns == null || _config.FileUploadPatterns.Count == 0)
            {
                //nothing to look for!
                return null;
            }
            Match match = Regex.Match(message, FILE_PATH_PATTERN);
            if (!match.Success)
            {
                return null;
            }
            // Look only at the first match, as we support max 1 attachment per log entry
            string filePath = match.Value;
            Match patternMatch;
            foreach (string pattern in _config.FileUploadPatterns)
            {
                patternMatch = Regex.Match(filePath, pattern);
                if (patternMatch.Success && Path.IsPathRooted(filePath)) //Ignore relative paths, as they are likely user-generated and tool-dependent in HTML logs.
                {
                    try
                    {
                        return new FileInfo(filePath);
                    }
                    catch (Exception exc)
                    {
                        message = $"{message}\r\nFailed to attach {filePath} ({exc.Message})";
                    }
                }
            }
            return null;
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
            if (attachmentData != null && attachmentFileName != null)
            {
                FileInfo fileInfo = new FileInfo(attachmentFileName);
                Attachment.AttachmentFile file = new Attachment.AttachmentFile(fileInfo);
                //TODO!+ You want the logMessage in here, too....
                Attachment logAndAttachment = new Attachment(testRunUuid.Value, _tree.GetItemId().Value, logLevel, attachmentFileName, file);
                _orangebeard.SendAttachment(logAndAttachment);
            }
            else
            {
                Log log = new Log(testRunUuid.Value, _tree.GetItemId().Value, logLevel, logMessage);
                _orangebeard.Log(log);
            }

            //TODO?~ Meta request... how are we going to handle the Test Item ID here?  Do Meta logs HAVE Test Item ID's? SHOULD they?
            if (MeetsMinimumSeverity(logLevel, LogLevel.warn) && metaInfos.Count >= 1)
            {
                var meta = new StringBuilder().Append("Meta Info:").Append("\r\n");

                foreach (var key in metaInfos.Keys)
                {
                    meta.Append("\t").Append(key).Append(" => ").Append(metaInfos[key]).Append("\r\n");
                }

                //TODO?~ Can we also get logs at the level of the test run?
                Log metaRq = new Log(testRunUuid.Value, _tree.GetItemId().Value, logLevel, meta.ToString());
                //TODO!+ Commented out for now. This one doesn't work yet, and sends more messages to demo. Until we got StartTestItem and non-meta Log working properly, let's not add the error messages that this one generates as well.
                //_orangebeard.Log(metaRq);
            }
        }

        /// <summary>
        /// We have a log item; it can be a Start log item, a Finish log item, or another type of log item.
        /// This method checks if the item is a Start log item or a Finish log item, and if so, handles them accordingly.
        /// In this case, the method returns <code>true</code> to tell the caller that the log item has been handled.
        /// Otherwise (in other words, if no log items were handled) this method returns <code>false</code>.
        /// </summary>
        /// <param name="info">Information obtained from Ranorex that describes the log item.</param>
        /// <returns><code>true</code> if a Start test item or Finish test item were handled; <code>false</code> otherwise.</returns>
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
                //TODO?+
                return true;
            }
            else
            {
                var finishTestItem = DetermineFinishTestItem(info["result"]);
                var itemId = _tree.GetItemId();
                //TODO?+ Null check on itemId ?
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

        private StartTestItem DetermineStartTestItem(string activityType, IDictionary<string,string> info)
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

            return new StartTestItem(testRunUuid.Value, name + namePostfix, type, description, attributes);
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
            return new FinishTestItem(testRunUuid.Value, status);
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
                        if (useOldCode)
                        {
                            try
                            {
                                //TODO!~ This passes the image filename, but NOT the directory where the screenshot is!
                                // The solution is to start using our own stuff....
                                LogData(
                                    level:      item.Level,
                                    category:   "Screenshot",
                                    message:    $"{item.Message}\r\nScreenshot file name: {item.ScreenshotFileName}",
                                    data:       Image.FromFile($"{TestReport.ReportEnvironment.ReportFileDirectory}\\{item.ScreenshotFileName}"),
                                    metaInfos:  new IndexedDictionary<string, string>()
                                                {
                                                    //new KeyValuePair<string, string>("attachmentFileName", Path.GetFileName(item.ScreenshotFileName))
                                                    new KeyValuePair<string, string>("attachmentFileName", fullPathName)
                                                });

                                _reportedErrorScreenshots.Add(item.ScreenshotFileName);
                            }
                            catch (Exception e)
                            {
                                LogToOrangebeard(
                                    level:              item.Level,
                                    category:           "Screenshot",
                                    message:            $"Exception getting screenshot: {e.Message}\r\n{e.GetType()}: {e.StackTrace}",
                                    attachmentData:     null,
                                    mimeType:           null,
                                    attachmentFileName: null,
                                    metaInfos:          new IndexedDictionary<string, string>()
                                    );
                            }
                        }
                        else
                        {
                            //TODO?- In this case the old code seems to work well with the new client...

                            var fileInfo = new FileInfo(fullPathName);
                            var attachmentFile = new Attachment.AttachmentFile(fileInfo);
                            var logLevel = DetermineLogLevel(item.Level.ToString()); //TODO?~ Is this correct?
                            //TODO?+ Handle the case that _tree == null ... ?
                            var attachment = new Attachment(testRunUuid.Value, _tree.GetItemId().Value, logLevel, fileInfo.Name, attachmentFile);
                            _orangebeard.SendAttachment(attachment);
                            _reportedErrorScreenshots.Add(item.ScreenshotFileName);
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
            var entry = (TestSuiteEntry) TestSuite.CurrentTestContainer;
            return entry.Comment;
        }

        private void UpdateTestrunWithSystemInfo(string message)
        {
            var launchAttrEntries = message.Split(new[] {"\r\n", "\r", "\n"}, StringSplitOptions.None);

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

            //TODO!~ Check if we also change the description. Maybe "attrs" contains a  "description" element, too.
            string testRunDescription = _config.Description;
            var update = new UpdateTestRun(testRunDescription, attrSet);
            _orangebeard.UpdateTestRun(testRunUuid.Value, update);
        }

        private static LogLevel DetermineLogLevel(string levelStr)
        {
            LogLevel level;
            var logLevel = char.ToUpper(levelStr[0]) + levelStr.Substring(1); //TODO?- This was done when the log levels where enums whose symbols started with a capital letter. Enum.IsDefined is case sensitive.
            if (Enum.IsDefined(typeof(LogLevel), levelStr.ToLower()))
                level = (LogLevel)Enum.Parse(typeof(LogLevel), levelStr.ToLower());
            else if (logLevel.Equals("Failure", StringComparison.InvariantCultureIgnoreCase))
                level = LogLevel.error;
            else if (logLevel.Equals("Warn", StringComparison.InvariantCultureIgnoreCase))
                level = LogLevel.warn;
            else
                level = LogLevel.info;  //TODO?~ Shouldn't the default log level be "unknown", rather than "info" ?
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