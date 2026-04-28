// See WPRP schema here: https://learn.microsoft.com/en-us/windows-hardware/test/wpt/wprcontrolprofiles-schema

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;

namespace InstantTraceViewerUI.Etw
{
    enum LoggingMode
    {
        File,
        Memory
    }

    enum DetailLevel
    {
        Light,
        Verbose
    }

    internal static class XmlExtensions
    {
        public static XElement RequiredElement(this XElement parent, string name)
        {
            XElement child = parent.Element(name);
            if (child == null)
            {
                throw new Exception($"Missing required element '{name}' in '{parent.Name}'");
            }
            return child;
        }

        public static XAttribute RequiredAttribute(this XElement element, string name)
        {
            XAttribute attr = element.Attribute(name);
            if (attr == null)
            {
                throw new Exception($"Missing required attribute '{name}' in element '{element.Name}'");
            }
            return attr;
        }
    }

    // https://learn.microsoft.com/en-us/windows-hardware/test/wpt/1-collector-definitions
    internal class CollectorBase
    {
        public string Id { get; private set; }
        public string Name { get; private set; }
        public uint? BufferSize { get; private set; }
        public uint? Buffers { get; private set; }

        protected void ParseXml(XElement collectorEl)
        {
            Id = (string)collectorEl.RequiredAttribute("Id");
            Name = (string)collectorEl.RequiredAttribute("Name");
            BufferSize = (uint?)collectorEl.Element("BufferSize")?.Attribute("Value");
            Buffers = (uint?)collectorEl.Element("Buffers")?.Attribute("Value");
        }
    }

    internal class SystemCollector : CollectorBase
    {
        public static SystemCollector Parse(XElement eventCollectorEl)
        {
            var collector = new SystemCollector();
            collector.ParseXml(eventCollectorEl);
            return collector;
        }
    }

    // https://learn.microsoft.com/en-us/windows-hardware/test/wpt/1-collector-definitions
    internal class EventCollector : CollectorBase
    {
        public static EventCollector Parse(XElement eventCollectorEl)
        {
            var collector = new EventCollector();
            collector.ParseXml(eventCollectorEl);
            return collector;
        }
    }

    internal class SystemProvider
    {
        public string Id { get; private set; }
        public List<string> Keywords { get; private set; }

        public static SystemProvider Parse(XElement systemProviderEl)
        {
            return new SystemProvider
            {
                Id = (string)systemProviderEl.RequiredAttribute("Id"),
                Keywords = systemProviderEl.Elements("Keywords")
                    .SelectMany(k => k.Elements("Keyword")
                        .Select(k => (string)k.RequiredAttribute("Value"))).ToList()
            };
        }
    }

    internal class EventProvider
    {
        public string Id { get; private set; }
        public string Name { get; private set; }
        public uint? Level { get; private set; }
        public ulong? Keywords { get; private set; }

        public static EventProvider Parse(XElement eventProviderEl)
        {
            // TODO: Operation (OperationEnumeration)
            var keywordCollection = eventProviderEl.Elements("Keywords")
                .SelectMany(k => k.Elements("Keyword")
                    .Select(k =>
                    {
                        // If starts with 0x then parse as hex, otherwise parse as decimal
                        string value = (string)k.RequiredAttribute("Value");
                        return value.StartsWith("0x") ? Convert.ToUInt64(value, 16) : Convert.ToUInt64(value);
                    })).ToList();

            ulong? keywords = keywordCollection.Any() ? keywordCollection.Aggregate(0ul, (acc, k) => acc | k) : null;

            // TODO: CaptureStateOnSave, CaptureStateOnStart, EventFilters, EventKey, NonPagedMemory
            return new EventProvider
            {
                Id = (string)eventProviderEl.RequiredAttribute("Id"),
                Name = (string)eventProviderEl.RequiredAttribute("Name"),
                Level = (uint?)eventProviderEl.Attribute("Level"),
                Keywords = keywords
            };
        }
    }

    internal class WprpProfile
    {
        // Some of these WPRP kernel keywords do not seem to be supported by the Microsoft.Diagnostics.Tracing library.
        internal readonly static Dictionary<string, KernelTraceEventParser.Keywords> KernelKeywordMap = new Dictionary<string, KernelTraceEventParser.Keywords> {
            // { "AllFaults", ??? },
            { "Alpc", KernelTraceEventParser.Keywords.AdvancedLocalProcedureCalls },
            // { "AntiStarvation", ??? },
            // { "CompactCSwitch", ??? },
            // { "ContiguousMemorygeneration", ??? },
            // { "CpuConfig", ??? },
            { "CSwitch", KernelTraceEventParser.Keywords.ContextSwitch },
            // { "CSwitch_Internal", ??? },
            { "DiskIO", KernelTraceEventParser.Keywords.DiskIO },
            { "DiskIOInit", KernelTraceEventParser.Keywords.DiskIOInit },
            { "DPC", KernelTraceEventParser.Keywords.DeferedProcedureCalls },
            // { "Dpc_Internal", ??? },
            { "Drivers", KernelTraceEventParser.Keywords.Driver },
            // { "Drivers_Internal", ??? },
            { "FileIO", KernelTraceEventParser.Keywords.FileIO },
            { "FileIOInit", KernelTraceEventParser.Keywords.FileIOInit },
            { "Filename", KernelTraceEventParser.Keywords.DiskFileIO },
            // { "FootPrint", ??? },
            // { "KeClock", ??? },
            { "HardFaults", KernelTraceEventParser.Keywords.MemoryHardFaults },
            // { "IdleStates", ??? },
            // { "InterProcessorInterrupt", ??? },
            { "Interrupt", KernelTraceEventParser.Keywords.Interrupt },
            // { "Interrupt_Internal", ??? },
            // { "KernelQueue", ??? },
            { "Loader", KernelTraceEventParser.Keywords.ImageLoad },
            // { "Memory", ??? },
            // { "MemoryInfoWS", ??? },
            { "NetworkTrace", KernelTraceEventParser.Keywords.NetworkTCPIP },
            { "PmcProfile", KernelTraceEventParser.Keywords.PMCProfile },
            // { "Pool", ??? },
            { "ProcessCounter", KernelTraceEventParser.Keywords.ProcessCounters },
            { "ProcessThread", KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread },
            // { "ProcessFreeze", ??? },
            { "ReadyThread", KernelTraceEventParser.Keywords.Dispatcher },
            // { "ReadyThread_Internal", ??? },
            // { "ReferenceSet", ??? },
            { "Registry", KernelTraceEventParser.Keywords.Registry },
            // { "RegistryHive", ??? },
            // { "RegistryNotify", ??? },
            { "SampledProfile", KernelTraceEventParser.Keywords.Profile },
            // { "SampledProfile_Internal", ??? },
            // { "Session", ??? },
            // { "SpinLock", ??? },
            { "SplitIO", KernelTraceEventParser.Keywords.SplitIO },
            // { "SynchronizationObjects", ??? },
            { "SystemCall", KernelTraceEventParser.Keywords.SystemCall },
            // { "SystemCall_Internal", ??? },
            { "ThreadPriority", KernelTraceEventParser.Keywords.ThreadPriority },
            // { "Timer", ??? },
            { "VirtualAllocation", KernelTraceEventParser.Keywords.VirtualAlloc },
            // { "VirtualAllocation_Internal", ??? },
            { "VAMap", KernelTraceEventParser.Keywords.VAMap }
        };

        public string Id { get; private set; }

        public string Name { get; private set; }

        public DetailLevel DetailLevel { get; private set; }

        public LoggingMode LoggingMode { get; private set; }

        public string Description { get; private set; }

        // There can only be zero or one system collector with one system provider.
        public SystemCollector SystemCollector { get; private set; }
        public SystemProvider SystemProvider { get; private set; }

        public IReadOnlyDictionary<EventCollector, IReadOnlyList<EventProvider>> EventProviders { get; private set; }

        // Convert from WPRP-native format to simplified format used to create an ETW session.
        public EtwSessionProfile ConvertToSessionProfile()
        {
            EtwSessionProfile etwSessionProfile = new() { DisplayName = Name };

            foreach (var keyword in SystemProvider?.Keywords ?? Enumerable.Empty<string>())
            {
                if (KernelKeywordMap.TryGetValue(keyword, out KernelTraceEventParser.Keywords matchingFlags))
                {
                    etwSessionProfile.KernelKeywords |= matchingFlags;
                }
                else
                {
                    Trace.WriteLine("Unknown system/kernel keyword: " + keyword);
                }
            }

            foreach (var collectorEventProviders in EventProviders)
            {
                foreach (var eventProvider in collectorEventProviders.Value)
                {
                    // TODO: Needed when more advanced features are supported.
                    // TraceEventProviderOptions options = new();

                    TraceEventLevel level = TraceEventLevel.Verbose;
                    if (eventProvider.Level.HasValue)
                    {
                        level = (TraceEventLevel)eventProvider.Level.Value;
                    }

                    ulong matchAnyKeywords = ulong.MaxValue;
                    if (eventProvider.Keywords.HasValue)
                    {
                        matchAnyKeywords = eventProvider.Keywords.Value;
                    }

                    etwSessionProfile.Providers.Add(new EtwSessionEnabledProvider
                    {
                        Name = eventProvider.Name,
                        Description = eventProvider.Id,
                        Level = level,
                        MatchAnyKeyword = matchAnyKeywords,
                    });
                }
            }

            return etwSessionProfile;
        }

        public static WprpProfile Parse(
            XElement profileEl,
            IReadOnlyList<SystemCollector> globalSystemCollectors,
            IReadOnlyList<EventCollector> globalEventCollectors,
            Dictionary<string, SystemProvider> globalSystemProviders,
            Dictionary<string, EventProvider> globalEventProviders)
        {
            WprpProfile profile = new WprpProfile();
            profile.Id = (string)profileEl.RequiredAttribute("Id");
            profile.Name = (string)profileEl.RequiredAttribute("Name");
            profile.Description = (string)profileEl.RequiredAttribute("Description");
            profile.DetailLevel = Enum.Parse<DetailLevel>((string)profileEl.RequiredAttribute("DetailLevel"));
            profile.LoggingMode = Enum.Parse<LoggingMode>((string)profileEl.RequiredAttribute("LoggingMode"));

            string baseProfile = (string)profileEl.Attribute("Base");
            if (!string.IsNullOrEmpty(baseProfile))
            {
                Debug.WriteLine($"Skipping profile '{profile.Id}' with base profile '{baseProfile}'. This is not supported yet.");
                return null; // Skip profiles that inherit from another profile for now.
            }

            var collectorsNode = profileEl.RequiredElement("Collectors");
            // TODO: 'Operation' attribute for Add/Remove/Union

            Dictionary<SystemCollector, IReadOnlyList<SystemProvider>> systemProviders = new();
            var systemCollectorNode = collectorsNode.Element("SystemCollectorId");
            if (systemCollectorNode != null)
            {
                string systemCollectorId = (string)systemCollectorNode.RequiredAttribute("Value");

                try
                {
                    profile.SystemCollector = globalSystemCollectors.Single(c => c.Id == systemCollectorId);
                }
                catch (Exception ex)
                {
                    throw new Exception($"Failed to find SystemCollectorId with Id='{systemCollectorId}' or duplicate Ids exist.", ex);
                }

                var systemProviderId = systemCollectorNode.Element("SystemProviderId");
                if (systemProviderId != null)
                {
                    if (!globalSystemProviders.TryGetValue((string)systemProviderId.RequiredAttribute("Value"), out SystemProvider systemProvider))
                    {
                        throw new Exception($"Failed to find SystemProviderId with Value='{(string)systemProviderId.Attribute("Value")}'");
                    }
                    profile.SystemProvider = systemProvider;
                }
                else
                {
                    var systemProvider = systemCollectorNode.Element("SystemProvider");
                    if (systemProvider != null)
                    {
                        profile.SystemProvider = SystemProvider.Parse(systemProvider);
                    }
                }
            }

            Dictionary<EventCollector, IReadOnlyList<EventProvider>> eventProviders = new();
            foreach (var eventCollector in collectorsNode.Elements("EventCollectorId"))
            {
                string eventCollectorId = (string)eventCollector.RequiredAttribute("Value");
                EventCollector collector;
                try
                {
                    collector = globalEventCollectors.Single(ec => ec.Id == eventCollectorId);
                }
                catch (Exception ex)
                {
                    throw new Exception($"Failed to find EventCollectorId with Id='{eventCollectorId}' or duplicate Ids exist.", ex);
                }

                var eventProvidersNode = eventCollector.RequiredElement("EventProviders");

                List<EventProvider> profileEventProviders = new();

                // EventProviderId refer to the global EventProvider elements outside the current Profile.
                var eventProviderIdNodes = eventProvidersNode.Elements("EventProviderId");
                profileEventProviders.AddRange(eventProviderIdNodes.Select(epi =>
                    globalEventProviders[(string)epi.RequiredAttribute("Value")]));

                // EventProvider refer to EventProviders inlined in this current Profile.
                var eventProviderNodes = eventProvidersNode.Elements("EventProvider");
                profileEventProviders.AddRange(eventProviderNodes.Select(EventProvider.Parse));

                eventProviders.Add(collector, profileEventProviders);
            }
            profile.EventProviders = eventProviders;

            return profile;
        }
    }

    internal class Wprp
    {
        private List<SystemCollector> _systemCollectors = new();
        private List<EventCollector> _eventCollectors = new();
        private List<WprpProfile> _profiles = new();

        private Wprp(string wprpFilePath)
        {
            XDocument doc = XDocument.Load(wprpFilePath);

            var profilesNode = doc.Root.Element("Profiles");
            if (profilesNode == null)
            {
                return;
            }

            _systemCollectors = profilesNode.Elements("SystemCollector").Select(SystemCollector.Parse).ToList();
            _eventCollectors = profilesNode.Elements("EventCollector").Select(EventCollector.Parse).ToList();

            Dictionary<string, SystemProvider> globalSystemProviders =
                    profilesNode
                        .Elements("SystemProvider")
                        .Select(SystemProvider.Parse)
                        .ToDictionary(ep => ep.Id);

            Dictionary<string, EventProvider> globalEventProviders =
                    profilesNode
                        .Elements("EventProvider")
                        .Select(EventProvider.Parse)
                        .ToDictionary(ep => ep.Id);

            var profileEls = profilesNode.Elements("Profile");
            foreach (var profileEl in profileEls)
            {
                WprpProfile profile = WprpProfile.Parse(profileEl, _systemCollectors, _eventCollectors, globalSystemProviders, globalEventProviders);
                if (profile != null)
                {
                    _profiles.Add(profile);
                }
            }
        }

        public static Wprp Load(string wprpFilePath)
        {
            return new Wprp(wprpFilePath);
        }

        public static void SaveToWprp(EtwSessionProfile sessionProfile)
        {
            string file = FileDialog.SaveFile("Windows Performance Recorder Profile Files (*.wprp)|*.wprp", Settings.WprpOpenLocation, "wprp");
            if (file == null)
            {
                return;
            }

            Settings.WprpOpenLocation = Path.GetDirectoryName(file);
            Settings.AddRecentlyOpenedWprp(file);

            const string systemCollectorId = "SystemCollector";
            const string eventCollectorId = "EventCollector_UserMode";

            string profileName = string.IsNullOrWhiteSpace(sessionProfile.DisplayName) ? "Instant Trace Viewer" : sessionProfile.DisplayName.Trim();
            string profileIdBase = new string(profileName.Select(c => char.IsWhiteSpace(c) || c == ':' ? '_' : c).ToArray());
            if (string.IsNullOrWhiteSpace(profileIdBase))
            {
                profileIdBase = "InstantTraceViewer";
            }

            var collectors = new XElement("Collectors");

            List<string> keywordNames = new();
            KernelTraceEventParser.Keywords remainingKernelKeywords = sessionProfile.KernelKeywords;
            foreach (var keyword in WprpProfile.KernelKeywordMap)
            {
                if ((remainingKernelKeywords & keyword.Value) == keyword.Value)
                {
                    keywordNames.Add(keyword.Key);
                    remainingKernelKeywords &= ~keyword.Value;
                }
            }

            if (remainingKernelKeywords != KernelTraceEventParser.Keywords.None)
            {
                Trace.WriteLine("WPRP export skipped unsupported system/kernel keywords: " + remainingKernelKeywords);
            }

            if (keywordNames.Count > 0)
            {
                collectors.Add(new XElement("SystemCollectorId",
                    new XAttribute("Value", systemCollectorId),
                    new XElement("SystemProvider",
                        new XAttribute("Id", "SystemProviderVerbose"),
                        new XElement("Keywords", keywordNames.Select(keywordName =>
                            new XElement("Keyword", new XAttribute("Value", keywordName)))))));
            }

            if (sessionProfile.Providers.Count > 0)
            {
                var eventProviders = new XElement("EventProviders");
                foreach (var provider in sessionProfile.Providers)
                {
                    string providerIdSource = string.IsNullOrWhiteSpace(provider.Description) ? provider.Name : provider.Description;
                    string providerId = new string(providerIdSource.Select(c => char.IsWhiteSpace(c) || c == ':' ? '_' : c).ToArray());
                    if (string.IsNullOrWhiteSpace(providerId))
                    {
                        providerId = "EventProvider";
                    }

                    var eventProvider = new XElement("EventProvider",
                        new XAttribute("Id", providerId),
                        new XAttribute("Name", provider.Name));

                    if (provider.Level != TraceEventLevel.Verbose)
                    {
                        eventProvider.Add(new XAttribute("Level", (uint)provider.Level));
                    }

                    if (provider.MatchAnyKeyword != ulong.MaxValue)
                    {
                        eventProvider.Add(new XElement("Keywords",
                            new XElement("Keyword", new XAttribute("Value", "0x" + provider.MatchAnyKeyword.ToString("X")))));
                    }

                    eventProviders.Add(eventProvider);
                }

                collectors.Add(new XElement("EventCollectorId",
                    new XAttribute("Value", eventCollectorId),
                    eventProviders));
            }

            var profiles = new XElement("Profiles");

            if (keywordNames.Count > 0)
            {
                profiles.Add(new XElement("SystemCollector",
                    new XAttribute("Id", systemCollectorId),
                    new XAttribute("Name", "NT Kernel Logger"),
                    new XElement("BufferSize", new XAttribute("Value", 1020)),
                    new XElement("Buffers",
                        new XAttribute("Value", 3),
                        new XAttribute("PercentageOfTotalMemory", true),
                        new XAttribute("MaximumBufferSpace", 100))));
            }

            if (sessionProfile.Providers.Count > 0)
            {
                profiles.Add(new XElement("EventCollector",
                    new XAttribute("Id", eventCollectorId),
                    new XAttribute("Name", "User Mode Event Collector"),
                    new XElement("BufferSize", new XAttribute("Value", 1020)),
                    new XElement("Buffers",
                        new XAttribute("Value", 3),
                        new XAttribute("PercentageOfTotalMemory", true),
                        new XAttribute("MaximumBufferSpace", 100))));
            }

            profiles.Add(new XElement("Profile",
                new XAttribute("Id", profileIdBase + ".Verbose.File"),
                new XAttribute("Name", profileIdBase),
                new XAttribute("Description", profileName),
                new XAttribute("LoggingMode", LoggingMode.File),
                new XAttribute("DetailLevel", DetailLevel.Verbose),
                new XAttribute("Default", true),
                collectors));

            profiles.Add(new XElement("Profile",
                new XAttribute("Id", profileIdBase + ".Verbose.Memory"),
                new XAttribute("Name", profileIdBase),
                new XAttribute("Description", profileName),
                new XAttribute("Base", profileIdBase + ".Verbose.File"),
                new XAttribute("LoggingMode", LoggingMode.Memory),
                new XAttribute("DetailLevel", DetailLevel.Verbose)));

            XDocument document = new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                new XElement("WindowsPerformanceRecorder",
                    new XAttribute("Version", "1.0"),
                    new XAttribute("Author", "Instant Trace Viewer"),
                    profiles));

            using XmlWriter writer = XmlWriter.Create(file, new XmlWriterSettings
            {
                Encoding = new System.Text.UTF8Encoding(false),
                Indent = true,
            });
            document.Save(writer);
        }

        public IReadOnlyList<WprpProfile> Profiles => _profiles;

        public IReadOnlyList<EventCollector> EventCollectors => _eventCollectors;
    }
}
