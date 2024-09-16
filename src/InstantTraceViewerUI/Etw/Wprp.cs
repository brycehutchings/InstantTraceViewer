// See WPRP schema here: https://learn.microsoft.com/en-us/windows-hardware/test/wpt/wprcontrolprofiles-schema

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Xml.Linq;

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
    internal class EventCollector
    {
        public string Id { get; private set; }
        public string Name { get; private set; }
        public uint? BufferSize { get; private set; }
        public uint? Buffers { get; private set; }

        public static EventCollector Parse(XElement eventCollectorEl)
        {
            return new EventCollector
            {
                Id = (string)eventCollectorEl.Attribute("Id"),
                Name = (string)eventCollectorEl.Attribute("Name"),
                BufferSize = (uint?)eventCollectorEl.Element("BufferSize")?.Attribute("Value"),
                Buffers = (uint?)eventCollectorEl.Element("Buffers")?.Attribute("Value")
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

            // TODO: Should this be flattened?
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
        public string Id { get; private set; }

        public string Name { get; private set; }

        public DetailLevel DetailLevel { get; private set; }

        public LoggingMode LoggingMode { get; private set; }

        public string Description { get; private set; }

        public IReadOnlyDictionary<EventCollector, IReadOnlyList<EventProvider>> EventProviders { get; private set; }

        public static WprpProfile? Parse(XElement profileEl, IReadOnlyList<EventCollector> globalEventCollectors, Dictionary<string, EventProvider> globalEventProviders)
        {
            WprpProfile profile = new WprpProfile();
            profile.Id = (string)profileEl.Attribute("Id");
            profile.Name = (string)profileEl.Attribute("Name");
            profile.Description = (string)profileEl.Attribute("Description");
            profile.DetailLevel = Enum.Parse<DetailLevel>((string)profileEl.Attribute("DetailLevel"));
            profile.LoggingMode = Enum.Parse<LoggingMode>((string)profileEl.Attribute("LoggingMode"));

            string? baseProfile = (string?)profileEl.Attribute("Base");
            if (!string.IsNullOrEmpty(baseProfile))
            {
                Debug.WriteLine($"Skipping profile '{profile.Id}' with base profile '{baseProfile}'. This is not supported yet.");
                return null; // Skip profiles that inherit from another profile for now.
            }

            var collectorsNode = profileEl.Element("Collectors");
            // TODO: 'Operation' attribute for Add/Remove/Union

            var systemCollectorNode = collectorsNode.Element("SystemCollectorId");
            if (systemCollectorNode != null)
            {
                Debug.WriteLine("Skipping SystemCollectorId. This is not supported yet.");
                // TODO
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

            _eventCollectors = profilesNode.Elements("EventCollector").Select(EventCollector.Parse).ToList();

            Dictionary<string, EventProvider> globalEventProviders =
                    profilesNode
                        .Elements("EventProvider")
                        .Select(EventProvider.Parse)
                        .ToDictionary(ep => ep.Id);

            var profileEls = profilesNode.Elements("Profile");
            foreach (var profileEl in profileEls)
            {
                WprpProfile? profile = WprpProfile.Parse(profileEl, _eventCollectors, globalEventProviders);
                if (profile != null) {
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
