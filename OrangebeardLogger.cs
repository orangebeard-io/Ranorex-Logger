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
using Newtonsoft.Json.Linq;
using Orangebeard.Client;
using Orangebeard.Client.Abstractions.Models;
using Orangebeard.Client.Abstractions.Requests;
using Orangebeard.Client.OrangebeardProperties;
using Orangebeard.Shared.Extensibility;
using Orangebeard.Shared.Reporter;
using Ranorex;
using Ranorex.Core;
using Ranorex.Core.Reporting;
using Ranorex.Core.Testing;
using DateTime = System.DateTime;

namespace RanorexOrangebeardListener
{
    // ReSharper disable once UnusedMember.Global
    public class OrangebeardLogger : IReportLogger
    {
        private readonly OrangebeardClient _orangebeard;
        private ITestReporter _currentReporter;
        private LaunchReporter _launchReporter;
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

        private List<ChangedComponent> _changedComponents = new List<ChangedComponent>();

        private const string CHANGED_COMPONENTS_PATH = @".\changedComponents.json";
        private const string CHANGED_COMPONENTS_VARIABLE = "orangebeard.changedComponents";

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
            _orangebeard = new OrangebeardClient(_config);

            LoadChangedComponentsList();
        }

        private void LoadChangedComponentsList()
        {
            string changedComponentsJson = Environment.GetEnvironmentVariable(CHANGED_COMPONENTS_VARIABLE);
            if (string.IsNullOrEmpty(changedComponentsJson))
            {
                if (File.Exists(CHANGED_COMPONENTS_PATH))
                {
                    changedComponentsJson = File.ReadAllText(CHANGED_COMPONENTS_PATH);
                }
            }

            if (string.IsNullOrWhiteSpace(changedComponentsJson))
            {
                _changedComponents = new List<ChangedComponent>();
            }
            else
            {
                _changedComponents = ParseJson(changedComponentsJson);
            }
        }

        /// <summary>
        /// Parse a JSON array to a list of (componentName, componentVersion) pairs.
        /// For example, suppose you have the JSON array [{"componentName":"barber","componentVersion":"2022.1.1.35"},{"componentName":"shaver","componentVersion":"2019.2.1.24"}].
        /// This will result in a list of two pairs: [("barber","2022.1.1.35"),("shaver","2019.2.1.24")].
        /// Elements in the JSON array <b>must</b> contain a pair that starts with "componentName" and a pair that starts with "componentVersion". However, the value for "componentVersion" is allowed to be null.
        /// So [{"componentName":"barber","componentVersion":null}] is legal; the version is allowed to be null.
        /// But [{"componentName":"barber"}] is illegal; there should be a "componentVersion".
        /// Illegal elements will be ignored.
        /// It is allowed to add more elements in an element than componentVersion and componentName; those extra elements will be ignored, but may be processed in the future.
        /// For example, [{"componentName":"barber","componentVersion":"2022.1.1.35", "componentTool":"shavingCream"}] will simply result in the pair ("barber","2022.1.1.35").
        /// </summary>
        /// <returns>A list of pairs, where each pair is a combination of a component name and a component version.</returns>
        public static List<ChangedComponent> ParseJson(string json)
        {
            JArray jsonArray = JArray.Parse(json);
            var pairs = new List<ChangedComponent>();

            foreach (JToken member in jsonArray)
            {
                JToken jTokenName = member["componentName"];
                JToken jTokenVersion = member["componentVersion"];

                if (jTokenName != null && jTokenName.Type == JTokenType.String && jTokenVersion!= null)
                {
                    string name = jTokenName.Value<string>();

                    if (jTokenVersion.Type == JTokenType.String)
                    {
                        string version = jTokenVersion.Value<string>();
                        pairs.Add(new ChangedComponent(name, version));
                    }
                    else if (jTokenVersion.Type == JTokenType.Null)
                    {
                        pairs.Add(new ChangedComponent(name, null));
                    }
                }
            }

            return pairs;
        }

        public bool PreFilterMessages => false;

        public void Start()
        {
            if(_launchReporter == null)
            {
                _launchReporter = new LaunchReporter(_orangebeard, null, null, new ExtensionManager());
                _launchReporter.Start(new StartLaunchRequest
                {
                    StartTime = DateTime.UtcNow,
                    Name = _config.TestSetName
                });
            }
        }

        public void End()
        {
            Report.SystemSummary();
            while (_currentReporter != null)
            {
                _currentReporter.Finish(new FinishTestItemRequest
                {
                    Status = Status.Interrupted,
                    EndTime = DateTime.UtcNow
                });
                _currentReporter = _currentReporter.ParentTestReporter ?? null;
            }

            _launchReporter.Finish(new FinishLaunchRequest { EndTime = DateTime.UtcNow });
            _launchReporter.Sync();
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
            if (MeetsMinimumSeverity(logLevel, LogLevel.Warning) && metaInfos.Count >= 1)
            {
                var meta = new StringBuilder().Append("Meta Info:").Append("\r\n");

                foreach (var key in metaInfos.Keys)
                {
                    meta.Append("\t").Append(key).Append(" => ").Append(metaInfos[key]).Append("\r\n");
                }

                metaRq = new CreateLogItemRequest
                {
                    Time = DateTime.UtcNow,
                    Level = LogLevel.Debug,
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

        private bool HandlePotentialStartFinishLog(IDictionary<string, string> info)
        {
            if (!info.ContainsKey("activity")) return false;

            //If there is no result key, we need to start an item
            if (!info.ContainsKey("result"))
            {
                StartTestItemRequest rq = DetermineStartTestItemRequest(info["activity"], info);

                if (_tree == null)
                {
                    _tree = new TypeTree(rq.Type, rq.Name);
                } else
                {
                    _tree = _tree.Add(rq.Type, rq.Name);
                }

                if (rq.Type == TestItemType.Test)
                {
                    _isTestCaseOrDescendant = true;
                }

                _currentReporter = _currentReporter == null
                    ? _launchReporter.StartChildTestReporter(rq)
                    : _currentReporter.StartChildTestReporter(rq);

            }
            else
            {
                FinishTestItemRequest finishTestItemRequest = DetermineFinishItemRequest(info["result"]);

                _currentReporter.Finish(finishTestItemRequest);

                _currentReporter = _currentReporter.ParentTestReporter;

                if (_tree.ItemType == TestItemType.Test)
                {
                    _isTestCaseOrDescendant = false;
                }
                _tree = _tree.GetParent();
            }

            return true;
        }

        private StartTestItemRequest DetermineStartTestItemRequest(string activityType, IDictionary<string, string> info)
        {
            var type = TestItemType.Step;
            var name = "";
            var namePostfix = "";
            var description = "";

            var attributes = new List<ItemAttribute>();
            switch (activityType)
            {
                case TESTSUITE:
                    var suite = (TestSuite)TestSuite.Current;
                    type = TestItemType.Suite;
                    name = info["modulename"];
                    attributes.Add(new ItemAttribute { Value = "Suite" });
                    description = suite.Children.First().Comment;
                    break;

                case TESTCONTAINER:
                    name = info["testcontainername"];
                    if (TestSuite.CurrentTestContainer.IsSmartFolder)
                    {
                        type = _isTestCaseOrDescendant ? TestItemType.Step : TestItemType.Suite;
                        attributes.Add(new ItemAttribute { Value = "Smart folder" });
                    }
                    else
                    {
                        type = TestItemType.Test;
                        attributes.Add(new ItemAttribute { Value = "Test Case" });
                        _isTestCaseOrDescendant = true;
                    }

                    description = DescriptionForCurrentContainer();
                    break;

                case SMARTFOLDER_DATAITERATION:
                    type = _isTestCaseOrDescendant? TestItemType.Step : TestItemType.Suite;
                    name = info["testcontainername"];
                    namePostfix = " (data iteration #" + info["smartfolderdataiteration"] + ")";
                    attributes.Add(new ItemAttribute { Value = "Smart folder" });
                    description = DescriptionForCurrentContainer();
                    break;

                case TESTCASE_DATAITERATION:
                    type = TestItemType.Test;
                    _isTestCaseOrDescendant = true;
                    name = info["testcontainername"];
                    namePostfix = " (data iteration #" + info["testcasedataiteration"] + ")";
                    attributes.Add(new ItemAttribute { Value = "Test Case" });
                    description = DescriptionForCurrentContainer();
                    break;

                case TESTMODULE:
                    type = TestItemType.Step;
                    name = info["modulename"];
                    attributes.Add(new ItemAttribute { Value = "Module" });
                    var currentLeaf = (TestModuleLeaf)TestModuleLeaf.Current;
                    if (currentLeaf.Parent is ModuleGroupNode)
                    {
                        attributes.Add(new ItemAttribute { Key = "Module Group", Value = currentLeaf.Parent.DisplayName });
                    }
                    if (currentLeaf.IsDescendantOfSetupNode)
                    {
                        attributes.Add(new ItemAttribute { Value = "Setup" });
                        type = TestItemType.BeforeMethod;
                    }

                    if (currentLeaf.IsDescendantOfTearDownNode)
                    {
                        attributes.Add(new ItemAttribute { Value = "TearDown" });
                        type = TestItemType.AfterMethod;
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
                Attributes = attributes
            };
            return rq;
        }

        private FinishTestItemRequest DetermineFinishItemRequest(string result /*IDictionary<string, string> info*/)
        {
            Status status;

            switch(result.ToLower())
            {
                case "success":
                    status = Status.Passed;
                    break;
                case "ignored":
                    status = Status.Skipped;
                    break;
                default:
                    status = Status.Failed;
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
                select new ItemAttribute {Key = attr[0], Value = attr[1]}).ToList();

            _launchReporter.Update(new UpdateLaunchRequest { Attributes = attrs });
        }

        private static LogLevel DetermineLogLevel(string levelStr)
        {
            LogLevel level;
            var logLevel = char.ToUpper(levelStr[0]) + levelStr.Substring(1);
            if (Enum.IsDefined(typeof(LogLevel), logLevel))
                level = (LogLevel) Enum.Parse(typeof(LogLevel), logLevel);
            else if (logLevel.Equals("Failure", StringComparison.InvariantCultureIgnoreCase))
                level = LogLevel.Error;
            else if (logLevel.Equals("Warn", StringComparison.InvariantCultureIgnoreCase))
                level = LogLevel.Warning;
            else
                level = LogLevel.Info;
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