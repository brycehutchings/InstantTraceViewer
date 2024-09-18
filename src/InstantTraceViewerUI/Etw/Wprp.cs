// See WPRP schema here: https://learn.microsoft.com/en-us/windows-hardware/test/wpt/wprcontrolprofiles-schema

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
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

    // https://learn.microsoft.com/en-us/windows-hardware/test/wpt/1-collector-definitions
    internal class CollectorBase
    {
        public string Id { get; private set; }
        public string Name { get; private set; }
        public uint? BufferSize { get; private set; }
        public uint? Buffers { get; private set; }

        protected void ParseXml(XElement collectorEl)
        {
            Id = (string)collectorEl.Attribute("Id");
            Name = (string)collectorEl.Attribute("Name");
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
                Id = (string)systemProviderEl.Attribute("Id"),
                Keywords = systemProviderEl.Elements("Keywords")
                    .SelectMany(k => k.Elements("Keyword")
                        .Select(k => (string)k.Attribute("Value"))).ToList()
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
                        string value = (string)k.Attribute("Value");
                        return value.StartsWith("0x") ? Convert.ToUInt64(value, 16) : Convert.ToUInt64(value);
                    })).ToList();

            ulong? keywords = keywordCollection.Any() ? keywordCollection.Aggregate(0ul, (acc, k) => acc | k) : null;

            // TODO: CaptureStateOnSave, CaptureStateOnStart, EventFilters, EventKey, NonPagedMemory
            return new EventProvider
            {
                Id = (string)eventProviderEl.Attribute("Id"),
                Name = (string)eventProviderEl.Attribute("Name"),
                Level = (uint?)eventProviderEl.Attribute("Level"),
                Keywords = keywords
            };
        }
    }

    internal class WprpProfile
    {
        // Some of these combinations seem nonsensical but I believe certain combinations are used for new meaning to avoid exceeding 64bit limit.
        // Example: SpinLock = Keywords.NetworkTCPIP | Keywords.ThreadPriority
        private readonly static Dictionary<string, KernelTraceEventParser.Keywords> KernelKeywordMap = new Dictionary<string, KernelTraceEventParser.Keywords> {
            { "AllFaults", KernelTraceEventParser.Keywords.Memory },
            { "Alpc", KernelTraceEventParser.Keywords.AdvancedLocalProcedureCalls },
            { "AntiStarvation", KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.ReferenceSet },
            { "CompactCSwitch", KernelTraceEventParser.Keywords.DiskIO | KernelTraceEventParser.Keywords.ThreadPriority },
            { "ContiguousMemorygeneration", KernelTraceEventParser.Keywords.AdvancedLocalProcedureCalls | KernelTraceEventParser.Keywords.ThreadPriority },
            { "CpuConfig", KernelTraceEventParser.Keywords.NetworkTCPIP | KernelTraceEventParser.Keywords.ReferenceSet },
            { "CSwitch", KernelTraceEventParser.Keywords.ContextSwitch },
            { "CSwitch_Internal", KernelTraceEventParser.Keywords.ImageLoad | KernelTraceEventParser.Keywords.ThreadPriority },
            { "DiskIO", KernelTraceEventParser.Keywords.DiskIO | KernelTraceEventParser.Keywords.DiskFileIO },
            { "DiskIOInit", KernelTraceEventParser.Keywords.DiskIOInit },
            { "Dpc", KernelTraceEventParser.Keywords.DeferedProcedureCalls },
            { "Dpc_Internal", KernelTraceEventParser.Keywords.SystemCall | KernelTraceEventParser.Keywords.ThreadPriority },
            { "Drivers", KernelTraceEventParser.Keywords.Driver },
            { "Drivers_Internal", KernelTraceEventParser.Keywords.ContextSwitch | KernelTraceEventParser.Keywords.ThreadPriority },
            { "FileIO", KernelTraceEventParser.Keywords.FileIO },
            { "FileIOInit", KernelTraceEventParser.Keywords.FileIOInit },
            { "Filename", KernelTraceEventParser.Keywords.DiskFileIO },
            { "FootPrint", KernelTraceEventParser.Keywords.ProcessCounters | KernelTraceEventParser.Keywords.ThreadPriority },
            { "KeClock", KernelTraceEventParser.Keywords.AdvancedLocalProcedureCalls | KernelTraceEventParser.Keywords.ReferenceSet },
            { "HardFaults", KernelTraceEventParser.Keywords.MemoryHardFaults },
            { "IdleStates", KernelTraceEventParser.Keywords.VAMap | KernelTraceEventParser.Keywords.ReferenceSet },
            { "InterProcessorInterrupt", KernelTraceEventParser.Keywords.Handle | KernelTraceEventParser.Keywords.ReferenceSet },
            { "Interrupt", KernelTraceEventParser.Keywords.Interrupt },
            { "Interrupt_Internal", KernelTraceEventParser.Keywords.VirtualAlloc | KernelTraceEventParser.Keywords.ThreadPriority },
            { "KernelQueue", KernelTraceEventParser.Keywords.Profile | KernelTraceEventParser.Keywords.ThreadPriority },
            { "Loader", KernelTraceEventParser.Keywords.ImageLoad },
            { "Memory", KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.ThreadPriority },
            { "MemoryInfoWS", KernelTraceEventParser.Keywords.Driver | KernelTraceEventParser.Keywords.ThreadPriority },
            { "NetworkTrace", KernelTraceEventParser.Keywords.NetworkTCPIP },
            { "PmcProfile", KernelTraceEventParser.Keywords.DiskIOInit | KernelTraceEventParser.Keywords.ThreadPriority },
            { "Pool", KernelTraceEventParser.Keywords.Interrupt | KernelTraceEventParser.Keywords.ThreadPriority },
            { "ProcessCounter", KernelTraceEventParser.Keywords.ProcessCounters },
            { "ProcessThread", KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread },
            { "ProcessFreeze", KernelTraceEventParser.Keywords.Thread | KernelTraceEventParser.Keywords.ReferenceSet },
            { "ReadyThread", KernelTraceEventParser.Keywords.Dispatcher },
            { "ReadyThread_Internal", KernelTraceEventParser.Keywords.DiskFileIO | KernelTraceEventParser.Keywords.ThreadPriority },
            { "ReferenceSet", KernelTraceEventParser.Keywords.DeferedProcedureCalls | KernelTraceEventParser.Keywords.ThreadPriority },
            { "Registry", KernelTraceEventParser.Keywords.Registry },
            { "RegistryHive", KernelTraceEventParser.Keywords.Profile | KernelTraceEventParser.Keywords.ReferenceSet },
            { "RegistryNotify", KernelTraceEventParser.Keywords.FileIO | KernelTraceEventParser.Keywords.ReferenceSet },
            { "SampledProfile", KernelTraceEventParser.Keywords.Profile },
            { "SampledProfile_Internal", KernelTraceEventParser.Keywords.Thread | KernelTraceEventParser.Keywords.ThreadPriority },
            { "Session", KernelTraceEventParser.Keywords.Handle | KernelTraceEventParser.Keywords.ThreadPriority },
            { "SpinLock", KernelTraceEventParser.Keywords.NetworkTCPIP | KernelTraceEventParser.Keywords.ThreadPriority },
            { "SplitIO", KernelTraceEventParser.Keywords.SplitIO },
            { "SynchronizationObjects", KernelTraceEventParser.Keywords.Registry | KernelTraceEventParser.Keywords.ThreadPriority },
            { "SystemCall", KernelTraceEventParser.Keywords.SystemCall },
            { "SystemCall_Internal", KernelTraceEventParser.Keywords.Interrupt | KernelTraceEventParser.Keywords.ReferenceSet },
            { "ThreadPriority", KernelTraceEventParser.Keywords.MemoryHardFaults | KernelTraceEventParser.Keywords.ThreadPriority },
            { "Timer", KernelTraceEventParser.Keywords.Registry | KernelTraceEventParser.Keywords.ReferenceSet },
            { "VirtualAllocation", KernelTraceEventParser.Keywords.VirtualAlloc },
            { "VirtualAllocation_Internal", KernelTraceEventParser.Keywords.VAMap | KernelTraceEventParser.Keywords.ThreadPriority },
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
            EtwSessionProfile etwSessionProfile = new();

            foreach (var keyword in SystemProvider.Keywords)
            {
                if (KernelKeywordMap.TryGetValue(keyword, out KernelTraceEventParser.Keywords matchingFlags))
                {
                    etwSessionProfile.KernelKeywords |= matchingFlags;
                }
                else
                {
                    Debug.WriteLine("Unknown system/kernel keyword: " + keyword);
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
            profile.Id = (string)profileEl.Attribute("Id");
            profile.Name = (string)profileEl.Attribute("Name");
            profile.Description = (string)profileEl.Attribute("Description");
            profile.DetailLevel = Enum.Parse<DetailLevel>((string)profileEl.Attribute("DetailLevel"));
            profile.LoggingMode = Enum.Parse<LoggingMode>((string)profileEl.Attribute("LoggingMode"));

            string baseProfile = (string)profileEl.Attribute("Base");
            if (!string.IsNullOrEmpty(baseProfile))
            {
                Debug.WriteLine($"Skipping profile '{profile.Id}' with base profile '{baseProfile}'. This is not supported yet.");
                return null; // Skip profiles that inherit from another profile for now.
            }

            var collectorsNode = profileEl.Element("Collectors");
            // TODO: 'Operation' attribute for Add/Remove/Union

            Dictionary<SystemCollector, IReadOnlyList<SystemProvider>> systemProviders = new();
            var systemCollectorNode = collectorsNode.Element("SystemCollectorId");
            if (systemCollectorNode != null)
            {
                string systemCollectorId = (string)systemCollectorNode.Attribute("Value");
                profile.SystemCollector = globalSystemCollectors.Single(c => c.Id == systemCollectorId);
                profile.SystemProvider = globalSystemProviders[(string)systemCollectorNode.Element("SystemProviderId").Attribute("Value")];
            }

            Dictionary<EventCollector, IReadOnlyList<EventProvider>> eventProviders = new();
            foreach (var eventCollector in collectorsNode.Elements("EventCollectorId"))
            {
                string eventCollectorId = (string)eventCollector.Attribute("Value");
                EventCollector collector = globalEventCollectors.Single(ec => ec.Id == eventCollectorId);
                var eventProvidersNode = eventCollector.Element("EventProviders");

                List<EventProvider> profileEventProviders = new();

                // EventProviderId refer to the global EventProvider elements outside the current Profile.
                var eventProviderIdNodes = eventProvidersNode.Elements("EventProviderId");
                profileEventProviders.AddRange(eventProviderIdNodes.Select(epi =>
                    globalEventProviders[(string)epi.Attribute("Value")]));

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

        public IReadOnlyList<WprpProfile> Profiles => _profiles;

        public IReadOnlyList<EventCollector> EventCollectors => _eventCollectors;
    }
}
