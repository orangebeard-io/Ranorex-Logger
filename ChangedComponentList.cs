using Newtonsoft.Json.Linq;
using Orangebeard.Client.Abstractions.Models;
using System;
using System.Collections.Generic;
using System.IO;
using JArray = Newtonsoft.Json.Linq.JArray;
using JTokenType = Newtonsoft.Json.Linq.JTokenType;
using JToken = Newtonsoft.Json.Linq.JToken;

namespace RanorexOrangebeardListener
{
    public class ChangedComponentsList
    {
        internal static ISet<ChangedComponent> Load()
        {
            ISet<ChangedComponent> changedComponents = new HashSet<ChangedComponent>();

            string changedComponentsJson = Environment.GetEnvironmentVariable(OrangebeardLogger.CHANGED_COMPONENTS_VARIABLE);
            if (string.IsNullOrEmpty(changedComponentsJson))
            {
                if (File.Exists(OrangebeardLogger.CHANGED_COMPONENTS_PATH))
                {
                    changedComponentsJson = File.ReadAllText(OrangebeardLogger.CHANGED_COMPONENTS_PATH);
                }
            }

            if (!string.IsNullOrWhiteSpace(changedComponentsJson))
            {
                changedComponents = ParseJson(changedComponentsJson);
            }

            return changedComponents;
        }

        /// <summary>
        /// Parse a JSON array to a set of (componentName, componentVersion) pairs.
        /// For example, suppose you have the JSON array [{"componentName":"barber","componentVersion":"2022.1.1.35"},{"componentName":"shaver","componentVersion":"2019.2.1.24"}].
        /// This will result in a set of two pairs: [("barber","2022.1.1.35"),("shaver","2019.2.1.24")].
        /// Elements in the JSON array <b>must</b> contain a pair that starts with "componentName" and a pair that starts with "componentVersion". However, the value for "componentVersion" is allowed to be null.
        /// So [{"componentName":"barber","componentVersion":null}] is legal; the version is allowed to be null.
        /// But [{"componentName":"barber"}] is illegal; there should be a "componentVersion".
        /// Illegal elements will be ignored.
        /// It is allowed to add more elements in an element than componentVersion and componentName; those extra elements will be ignored, but may be processed in the future.
        /// For example, [{"componentName":"barber","componentVersion":"2022.1.1.35", "componentTool":"shavingCream"}] will simply result in the pair ("barber","2022.1.1.35").
        /// </summary>
        /// <returns>A set of pairs, where each pair is a combination of a component name and a component version.</returns>
        public static ISet<ChangedComponent> ParseJson(string json)
        {
            JArray jsonArray = JArray.Parse(json);
            var pairs = new HashSet<ChangedComponent>();

            foreach (JToken member in jsonArray)
            {
                JToken jTokenName = member["componentName"];
                JToken jTokenVersion = member["componentVersion"];

                if (jTokenName != null && jTokenName.Type == JTokenType.String && jTokenVersion != null)
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
    }
}