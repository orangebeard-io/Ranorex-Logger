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
using Orangebeard.Client;
using Orangebeard.Client.Entities;
using Orangebeard.Client.OrangebeardProperties;
using Ranorex;
using Ranorex.Core;
using Ranorex.Core.Reporting;
using Ranorex.Core.Testing;
using DateTime = System.DateTime;
using ItemAttribute = Orangebeard.Client.Entities.Attribute;


namespace RanorexOrangebeardListener
{
    // ReSharper disable once UnusedMember.Global
    public class OrangebeardLogger : IReportLogger
    {
        private bool useOldCode = false;
        //private readonly OrangebeardClient _orangebeard;
        private readonly OrangebeardV2Client _orangebeard;

        // OLD variables to hold context information.
        private ITestReporter _currentReporter;
        private LaunchReporter _launchReporter;
        // NEW variables to hold context information.
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
            if (useOldCode)
            {
                if (_launchReporter == null)
                {
                    _launchReporter = new LaunchReporter(_orangebeard, null, null, new ExtensionManager());
                    _launchReporter.Start(new StartLaunchRequest
                    {
                        StartTime = DateTime.UtcNow,
                        Name = _config.TestSetName,
                        ChangedComponents = _changedComponents.ToList()
                    });
                }
            }
            else
            {
                StartTestRun startTestRun = new StartTestRun(_config.TestSetName, _config.Description, _config.Attributes, _changedComponents);
                testRunUuid = _orangebeard.StartTestRun(startTestRun);
                if (testRunUuid == null)
                {
                    //TODO!+ Error handling!
                }
            }
        }

        public void End()
        {
            if (useOldCode)
            {
                Report.SystemSummary();
                while (_currentReporter != null)
                {
                    _currentReporter.Finish(new FinishTestItemRequest
                    {
                        Status = Status.STOPPED, // Was: Status.Interrupted
                        EndTime = DateTime.UtcNow
                    });
                    _currentReporter = _currentReporter.ParentTestReporter ?? null;
                }

                _launchReporter.Finish(new FinishLaunchRequest { EndTime = DateTime.UtcNow });
                _launchReporter.Sync();
            }
            else
            {
                if (testRunUuid != null)
                {
                    FinishTestRun finishTestRun = new FinishTestRun(); //TODO?~ What's the Status of the test run...? 
                    _orangebeard.FinishTestRun(testRunUuid.Value, finishTestRun);
                }
            }
        }

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
                            attachmentFileName = Path.GetFileName(filePath);
                            attachmentMimeType = Orangebeard.Shared.MimeTypes.MimeTypeMap.GetMimeType(Path.GetExtension(filePath));
                            return; 
                        } catch (Exception e)
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
            if (useOldCode)
            {
                if (category == null)
                {
                    category = string.Empty;
                }
                var logRq = new CreateLogItemRequest
                {
                    Time = DateTime.UtcNow,
                    Level = DetermineLogLevel(level.Name),
                    Text = "[" + category + "]: " + message
                };
                if (attachmentData != null && attachmentFileName != null)
                {
                    logRq.Attach = new LogItemAttach(mimeType, attachmentData) { Name = attachmentFileName };
                }


                var logLevel = DetermineLogLevel(level.Name);
                CreateLogItemRequest metaRq = null;
                if (MeetsMinimumSeverity(logLevel, LogLevel.warn) && metaInfos.Count >= 1)
                {
                    var meta = new StringBuilder().Append("Meta Info:").Append("\r\n");

                    foreach (var key in metaInfos.Keys)
                    {
                        meta.Append("\t").Append(key).Append(" => ").Append(metaInfos[key]).Append("\r\n");
                    }

                    metaRq = new CreateLogItemRequest
                    {
                        Time = DateTime.UtcNow,
                        Level = LogLevel.debug,
                        Text = meta.ToString()
                    };
                }

                if (_currentReporter == null)
                {
                    _launchReporter.Log(logRq);
                    if (metaRq != null) _launchReporter.Log(metaRq);
                }
                else
                {
                    _currentReporter.Log(logRq);
                    if (metaRq != null) _currentReporter.Log(metaRq);
                }
            }
            else
            {
                if (category == null)
                {
                    category = string.Empty;
                }
                var logLevel = DetermineLogLevel(level.Name);
                string logMessage = $"[{category}]: {message}";
                if (attachmentData != null && attachmentFileName != null)
                {

                    Attachment.AttachmentFile file = new Attachment.AttachmentFile(null); //TODO!+ Get the FileInfo ....
                    //TODO!+ You want the logMessage in here, too....
                    Attachment logAndAttachment = new Attachment(testRunUuid.Value, _tree.GetItemId().Value, logLevel, attachmentFileName, file);
                    //TODO!+ Commented out for now. This one doesn't work yet, and sends more messages to demo. Until we got StartTestItem and Log working properly, let's not add the error messages that this one generates as well.
                    //_orangebeard.SendAttachment(logAndAttachment);
                }
                else
                {
                    Log log = new Log(testRunUuid.Value, _tree.GetItemId().Value, logLevel, logMessage);
                    //TODO!+ Commented out for now. This one doesn't work yet, and sends more messages to demo. Until we got StartTestItem working properly, let's not add the error messages that this one generates as well.
                    //_orangebeard.Log(log);
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
                if (useOldCode)
                {
                    StartTestItemRequest rq = DetermineStartTestItemRequest(info["activity"], info);

                    // If there is no result key and we have not autopopulated suite and item, we need to start an item.
                    EnsureExistenceOfTopLevelSuite(rq);

                    UpdateTree(rq);

                    _currentReporter = _currentReporter == null
                        ? _launchReporter.StartChildTestReporter(rq)
                        : _currentReporter.StartChildTestReporter(rq);

                    return true;
                }
                else
                {
                    var startTestItem = DetermineStartTestItem(info["activity"], info);
                    var parentItemId = _tree?.GetItemId();
                    var testItemId = _orangebeard.StartTestItem(parentItemId, startTestItem);
                    UpdateTree(startTestItem, testItemId);
                    //TODO?+
                    return true;
                }

            }
            else
            {
                if (useOldCode)
                {
                    FinishTestItemRequest finishTestItemRequest = DetermineFinishItemRequest(info["result"]);

                    _currentReporter.Finish(finishTestItemRequest);
                    _currentReporter = _currentReporter.ParentTestReporter;

                    if (_tree.ItemType == TestItemType.TEST)
                    {
                        _isTestCaseOrDescendant = false;
                    }
                    _tree = _tree.GetParent();
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

        }

        /// <summary>
        /// Test if a top-level suite is needed, and if so, create one.
        /// </summary>
        private void EnsureExistenceOfTopLevelSuite(StartTestItemRequest rq)
        {
            if (_currentReporter == null && rq.Type != TestItemType.SUITE)
            {
                //start toplevel suite first
                StartTestItemRequest suiteRq = new StartTestItemRequest
                {
                    StartTime = DateTime.UtcNow,
                    Type = TestItemType.SUITE,
                    Name = ((TestSuite)TestSuite.Current).Children[0].Name,
                    Description = ((TestSuite)TestSuite.Current).Children[0].Comment,
                    Attributes = new List<ItemAttribute> { new ItemAttribute("Suite") },
                    HasStats = true
                };

                UpdateTree(suiteRq);
                _currentReporter = _launchReporter.StartChildTestReporter(suiteRq);
            }
        }

        private void UpdateTree(StartTestItemRequest rq)
        {
            if (_tree == null)
            {
                _tree = new TypeTree(rq.Type, rq.Name, null);
            }
            else
            {
                _tree = _tree.Add(rq.Type, rq.Name, null);
            }

            if (rq.Type == TestItemType.TEST)
            {
                _isTestCaseOrDescendant = true;
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

        private StartTestItemRequest DetermineStartTestItemRequest(string activityType, IDictionary<string, string> info)
        {
            var type = TestItemType.STEP;
            var name = "";
            var namePostfix = "";
            var description = "";           

            var attributes = new List<ItemAttribute>();
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
                    type = _isTestCaseOrDescendant? TestItemType.STEP : TestItemType.SUITE;
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

            var rq = new StartTestItemRequest
            {
                StartTime = DateTime.UtcNow,
                Type = type,
                Name = name + namePostfix,
                Description = description,
                Attributes = attributes,
                HasStats = type != TestItemType.STEP
            };
            return rq;
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

        private FinishTestItemRequest DetermineFinishItemRequest(string result /*IDictionary<string, string> info*/)
        {
            Status status;

            switch(result.ToLower())
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

            FinishTestItemRequest finishTestItemRequest = new FinishTestItemRequest
            {
                EndTime = DateTime.UtcNow,
                Status = status
            };
            return finishTestItemRequest;
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
                    try
                        {
                            LogData(
                                item.Level,
                                "Screenshot",
                                item.Message + "\r\n" +
                                "Screenshot file name: " + item.ScreenshotFileName,
                                Image.FromFile(TestReport.ReportEnvironment.ReportFileDirectory + "\\" + item.ScreenshotFileName),
                                new IndexedDictionary<string, string>() 
                                {
                                    new KeyValuePair<string, string>("attachmentFileName", Path.GetFileName(item.ScreenshotFileName)) 
                                });

                            _reportedErrorScreenshots.Add(item.ScreenshotFileName);
                        }
                        catch (Exception e)
                        {
                            LogToOrangebeard(
                                item.Level, 
                                "Screenshot", "Exception getting screenshot: " + e.Message + "\r\n" +
                                e.GetType().ToString() + ": " + e.StackTrace, 
                                null, null, null,
                                new IndexedDictionary<string, string>()
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
            var entry = (TestSuiteEntry) TestSuite.CurrentTestContainer;
            return entry.Comment;
        }

        private void UpdateTestrunWithSystemInfo(string message)
        {
            var launchAttrEntries = message.Split(new[] {"\r\n", "\r", "\n"}, StringSplitOptions.None);

            var attrs = (
                from entry in launchAttrEntries 
                select entry.Split(new[] {": "}, StringSplitOptions.None) 
                into attr 
                where attr.Length > 1 && 
                      !attr[0].Contains("displays") && 
                      !attr[0].Contains("CPUs") && 
                      !attr[0].Contains("Ranorex version") && 
                      !attr[0].Contains("Memory") && 
                      !attr[0].Contains("Runtime version") 
                select new ItemAttribute(attr[0], attr[1])
                ).ToList();

            _launchReporter.Update(new UpdateLaunchRequest { Attributes = attrs });
        }

        private static LogLevel DetermineLogLevel(string levelStr)
        {
            LogLevel level;
            var logLevel = char.ToUpper(levelStr[0]) + levelStr.Substring(1);
            if (Enum.IsDefined(typeof(LogLevel), logLevel))
                level = (LogLevel) Enum.Parse(typeof(LogLevel), logLevel);
            else if (logLevel.Equals("Failure", StringComparison.InvariantCultureIgnoreCase))
                level = LogLevel.error;
            else if (logLevel.Equals("Warn", StringComparison.InvariantCultureIgnoreCase))
                level = LogLevel.warn;
            else
                level = LogLevel.info;
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