// Uncomment to diagnose parsing/lookup problems.
// #define DEBUG_PARSING

namespace Tabnalysis
{
    using Perfetto.Protos;
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;

    internal class InternedStringManager
    {
        private struct InternKey : IEquatable<InternKey>
        {
            public InternKey(uint trustedPacketSequenceId, ulong iid)
            {
                this.TrustedPacketSequenceId = trustedPacketSequenceId;
                this.Iid = iid;
            }

            public uint TrustedPacketSequenceId { get; }
            public ulong Iid { get; }

            public override bool Equals(object obj) => this.Equals((InternKey)obj);
            public bool Equals(InternKey other) => this.TrustedPacketSequenceId == other.TrustedPacketSequenceId && this.Iid == other.Iid;
            public override int GetHashCode() => this.TrustedPacketSequenceId.GetHashCode() ^ this.Iid.GetHashCode();
        }

        class SequenceData
        {
            // Keys are Iid.
            public Dictionary<ulong, string> InternedEventNames = new Dictionary<ulong, string>();
            public Dictionary<ulong, string> InternedEventCategories = new Dictionary<ulong, string>();
            public Dictionary<ulong, string> InternedDebugAnnotationNames = new Dictionary<ulong, string>();

            public void Clear()
            {
                this.InternedEventNames.Clear();
                this.InternedEventCategories.Clear();
                this.InternedDebugAnnotationNames.Clear();
            }
        }

        // Key is TrustedPacketSequenceId
        private Dictionary<uint, SequenceData> allSequenceData = new Dictionary<uint, SequenceData>();

        public void ProcessPacket(TracePacket packet)
        {
            // Track all interned strings.
            if (packet.InternedData == null)
            {
                return;
            }

            SequenceData sequenceData = null;
            if (!allSequenceData.TryGetValue(packet.TrustedPacketSequenceId, out sequenceData))
            {
                sequenceData = new SequenceData();
                allSequenceData.Add(packet.TrustedPacketSequenceId, sequenceData);
            }

            if (packet.IncrementalStateCleared)
            {
                sequenceData.Clear();
            }

            foreach (var eventName in packet.InternedData.EventNames)
            {
                if (eventName.HasIid && eventName.HasName)
                {
#if DEBUG_PARSING
                    Debug.Print($"  InternedData EventName Iid={eventName.Iid} Value={eventName.Name}");
#endif
                    sequenceData.InternedEventNames[eventName.Iid] = eventName.Name;
                }
            }
            foreach (var eventCategory in packet.InternedData.EventCategories)
            {
                if (eventCategory.HasIid && eventCategory.HasName)
                {
                    sequenceData.InternedEventCategories[eventCategory.Iid] = eventCategory.Name;
                }
            }
            foreach (var annotationName in packet.InternedData.DebugAnnotationNames)
            {
                if (annotationName.HasIid && annotationName.HasName)
                {
                    sequenceData.InternedDebugAnnotationNames[annotationName.Iid] = annotationName.Name;
                }
            }
        }

        public string GetInternedEventName(TracePacket packet, ulong eventNameIid)
        {
#if DEBUG_PARSING
            Debug.Print($"  NameIid={packet.TrackEvent.NameIid}");
#endif

            string name;
            SequenceData sequenceData = null;
            if (!allSequenceData.TryGetValue(packet.TrustedPacketSequenceId, out sequenceData) ||
                !sequenceData.InternedEventNames.TryGetValue(eventNameIid, out name))
            {
                name = $"Unknown NameIid={eventNameIid}";
            }

            return name;
        }

        public string GetInternedCategoryName(TracePacket packet, ulong categoryNameIid)
        {
            string name;
            SequenceData sequenceData = null;
            if (!allSequenceData.TryGetValue(packet.TrustedPacketSequenceId, out sequenceData) ||
                !sequenceData.InternedEventCategories.TryGetValue(categoryNameIid, out name))
            {
                name = $"Unknown CategoryNameIid={categoryNameIid}";
            }

            return name;
        }

        public string GetInternedDebugAnnotationName(TracePacket packet, ulong debugAnnotationNameIid)
        {
            string name;
            SequenceData sequenceData = null;
            if (!allSequenceData.TryGetValue(packet.TrustedPacketSequenceId, out sequenceData) ||
                !sequenceData.InternedDebugAnnotationNames.TryGetValue(debugAnnotationNameIid, out name))
            {
                name = $"Unknown DebugAnnotationNameIid={debugAnnotationNameIid}";
            }

            return name;
        }
    }
}