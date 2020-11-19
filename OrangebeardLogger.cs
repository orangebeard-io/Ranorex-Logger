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
using System.Text;
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
        private readonly OrangebeardConfiguration config;

        public OrangebeardLogger()
        {
            
            config = new OrangebeardConfiguration();
            _orangebeard = new OrangebeardClient(config);
        }

        public bool PreFilterMessages => false;

        public void Start()
        {
            ItemAttribute skippedIssue = new ItemAttribute
            {
                IsSystem = true,
                Key = "skippedIssue",
                Value = "false"
            };

            _launchReporter = new LaunchReporter(_orangebeard, null, null, new ExtensionManager());
            _launchReporter.Start(new StartLaunchRequest
            {
                StartTime = DateTime.UtcNow,
                Name = config.ProjectName,
                Attributes = new List<ItemAttribute>() { skippedIssue }
            });
        }

        public void End()
        {
            Report.SystemSummary();
            if (_currentReporter != null)
                while (_currentReporter.ParentTestReporter != null)
                {
                    _currentReporter.Finish(new FinishTestItemRequest
                    {
                        Status = Status.Interrupted,
                        EndTime = DateTime.UtcNow
                    });
                    _currentReporter.Sync();
                    _currentReporter = _currentReporter.ParentTestReporter;
                }

            _launchReporter.Finish(new FinishLaunchRequest {EndTime = DateTime.UtcNow});
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
                var dataBytes = ms.ToArray();
                LogToOrangebeard(level, category, message, dataBytes, metaInfos);
            }
        }

        public void LogText(ReportLevel level, string category, string message, bool escape,
            IDictionary<string, string> metaInfos)
        {
            if (category.Equals("System Summary", StringComparison.InvariantCultureIgnoreCase))
                UpdateTestrunWithSystemInfo(message);
            else if (!HandlePotentialStartFinishLog(metaInfos))
                LogToOrangebeard(level, category, message, null, metaInfos);
        }

        private void LogToOrangebeard(ReportLevel level, string category, string message, byte[] attachmentData,
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
            if (attachmentData != null) logRq.Attach = new LogItemAttach("image/jpeg", attachmentData);


            CreateLogItemRequest metaRq = null;
            if (metaInfos.Count >= 1)
            {
                var meta = new StringBuilder().Append("Meta Info:").Append("\r\n");

                foreach (var key in metaInfos.Keys)
                    meta.Append("\t").Append(key).Append(" => ").Append(metaInfos[key]).Append("\r\n");

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

            var type = TestItemType.Step;
            var name = "";
            var namePostfix = "";
            var description = "";

            var attributes = new List<ItemAttribute>();

            //If there is no result key, we need to start an item
            if (!info.ContainsKey("result"))
            {
                switch (info["activity"])
                {
                    case "testsuite":
                        var suite = (TestSuite) TestSuite.Current;
                        type = TestItemType.Suite;
                        name = info["modulename"];
                        attributes.Add(new ItemAttribute {Value = "Suite"});
                        description = suite.Children.First().Comment;
                        break;

                    case "testcontainer":
                        name = info["testcontainername"];
                        if (TestSuite.CurrentTestContainer.IsSmartFolder)
                        {
                            type = TestItemType.Suite;
                            attributes.Add(new ItemAttribute {Value = "Smart folder"});
                        }
                        else
                        {
                            type = TestItemType.Test;
                            attributes.Add(new ItemAttribute {Value = "Test Case"});
                        }

                        description = DescriptionForCurrentContainer();
                        break;

                    case "smartfolder_dataiteration":
                        type = TestItemType.Suite;
                        name = info["testcontainername"];
                        namePostfix = " (data iteration #" + info["smartfolderdataiteration"] + ")";
                        attributes.Add(new ItemAttribute {Value = "Smart folder"});
                        description = DescriptionForCurrentContainer();
                        break;

                    case "testcase_dataiteration":
                        type = TestItemType.Test;
                        name = info["testcontainername"];
                        namePostfix = " (data iteration #" + info["testcasedataiteration"] + ")";
                        attributes.Add(new ItemAttribute {Value = "Test Case"});
                        description = DescriptionForCurrentContainer();
                        break;

                    case "testmodule":
                        type = TestItemType.Step;
                        name = info["modulename"];
                        attributes.Add(new ItemAttribute {Value = "Module"});
                        var currentLeaf = (TestModuleLeaf) TestModuleLeaf.Current;
                        if (currentLeaf.Parent is ModuleGroupNode)
                            attributes.Add(new ItemAttribute
                                {Key = "Module Group", Value = currentLeaf.Parent.DisplayName});
                        if (currentLeaf.IsDescendantOfSetupNode)
                        {
                            attributes.Add(new ItemAttribute {Value = "Setup"});
                            type = TestItemType.BeforeMethod;
                        }

                        if (currentLeaf.IsDescendantOfTearDownNode)
                        {
                            attributes.Add(new ItemAttribute {Value = "TearDown"});
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

                _currentReporter = _currentReporter == null
                    ? _launchReporter.StartChildTestReporter(rq)
                    : _currentReporter.StartChildTestReporter(rq);
            }
            else
            {
                Status status;

                switch (info["result"].ToLower())
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
                

                _currentReporter.Finish(new FinishTestItemRequest
                {
                    EndTime = DateTime.UtcNow,
                    Status = status
                });

                _currentReporter = _currentReporter.ParentTestReporter;
            }

            return true;
        }

        private void LogErrorScreenshots(IEnumerable<IReportItem> reportItems)
        {
            foreach (var reportItem in reportItems)
            {
                if (reportItem.GetType() == typeof(ReportItem))
                {
                    var item = (ReportItem)reportItem;
                    if ((item.Level == ReportLevel.Error || item.Level == ReportLevel.Failure) && item.ScreenshotFileName.Length > 0)
                    {
                        LogData(item.Level, "Screenshot", item.Message, GetImageFromFile(item.ScreenshotFileName), new IndexedDictionary<string, string>());
                    }
                }
                else if (reportItem.GetType() == typeof(Activity) || reportItem.GetType().IsSubclassOf(typeof(Activity)))
                {
                    LogErrorScreenshots(((Activity) reportItem).Children);
                }
            }
        }

        private static Image GetImageFromFile(string itemScreenshotFileName)
        {
            var reportDir = TestSuite.Current.ReportSettings.ReportDirectoryName;

            return Image.FromFile(Directory.GetCurrentDirectory() +  
                                     "//" + reportDir + 
                                     "//" + itemScreenshotFileName);
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
    }
}