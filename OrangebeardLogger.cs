/*
 * Copyright 2020 Orangebeard.io (https://www.orangebeard.io)
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using Ranorex;
using Ranorex.Core;
using Ranorex.Core.Testing;
using RanorexOrangebeardListener.Requests;
using ReportPortal.Client;
using ReportPortal.Client.Abstractions.Models;
using ReportPortal.Client.Abstractions.Requests;
using ReportPortal.Client.Abstractions.Responses;
using ReportPortal.Shared.Extensibility;
using ReportPortal.Shared.Reporter;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;

namespace RanorexOrangebeardListener
{
    public class OrangebeardLogger : IReportLogger
    {
        private readonly Service orangebeard;
        private ITestReporter currentReporter;
        private LaunchReporter launchReporter;

        public OrangebeardLogger()
        {
            CheckEnvVar("orangebeard.token");
            CheckEnvVar("orangebeard.endpoint");
            CheckEnvVar("orangebeard.project");
            CheckEnvVar("orangebeard.testrun");

            string token = Environment.GetEnvironmentVariable("orangebeard.token");
            string endpoint = Environment.GetEnvironmentVariable("orangebeard.endpoint");
            string project = Environment.GetEnvironmentVariable("orangebeard.project");

            orangebeard = new Service(new Uri(endpoint), project, token);
        }


        public bool PreFilterMessages => false;

        public void Start()
        {
            launchReporter = new LaunchReporter(orangebeard, null, null, new ExtensionManager());
            launchReporter.Start(new StartLaunchRequest
            {
                StartTime = System.DateTime.UtcNow,
                Name = Environment.GetEnvironmentVariable("orangebeard.testrun"),

            });
        }

        public void End()
        {
            Report.SystemSummary();
            if (currentReporter != null)
            {
                while (currentReporter.ParentTestReporter != null)
                {
                    currentReporter.Finish(new FinishTestItemRequest
                    {
                        Status = Status.Interrupted,
                        EndTime = System.DateTime.UtcNow
                    });
                    currentReporter.Sync();
                    currentReporter = currentReporter.ParentTestReporter;
                }
            }

            launchReporter.Finish(new FinishLaunchRequest { EndTime = System.DateTime.UtcNow });
            launchReporter.Sync();
        }

        public void LogData(ReportLevel level, string category, string message, object data, IDictionary<string, string> metaInfos)
        {
            //Currently only screenshot attachments are supported. Can ranorex attach anything else?
            byte[] dataBytes = null;
            if (data is Bitmap)
            {
                dataBytes = ByteArrayForImage((Bitmap)data);
                LogToOrangebeard(level, category, message, dataBytes, metaInfos);
            }
        }

        public void LogText(ReportLevel level, string category, string message, bool escape, IDictionary<string, string> metaInfos)
        {
            if (category.Equals("System Summary", StringComparison.InvariantCultureIgnoreCase))
            {
                updateTestrunWithSystemInfo(message);
            }
            else if (!handlePotentialStartFinishLog(metaInfos))
            {
                LogToOrangebeard(level, category, message, null, metaInfos);
            }
        }

        private void LogToOrangebeard(ReportLevel level, string category, string message, byte[] attachmentData, IDictionary<string, string> metaInfos)
        {
            CreateLogItemRequest rq = CreateLogItemRequest(category, message, attachmentData, DetermineLogLevel(level.Name));
            CreateLogItemRequest metaRq = null;
            if (metaInfos.Count >= 1)
            {
                metaRq = CreateMetaDataLogItemRequest(metaInfos);
            }

            if (currentReporter == null)
            {
                launchReporter.Log(rq);
                if (metaRq != null) { launchReporter.Log(metaRq); }
            }
            else
            {
                currentReporter.Log(rq);
                if (metaRq != null) { currentReporter.Log(metaRq); }
            }
        }

        private bool handlePotentialStartFinishLog(IDictionary<string, string> info)
        {
            if (info.ContainsKey("activity"))
            {
                TestItemType type = TestItemType.Step;
                string name = "";
                string name_postfix = "";
                string description = "";

                TestSuiteEntry entry;

                List<ItemAttribute> attributes = new List<ItemAttribute>();

                //If ther is no result key, we need to start an item
                if (!info.ContainsKey("result"))
                {
                    switch (info["activity"])
                    {
                        case "testsuite":
                            TestSuite suite = (TestSuite)TestSuite.Current;
                            type = TestItemType.Suite;
                            name = info["modulename"];
                            attributes.Add(new ItemAttribute { Value = "Suite" });
                            description = suite.Children.First().Comment;
                            break;

                        case "testcontainer":
                            entry = (TestSuiteEntry)TestSuite.CurrentTestContainer;
                            name = info["testcontainername"];
                            if (TestSuite.CurrentTestContainer.IsSmartFolder)
                            {
                                type = TestItemType.Suite;
                                attributes.Add(new ItemAttribute { Value = "Smart folder" });
                            }
                            else
                            {
                                type = TestItemType.Test;
                                attributes.Add(new ItemAttribute { Value = "Test Case" });
                            }
                            description = entry.Comment;
                            break;

                        case "smartfolder_dataiteration":
                            entry = (TestSuiteEntry)TestSuite.CurrentTestContainer;
                            type = TestItemType.Suite;
                            name = info["testcontainername"];
                            name_postfix = " (data iteration #" + info["smartfolderdataiteration"] + ")";
                            attributes.Add(new ItemAttribute { Value = "Smart folder" });
                            description = entry.Comment;
                            break;

                        case "testcase_dataiteration":
                            entry = (TestSuiteEntry)TestSuite.CurrentTestContainer;
                            type = TestItemType.Test;
                            name = info["testcontainername"];
                            name_postfix = " (data iteration #" + info["testcasedataiteration"] + ")";
                            attributes.Add(new ItemAttribute { Value = "Test Case" });
                            description = entry.Comment;
                            break;

                        case "testmodule":
                            type = TestItemType.Step;
                            name = info["modulename"];
                            attributes.Add(new ItemAttribute { Value = "Module" });
                            TestModuleLeaf currentLeaf = (TestModuleLeaf)TestModuleLeaf.Current;
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

                    StartTestItemRequest rq = new StartTestItemRequest
                    {
                        StartTime = System.DateTime.UtcNow,
                        Type = type,
                        Name = name + name_postfix,
                        Description = description,
                        Attributes = attributes
                    };

                    currentReporter = currentReporter == null ? launchReporter.StartChildTestReporter(rq) : currentReporter.StartChildTestReporter(rq);
                }
                else
                {
                    currentReporter.Finish(new FinishTestItemRequest
                    {
                        EndTime = System.DateTime.UtcNow,
                        Status = determineStatus(info["result"])
                    });
                    currentReporter = currentReporter.ParentTestReporter;
                }
                return true;
            }
            return false;
        }

        private Status determineStatus(string statusStr)
        {
            Status status;
            switch (statusStr.ToLower())
            {
                case "success":
                    status = Status.Passed;
                    break;
                case "ignored":
                    status = Status.Skipped;
                    break;
                default:
                    status = Status.Failed;
                    break;
            }
            return status;
        }

        private void updateTestrunWithSystemInfo(string message)
        {
            LaunchResponse launchInfo = orangebeard.Launch.GetAsync(launchReporter.Info.Uuid).GetAwaiter().GetResult();

            List<ItemAttribute> attrs = new List<ItemAttribute>();
            string[] launchAttrEntries = message.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

            foreach (string entry in launchAttrEntries)
            {
                string[] attr = entry.Split(new[] { ": " }, StringSplitOptions.None);
                if (attr.Length > 1 && !attr[0].Contains("displays") && !attr[0].Contains("CPUs") && !attr[0].Contains("Ranorex version") && !attr[0].Contains("Memory") && !attr[0].Contains("Runtime version"))
                {
                    attrs.Add(new ItemAttribute { Key = attr[0], Value = attr[1] });
                }
            }

            orangebeard.Launch.UpdateAsync(launchInfo.Id, new UpdateOrangebeardLaunchRequest { Attributes = attrs });
        }

        private string ConstructMetaDataLogMessage(IDictionary<string, string> metaInfos)
        {
            var meta = new StringBuilder();
            meta.Append("Meta Info:").Append("\r\n");

            foreach (var key in metaInfos.Keys)
                meta.Append("\t").Append(key).Append(" => ").Append(metaInfos[key]).Append("\r\n");

            return meta.ToString();
        }
        private static LogLevel DetermineLogLevel(string levelStr)
        {
            LogLevel level;
            var logLevel = char.ToUpper(levelStr[0]) + levelStr.Substring(1);
            if (Enum.IsDefined(typeof(LogLevel), logLevel))
            {
                level = (LogLevel)Enum.Parse(typeof(LogLevel), logLevel);
            }
            else if (logLevel.Equals("Failure", StringComparison.InvariantCultureIgnoreCase))
            {
                level = LogLevel.Error;
            }
            else if (logLevel.Equals("Warn", StringComparison.InvariantCultureIgnoreCase))
            {
                level = LogLevel.Warning;
            }
            else
            {
                level = LogLevel.Info;
            }
            return level;
        }
        private CreateLogItemRequest CreateLogItemRequest(string category, string message, byte[] data, LogLevel level)
        {
            CreateLogItemRequest rq = new CreateLogItemRequest();
            rq.Time = System.DateTime.UtcNow;
            rq.Level = level;
            rq.Text = "[" + category + "]: " + message;

            if (data != null) rq.Attach = new LogItemAttach("image/jpeg", data);
            return rq;
        }

        private CreateLogItemRequest CreateMetaDataLogItemRequest(IDictionary<string, string> metaInfo)
        {

            CreateLogItemRequest rq = new CreateLogItemRequest();
            rq.Time = System.DateTime.UtcNow;
            rq.Level = LogLevel.Debug;
            rq.Text = ConstructMetaDataLogMessage(metaInfo);

            return rq;
        }


        private static byte[] ByteArrayForImage(Bitmap data)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                data.Save(ms, ImageFormat.Bmp);
                return ms.ToArray();
            }
        }

        private static void CheckEnvVar(string name)
        {
            if (Environment.GetEnvironmentVariable(name) == null) { throw new MissingEnvironmentVariableException(name); }
        }
    }
}
