using Orangebeard.Client;
using Orangebeard.Client.Entities;
using System;
using System.Collections.Generic;
using ItemAttribute = Orangebeard.Client.Entities.Attribute;

/**
 * This file contains several dummy classes, that imitiate the classes in the old RanorexOrangebeardListener.
 * The purpose of this is to make the old code compile while building the new code.
 * Eventually, all of these must be removed.
 */

namespace RanorexOrangebeardListener
{
    //TODO!- Dummy class, so that the stuff compiles until the old code can be removed completely.
    internal class ExtensionManager { }
    //TODO!- Dummy class, so that the stuff compiles until the old code can be removed completely.
    internal class StartLaunchRequest
    {
        internal DateTime StartTime { get; set; }
        internal string Name { get; set; }
        internal IList<ChangedComponent> ChangedComponents { get; set; }
    }
    //TODO!- Dummy class, so that the stuff compiles until the old code can be removed completely.
    internal class UpdateLaunchRequest
    {
        internal List<ItemAttribute> Attributes;
    }
    //TODO!- Dummy class, so that the stuff compiles until the old code can be removed completely.
    internal class FinishLaunchRequest
    {
        internal DateTime EndTime { get; set; }
    }
    //TODO!- Dummy class, so that the stuff compiles until the old code can be removed completely.
    internal class LaunchReporter
    {
        internal LaunchReporter(OrangebeardV2Client client, string dummy1, string dummy2, ExtensionManager extensionManager) { }
        internal ITestReporter StartChildTestReporter(StartTestItemRequest startTestItemRequest) { return null; }
        internal void Start(StartLaunchRequest startLaunchRequest) { }
        internal void Log(CreateLogItemRequest createLogItemRequest) { }
        internal void Update(UpdateLaunchRequest updateLaunchRequest) { }
        internal void Finish(FinishLaunchRequest finishLaunchRequest) { }
        internal void Sync() { }
    }
    //TODO!- Dummy class, so that the stuff compiles until the old code can be removed completely.
    internal class StartTestItemRequest
    {
        internal TestItemType Type { get; set; }
        internal DateTime StartTime { get; set; }
        internal string Name { get; set; }
        internal string Description { get; set; }
        internal List<ItemAttribute> Attributes { get; set; }
        internal bool HasStats { get; set; }
    }
    //TODO!- Dummy class, so that the stuff compiles until the old code can be removed completely.
    internal class FinishTestItemRequest
    {
        internal DateTime EndTime { get; set; }
        internal Status Status { get; set; }
    }
    //TODO!- Dummy class, so that the stuff compiles until the old code can be removed completely.
    internal class LogItemAttach
    {
        internal string Name { get; set; }
        internal LogItemAttach(string mimeType, byte[] byteData) { }
    }
    //TODO!- Dummy class, so that the stuff compiles until the old code can be removed completely.
    internal class CreateLogItemRequest
    {
        internal DateTime Time { get; set; }
        internal LogLevel Level { get; set; }
        internal string Text { get; set; }
        internal LogItemAttach Attach { get; set; }
    }
    //TODO!- Dummy class, so that the stuff compiles until the old code can be removed completely.
    internal class ITestReporter
    {
        internal ITestReporter ParentTestReporter;
        internal ITestReporter StartChildTestReporter(StartTestItemRequest startTestItemRequest) { return null; }
        internal void Log(CreateLogItemRequest createLogItemRequest) { }
        internal void Finish(FinishTestItemRequest finishTestItemRequest) { }
    }
}