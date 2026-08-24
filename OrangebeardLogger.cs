/*
 * Copyright 2025 Orangebeard.io (https://www.orangebeard.io)
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
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using Orangebeard.Client.V3;
using Orangebeard.Client.V3.OrangebeardConfig;
using DateTime = System.DateTime;
using Attribute = Orangebeard.Client.V3.Entity.Attribute;
using Orangebeard.Client.V3.Entity.TestRun;
using RanorexOrangebeardListener.RunContext;
using Orangebeard.Client.V3.MimeTypes;
using Orangebeard.Client.V3.Entity.Log;
using Orangebeard.Client.V3.Entity;
using Orangebeard.Client.V3.Entity.Attachment;
using Orangebeard.Client.V3.Entity.Suite;
using Orangebeard.Client.V3.Entity.Test;
using Orangebeard.Client.V3.Entity.Step;
using Ranorex.Core.Reporting;
using Ranorex;
using Ranorex.Core.Testing;
using Ranorex.Core;

namespace RanorexOrangebeardListener
{
    // ReSharper disable once UnusedMember.Global
    public class OrangebeardLogger : IReportLogger
    {
        private readonly OrangebeardAsyncV3Client _orangebeard;
        private readonly OrangebeardConfiguration _config;

        // Keyed by a hash of the encoded image bytes, not by filename: Ranorex-initiated LogData
        // calls (e.g. an explicit Report.Screenshot() in test code) never populate an
        // "attachmentFileName" meta key - that's an OrangebeardLogger-only convention set by
        // LogErrorScreenshots below - so a filename-based check can never recognize a screenshot
        // streamed live here as the same one LogErrorScreenshots later re-discovers via
        // ReportItem.ScreenshotFileName. Content hashing works regardless of which path sees the
        // image first.
        private readonly HashSet<string> _reportedScreenshotHashes = new HashSet<string>();

        // Correlates an already-sent plain-text log entry with the Orangebeard logId it was given,
        // keyed by (level, category, message) - the exact triple Ranorex hands to every registered
        // logger for a given ReportItem, so it matches both our own LogText call and the same
        // ReportItem's Level/Category/Message when later found via LogErrorScreenshots. A screenshot
        // discovered for a matching entry is attached to that logId instead of creating a duplicate
        // "[Screenshot]" log. A Stack (not a Queue) so that repeated identical messages resolve to
        // the most recently logged occurrence, which is the more likely match. Cleared whenever the
        // enclosing test/before/after finishes, since a screenshot is never looked up outside the
        // scope of the item that is currently finishing.
        private readonly Dictionary<string, Stack<Guid>> _pendingLogIdsByMessage = new Dictionary<string, Stack<Guid>>();

        /// <summary>
        /// Context information required for properly converting Ranorex items to Orangebeard items.
        /// When converting Ranorex items to Orangebeard items, the context is important.
        /// A Ranorex Smart Folder can become an Orangebeard Suite or an Orangebeard Step, depending on where it appears.
        /// </summary>
        private TypeTree _tree;

        /// <summary>
        /// Indicates if the current Ranorex item maps to an Orangebeard Test, or a descendant of an Orangebeard Test.
        /// </summary>
        private bool _isTestCaseOrDescendant;

        private bool _inProgress;

        private ISet<Attribute> _testRunAttributes;

        private const string FILE_PATH_PATTERN = @"((((?<!\w)[A-Z,a-z]:)|(\.{0,2}\\))([^\b%\/\|:\n<>""']*))";
        private const string TESTSUITE = "testsuite";
        private const string TESTCONTAINER = "testcontainer";
        private const string SMARTFOLDER_DATAITERATION = "smartfolder_dataiteration";
        private const string SMARTFOLDER_RUNITERATION = "smartfolder_runiteration";
        private const string TESTCASE_DATAITERATION = "testcase_dataiteration";
        private const string TESTCASE_RUNITERATION = "testcase_runiteration";
        private const string TESTMODULE = "testmodule";

        public OrangebeardLogger()
        {
            var listenerVersion = typeof(OrangebeardLogger).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                .InformationalVersion.Split('+')[0];

            _config = new OrangebeardConfiguration()
                .WithListenerIdentification(
                    "Ranorex Logger/" + listenerVersion
                );
            _orangebeard = new OrangebeardAsyncV3Client(_config);
        }

        public bool PreFilterMessages => false;

        public void Start()
        {
            if (_orangebeard.TestRunContext() != null) return;
            _testRunAttributes = _config.Attributes ?? new HashSet<Attribute>();

            _ = _orangebeard.StartTestRun(
                new StartTestRun()
                {
                    StartTime = DateTime.UtcNow,
                    TestSetName = _config.TestSetName,
                    Description = _config.Description ?? "",
                    Attributes = _testRunAttributes,
                }
            );
            
            _inProgress = true;
        }

        public void End()
        {
            if (!_inProgress) return; //Already finished

            Report.SystemSummary();
            Task.Run(() => _orangebeard.FinishTestRun(_orangebeard.TestRunContext().TestRun, new FinishTestRun()))
                .Wait();
            _inProgress = false;
            Console.WriteLine("Orangebeard Report finished");
            Report.End(); //Force synchronous finish of any IReportLoggers
        }

        public void LogData(ReportLevel level, string category, string message, object data,
            IDictionary<string, string> metaInfos)
        {
            // Currently only screenshot attachments are supported. Can Ranorex attach anything else?
            if (!(data is Image img)) return;
            if (!TryEncodeAndDedupeScreenshot(img, out var dataBytes)) return;

            metaInfos.TryGetValue("attachmentFileName", out var filename);
            if (filename == null) filename = category + ".jpg"; // Use category as default filename if null

            LogToOrangebeard(level, category, message, dataBytes, "image/jpeg", filename, metaInfos);
        }

        // Encodes the screenshot as JPEG and dedupes by content hash. Returns false (and leaves
        // dataBytes populated but unused by the caller) if this exact image was already sent -
        // shared by LogData (screenshots streamed live via Ranorex's broadcast) and
        // AttachScreenshotToExistingLog (screenshots backfilled by LogErrorScreenshots), which are
        // two independent discovery paths for what can be the same underlying image.
        internal bool TryEncodeAndDedupeScreenshot(Image img, out byte[] dataBytes)
        {
            using (var ms = new MemoryStream())
            {
                img.Save(ms, ImageFormat.Jpeg);
                dataBytes = ms.ToArray();
            }

            string hash;
            using (var sha256 = SHA256.Create())
            {
                hash = Convert.ToBase64String(sha256.ComputeHash(dataBytes));
            }

            return _reportedScreenshotHashes.Add(hash);
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
                // Captured before PopulateAttachmentData can rewrite message (it appends a failure
                // note on an attach error) - the correlation key must match Ranorex's own,
                // unmodified ReportItem.Message, which LogErrorScreenshots reads later.
                var originalMessage = message;

                string attachmentMimeType = null;
                byte[] attachmentData = null;
                string attachmentFileName = null;
                PopulateAttachmentData(ref message, ref attachmentMimeType, ref attachmentData, ref attachmentFileName);
                var logId = LogToOrangebeard(level, category, message, attachmentData, attachmentMimeType,
                    attachmentFileName, metaInfos);

                if (logId.HasValue)
                {
                    var key = LogCorrelationKey(level.Name, category, originalMessage);
                    if (!_pendingLogIdsByMessage.TryGetValue(key, out var stack))
                    {
                        stack = new Stack<Guid>();
                        _pendingLogIdsByMessage[key] = stack;
                    }

                    stack.Push(logId.Value);
                }
            }
        }

        internal static string LogCorrelationKey(string levelName, string category, string message) =>
            levelName + "" + category + "" + message;

        private void PopulateAttachmentData(ref string message, ref string attachmentMimeType,
            ref byte[] attachmentData, ref string attachmentFileName)
        {
            if (_config.FileUploadPatterns == null || _config.FileUploadPatterns.Count == 0)
            {
                //nothing to look for!
                return;
            }

            var match = Regex.Match(message, FILE_PATH_PATTERN);
            if (!match.Success) return; //Look only at first match, as we support max 1 attachment per log entry
            var filePath = match.Value;
            foreach (var pattern in _config.FileUploadPatterns)
            {
                var patternMatch = Regex.Match(filePath, pattern);
                if (!patternMatch.Success ||
                    !Path.IsPathRooted(
                        filePath))
                    continue; //Ignore relative paths, as they are likely user-generated and tool dependent in html logs
                try
                {
                    attachmentData = File.ReadAllBytes(filePath);
                    attachmentFileName = Path.GetFileName(filePath);
                    attachmentMimeType = MimeTypeMap.GetMimeType(Path.GetExtension(filePath));
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

        private Guid? LogToOrangebeard(ReportLevel level, string category, string message, byte[] attachmentData,
            string mimeType, string attachmentFileName,
            IDictionary<string, string> metaInfos)
        {
            //Don't send data if no test has been started yet.
            if (!_orangebeard.TestRunContext().ActiveTest().HasValue) return null;

            if (category == null)
            {
                category = string.Empty;
            }

            var logItem = new Log
            {
                TestRunUUID = _orangebeard.TestRunContext().TestRun,
                TestUUID = _orangebeard.TestRunContext().ActiveTest().Value,
                StepUUID = _orangebeard.TestRunContext().ActiveStep(),
                LogTime = DateTime.UtcNow,
                LogLevel = DetermineLogLevel(level.Name),
                Message = "[" + category + "]: " + message,
                LogFormat = LogFormat.PLAIN_TEXT
            };

            var logId = _orangebeard.Log(logItem);

            if (attachmentData != null && attachmentFileName != null)
            {
                var attachment = new Attachment
                {
                    File = new AttachmentFile
                    {
                        Name = attachmentFileName,
                        Content = attachmentData,
                        ContentType = mimeType,
                    },
                    MetaData = new AttachmentMetaData
                    {
                        TestRunUUID = _orangebeard.TestRunContext().TestRun,
                        TestUUID = _orangebeard.TestRunContext().ActiveTest().Value,
                        StepUUID = _orangebeard.TestRunContext().ActiveStep(),
                        LogUUID = logId,
                        AttachmentTime = DateTime.UtcNow
                    }
                };
                _ = _orangebeard.SendAttachment(attachment);
            }

            if (MeetsMinimumSeverity(DetermineLogLevel(level.Name), LogLevel.WARN) && metaInfos.Count >= 1)
            {
                LogMetaInfo(metaInfos);
            }

            return logId;
        }

        // Attaches a screenshot to a log entry that was already sent, instead of creating a new
        // "[Screenshot]" log for text that's already in the report. Orangebeard associates an
        // attachment with its log purely via AttachmentMetaData.LogUUID, so no new Log is needed.
        private void AttachScreenshotToExistingLog(Guid logId, Image img, string attachmentFileName)
        {
            if (!TryEncodeAndDedupeScreenshot(img, out var dataBytes)) return;

            var attachment = new Attachment
            {
                File = new AttachmentFile
                {
                    Name = attachmentFileName,
                    Content = dataBytes,
                    ContentType = "image/jpeg",
                },
                MetaData = new AttachmentMetaData
                {
                    TestRunUUID = _orangebeard.TestRunContext().TestRun,
                    TestUUID = _orangebeard.TestRunContext().ActiveTest().Value,
                    StepUUID = _orangebeard.TestRunContext().ActiveStep(),
                    LogUUID = logId,
                    AttachmentTime = DateTime.UtcNow
                }
            };
            _ = _orangebeard.SendAttachment(attachment);
        }

        private void LogMetaInfo(IDictionary<string, string> metaInfos)
        {
            var meta = new StringBuilder().Append("Meta Info:").Append("\r\n");

            foreach (var key in metaInfos.Keys)
            {
                meta.Append("\t").Append(key).Append(" => ").Append(metaInfos[key]).Append("\r\n");
            }

            var metaLogItem = new Log
            {
                TestRunUUID = _orangebeard.TestRunContext().TestRun,
                TestUUID = _orangebeard.TestRunContext().ActiveTest().Value,
                StepUUID = _orangebeard.TestRunContext().ActiveStep(),
                LogTime = DateTime.UtcNow,
                LogLevel = LogLevel.DEBUG,
                Message = meta.ToString(),
                LogFormat = LogFormat.PLAIN_TEXT
            };

            _orangebeard.Log(metaLogItem);
        }

        private bool HandlePotentialStartFinishLog(IDictionary<string, string> info)
        {
            //check if there is an active (root) suite, otherwise make sure it has started
            var forcedSynchronization = EnsureReportingIsInSync(info);

            if (!info.TryGetValue("activity", out var activity)) return false;

            //If there is no result key and we have not auto-populated suite and item, we need to start an item
            if (!info.ContainsKey("result") && !forcedSynchronization)
            {
                var creationData = DetermineStartTestItemRequest(activity, info);
                CreateReportItem(creationData);
            }
            else
            {
                FinishItemWithStatus(DetermineFinishedItemStatus(info["result"]));
            }

            return true;
        }

        private void FinishItemWithStatus(TestStatus status)
        {
            switch (_tree.Type)
            {
                case "step":
                    _orangebeard.FinishStep(_orangebeard.TestRunContext().ActiveStep().Value, new FinishStep
                    {
                        TestRunUUID = _orangebeard.TestRunContext().TestRun,
                        Status = status,
                        EndTime = DateTime.UtcNow
                    });

                    break;
                case "test":
                case "before":
                case "after":
                    _orangebeard.FinishTest(_orangebeard.TestRunContext().ActiveTest().Value, new FinishTest
                    {
                        TestRunUUID = _orangebeard.TestRunContext().TestRun,
                        Status = status,
                        EndTime = DateTime.UtcNow
                    });
                    _isTestCaseOrDescendant = false;
                    // A screenshot is only ever looked up while the item that logged it is still
                    // finishing (see LogErrorScreenshots), so pending correlations from this test
                    // can never be matched again once it closes.
                    _pendingLogIdsByMessage.Clear();
                    break;
                case "suite":
                    _orangebeard.TestRunContext().FinishSuite(_orangebeard.TestRunContext().ActiveSuite().Value);
                    break;
            }

            _tree = _tree.GetParent();
        }

        private bool EnsureReportingIsInSync(IDictionary<string, string> info)
        {
            if (_orangebeard.TestRunContext().activeSuiteIds.Count > 0) return false;

            var creationData = DetermineStartTestItemRequest(info["activity"], info);

            if (creationData.Type != "suite")
            {
                var suiteComment = ((TestSuite)TestSuite.Current).Children[0].Comment;
                suiteComment = suiteComment.Length > 1024 ? suiteComment.Substring(0, 1021) + "..." : suiteComment;
                var suiteDescription = PrependParameters(FormatContainerParameters(TestSuite.Current), suiteComment);

                //start toplevel suite first
                var suite = new StartSuite
                {
                    TestRunUUID = _orangebeard.TestRunContext().TestRun,
                    SuiteNames = new List<string> { ((TestSuite)TestSuite.Current).Children[0].Name },
                    Description = suiteDescription,
                    Attributes = new HashSet<Attribute>(),
                };

                UpdateTree(suite.SuiteNames[suite.SuiteNames.Count - 1], "suite");
                _ = _orangebeard.StartSuite(suite)[0];
            }

            // start current item
            CreateReportItem(creationData);
            return true;
        }

        private void CreateReportItem(ItemCreationData creationData)
        {
            UpdateTree(creationData.Name, creationData.Type);

            switch (creationData.Type)
            {
                case "test":
                case "before":
                case "after":
                    _orangebeard.StartTest(new StartTest
                    {
                        TestRunUUID = _orangebeard.TestRunContext().TestRun,
                        SuiteUUID = _orangebeard.TestRunContext().ActiveSuite().Value,
                        TestName = creationData.Name,
                        TestType = (TestType)Enum.Parse(typeof(TestType), creationData.Type, true),
                        Description = creationData.Description,
                        Attributes = creationData.Attributes,
                        StartTime = creationData.StartTime
                    });
                    break;
                case "step":
                    _orangebeard.StartStep(new StartStep
                    {
                        TestRunUUID = _orangebeard.TestRunContext().TestRun,
                        TestUUID = _orangebeard.TestRunContext().ActiveTest().Value,
                        ParentStepUUID = _orangebeard.TestRunContext().ActiveStep(),
                        StepName = creationData.Name,
                        Description = creationData.Description,
                        StartTime = creationData.StartTime
                    });
                    break;
                case "suite":
                    _orangebeard.StartSuite(new StartSuite
                    {
                        TestRunUUID = _orangebeard.TestRunContext().TestRun,
                        ParentSuiteUUID = _orangebeard.TestRunContext().ActiveSuite(),
                        SuiteNames = new List<string> { creationData.Name },
                        Description = creationData.Description,
                        Attributes = creationData.Attributes,
                    });
                    break;
            }
        }

        private void UpdateTree(string name, string type)
        {
            var activity = ActivityStack.Current;
            _tree = _tree == null ? new TypeTree(type, name, activity) : _tree.Add(type, name, activity);

            if (type == "test")
            {
                _isTestCaseOrDescendant = true;
            }
        }

        private ItemCreationData DetermineStartTestItemRequest(string activityType, IDictionary<string, string> info)
        {
            var type = "step";
            var name = "";
            var namePostfix = "";
            var description = "";

            var attributes = new HashSet<Attribute>();
            switch (activityType)
            {
                case TESTSUITE:
                    var suite = (TestSuite)TestSuite.Current;
                    type = "suite";
                    name = info["modulename"];
                    description = PrependParameters(FormatContainerParameters(TestSuite.Current), suite.Children[0].Comment);
                    break;

                case TESTCONTAINER:
                    name = info["testcontainername"];
                    if (TestSuite.CurrentTestContainer.IsSmartFolder)
                    {
                        type = _isTestCaseOrDescendant ? "step" : "suite";
                    }
                    else
                    {
                        type = "test";
                    }

                    description = DescriptionForCurrentContainer();
                    break;

                case SMARTFOLDER_DATAITERATION:
                    type = _isTestCaseOrDescendant ? "step" : "suite";
                    name = info["testcontainername"];
                    namePostfix = " (data iteration #" + info["smartfolderdataiteration"] + ")";
                    description = DescriptionForCurrentContainer();
                    break;
                
                case SMARTFOLDER_RUNITERATION:
                    type = _isTestCaseOrDescendant ? "step" : "suite";
                    name = info["testcontainername"];
                    namePostfix = " (iteration #" + info["smartfolderruniteration"] + ")";
                    description = DescriptionForCurrentContainer();
                    break;

                case TESTCASE_DATAITERATION:
                    type = "test";
                    name = info["testcontainername"];
                    namePostfix = " (data iteration #" + info["testcasedataiteration"] + ")";
                    description = DescriptionForCurrentContainer();
                    break;
                
                case TESTCASE_RUNITERATION:
                    type = "test";
                    name = info["testcontainername"];
                    namePostfix = " (iteration #" + info["testcaseruniteration"] + ")";
                    description = DescriptionForCurrentContainer();
                    break;

                case TESTMODULE:
                    type = "step";
                    name = info["modulename"];
                    var currentLeaf = (TestModuleLeaf)TestModuleLeaf.Current;
                    if (currentLeaf.IsDescendantOfSetupNode)
                    {
                        type = _isTestCaseOrDescendant ? "step" : "before";
                    }

                    if (currentLeaf.IsDescendantOfTearDownNode)
                    {
                        type = _isTestCaseOrDescendant ? "step" : "after";
                    }
                    description = currentLeaf.Comment;
                    break;
            }

            if (activityType != TESTSUITE)
            {
                description = PrependParameters(FormatContainerParameters(TestSuite.CurrentTestContainer), description);
            }

            var data = new ItemCreationData
            {
                StartTime = DateTime.UtcNow,
                Type = type,
                Name = name + namePostfix,
                Description = description,
                Attributes = attributes,
            };
            return data;
        }

        internal static string FormatContainerParameters(ITestContainer container)
        {
            return FormatParameters(container?.Parameters);
        }

        internal static string FormatContainerParameters(ITestSuite suite)
        {
            return FormatParameters(suite?.Parameters);
        }

        internal static string FormatParameters(IDictionary<string, string> parameters)
        {
            if (parameters == null || parameters.Count == 0) return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine("| Parameter | Value |");
            sb.AppendLine("|---|---|");
            foreach (var key in parameters.Keys)
            {
                sb.AppendLine("| " + key + " | " + parameters[key] + " |");
            }
            return sb.ToString().TrimEnd();
        }

        internal static string PrependParameters(string parametersMarkdown, string description)
        {
            if (string.IsNullOrEmpty(parametersMarkdown)) return description;
            if (string.IsNullOrEmpty(description)) return parametersMarkdown;
            return parametersMarkdown + "\r\n\r\n" + description;
        }

        private TestStatus DetermineFinishedItemStatus(string result /*IDictionary<string, string> info*/)
        {
            TestStatus status;

            switch (result.ToLower())
            {
                case "success":
                    status = TestStatus.PASSED;
                    break;
                case "ignored":
                    status = TestStatus.SKIPPED;
                    break;
                default:
                    status = TestStatus.FAILED;
                    // Use the Activity captured when this item started, not ActivityStack.Current:
                    // Ranorex already pops the stack to the parent before this finish event reaches
                    // us, so ActivityStack.Current.Children would pull in every sibling item's
                    // screenshots too (see TypeTree.RanorexActivity).
                    LogErrorScreenshots(_tree.RanorexActivity?.Children ?? Array.Empty<IReportItem>());
                    break;
            }

            return status;
        }

        private void LogErrorScreenshots(IEnumerable<IReportItem> reportItems)
        {
            foreach (var reportItem in reportItems)
            {
                if (reportItem is ReportItem item)
                {
                    if (item.ScreenshotFileName != null)
                    {
                        try
                        {
                            var screenshotPath = TestReport.ReportEnvironment.ReportFileDirectory + "\\" +
                                                  item.ScreenshotFileName;
                            var key = LogCorrelationKey(item.Level.Name, item.Category, item.Message);

                            if (_pendingLogIdsByMessage.TryGetValue(key, out var stack) && stack.Count > 0)
                            {
                                // item.Message was already sent as its own log entry - attach the
                                // screenshot there instead of logging the same text a second time.
                                var logId = stack.Pop();
                                if (stack.Count == 0) _pendingLogIdsByMessage.Remove(key);
                                AttachScreenshotToExistingLog(logId, Image.FromFile(screenshotPath),
                                    Path.GetFileName(item.ScreenshotFileName));
                            }
                            else
                            {
                                LogData(
                                    item.Level,
                                    "Screenshot",
                                    item.Message + "\r\n" +
                                    "Screenshot file name: " + item.ScreenshotFileName,
                                    Image.FromFile(screenshotPath),
                                    new IndexedDictionary<string, string>()
                                    {
                                        new KeyValuePair<string, string>("attachmentFileName",
                                            Path.GetFileName(item.ScreenshotFileName))
                                    });
                            }
                        }
                        catch (Exception e)
                        {
                            LogToOrangebeard(
                                item.Level,
                                "Screenshot", "Exception getting screenshot: " + e.Message + "\r\n" +
                                              e.GetType() + ": " + e.StackTrace,
                                null, null, null,
                                new IndexedDictionary<string, string>()
                            );
                        }
                    }
                }
                else if (reportItem is Activity activity)
                {
                    LogErrorScreenshots(activity.Children);
                }
            }
        }

        private static string DescriptionForCurrentContainer()
        {
            var entry = (TestSuiteEntry)TestSuite.CurrentTestContainer;
            return StripHtml(entry.Comment);
        }

        internal static string StripHtml(string str)
        {
            var cleanStr = str.Contains("<")
                ? Regex.Replace(ReplaceHtmlParagraphsAndLinebreaks(str), "<[a-zA-Z/].*?>", string.Empty)
                : str;
            return Regex.IsMatch(cleanStr, "&[a-z]+;") ? HttpUtility.HtmlDecode(cleanStr) : cleanStr;
        }

        private static string ReplaceHtmlParagraphsAndLinebreaks(string str) =>
            Regex.Replace(str, @"<(br|BR|\/[pP]).*?>", "\r\n");


        private void UpdateTestrunWithSystemInfo(string message)
        {
            if (Environment.GetEnvironmentVariable("orangebeard.ranorex.systemattributes") == null) return;

            var ranorexAttributesToInclude =
                Environment.GetEnvironmentVariable("orangebeard.ranorex.systemattributes")?.Split(';');

            var launchAttrEntries = message.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

            var attrs = (
                from entry in launchAttrEntries
                select entry.Split(new[] { ": " }, StringSplitOptions.None)
                into attr
                where ranorexAttributesToInclude != null && attr.Length > 1 &&
                      ranorexAttributesToInclude.Any(r => r.Trim().Equals(attr[0], StringComparison.OrdinalIgnoreCase))
                select new Attribute { Key = attr[0], Value = attr[1] }).Concat(_testRunAttributes).ToList();

            _orangebeard.UpdateTestRun(_orangebeard.TestRunContext().TestRun, new UpdateTestRun
            {
                Attributes = new HashSet<Attribute>(attrs)
            });
        }

        internal static LogLevel DetermineLogLevel(string levelStr)
        {
            var logLevel = levelStr.ToUpper();
            if (Enum.TryParse(logLevel, true, out LogLevel level)) return level;

            level = logLevel.Equals("Failure", StringComparison.InvariantCultureIgnoreCase)
                ? LogLevel.ERROR
                : LogLevel.INFO;

            return level;
        }

        /// Determine if a LogLevel has at least a minimum severity.
        /// For example, <code>LogLevel.Error</code> is more severe than <code>LogLevel.Warning</code>; so <code>MeetsMinimumSeverity(Error, Warning)</code> is <code>true</code>.
        /// Similarly, <code>LogLevel.Warning</code> is at severity level <code>Warning</code>; so <code>MeetsMinimumSeverity(Warning, Warning)</code> is also <code>true</code>.
        /// But <code>LogLevel.Info</code> is less severe than <code>LogLevel.Warning</code>, so <code>MeetsMinimumSeverity(Warning, Info)</code> is <code>false</code>.
        /// <param name="level">The LogLevel whose severity must be checked.</param>
        /// <param name="threshold">The severity level to check against.</param>
        /// <returns>The boolean value <code>true</code> if and only if the given log level has at least the same level of severity as the threshold value.</returns>
        internal static bool MeetsMinimumSeverity(LogLevel level, LogLevel threshold)
        {
            return ((int)level) <= (int)threshold;
        }
    }
}