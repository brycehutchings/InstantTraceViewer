// Uncomment to diagnose parsing/lookup problems.
// #define DEBUG_PARSING

namespace Tabnalysis
{
    using Perfetto.Protos;
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;

    internal class TrackDescriptorManager
    {
        private Dictionary<ulong, ThreadDescriptor> threadDescriptorsByUuid = new Dictionary<ulong, ThreadDescriptor>(); // Uuid is key
        private Dictionary<ulong, ProcessDescriptor> processDescriptorsByUuid = new Dictionary<ulong, ProcessDescriptor>(); // Uuid is key

        private Dictionary<uint, ThreadDescriptor> threadDescriptorsByTrustedPacketSequenceId = new Dictionary<uint, ThreadDescriptor>(); // TrustedPacketSequenceId is key
        private Dictionary<uint, ProcessDescriptor> processDescriptorsByTrustedPacketSequenceId = new Dictionary<uint, ProcessDescriptor>(); // TrustedPacketSequenceId is key

        private Dictionary<int, ProcessDescriptor> processTrackDescriptors = new Dictionary<int, ProcessDescriptor>(); // Pid is key
        private Dictionary<int, ThreadDescriptor> threadTrackDescriptors = new Dictionary<int, ThreadDescriptor>(); // Tid is key

        public void ProcessPacket(TracePacket packet)
        {
            if (packet.TrackDescriptor == null)
            {
                return;
            }

#if DEBUG_PARSING
            Debug.Print($"  TrackDescriptor Uuid={packet.TrackDescriptor.Uuid} ProcessPid={packet.TrackDescriptor?.Process?.Pid ?? -1} ThreadPid={packet.TrackDescriptor?.Thread?.Pid ?? -1} Tid={packet.TrackDescriptor?.Thread?.Tid ?? -1}");
#endif

            // For unknown reasons there may be repeats (same Uuid or same TrustedPacketSequenceId).

            if (packet.TrackDescriptor.Process != null)
            {
                this.processDescriptorsByUuid[packet.TrackDescriptor.Uuid] = packet.TrackDescriptor.Process;
                this.processDescriptorsByTrustedPacketSequenceId[packet.TrustedPacketSequenceId] = packet.TrackDescriptor.Process;
                this.processTrackDescriptors[packet.TrackDescriptor.Process.Pid] = packet.TrackDescriptor.Process;
            }

            if (packet.TrackDescriptor.Thread != null)
            {
                this.threadDescriptorsByUuid[packet.TrackDescriptor.Uuid] = packet.TrackDescriptor.Thread;
                this.threadDescriptorsByTrustedPacketSequenceId[packet.TrustedPacketSequenceId] = packet.TrackDescriptor.Thread;
                this.threadTrackDescriptors[packet.TrackDescriptor.Thread.Tid] = packet.TrackDescriptor.Thread;
            }
        }

        public ThreadDescriptor GetThreadDescriptor(TracePacket packet)
        {
            ThreadDescriptor threadDescriptor = null;
            if (packet.TrackEvent?.HasTrackUuid ?? false)
            {
#if DEBUG_PARSING
                Debug.Print($"  TrackEvent.TrackUuid={packet.TrackEvent.HasTrackUuid}");
#endif
                this.threadDescriptorsByUuid.TryGetValue(packet.TrackEvent.TrackUuid, out threadDescriptor);
            }

            if (threadDescriptor == null)
            {
                this.threadDescriptorsByTrustedPacketSequenceId.TryGetValue(packet.TrustedPacketSequenceId, out threadDescriptor);
            }

            return threadDescriptor;
        }

        public ProcessDescriptor GetProcessDescriptor(TracePacket packet, ThreadDescriptor threadDescriptor)
        {
            ProcessDescriptor processDescriptor = null;
            if (packet.TrackEvent?.HasTrackUuid ?? false)
            {
#if DEBUG_PARSING
                Debug.Print($"  TrackEvent.TrackUuid={packet.TrackEvent.HasTrackUuid}");
#endif
                this.processDescriptorsByUuid.TryGetValue(packet.TrackEvent.TrackUuid, out processDescriptor);
            }

            if (processDescriptor == null)
            {
                this.processDescriptorsByTrustedPacketSequenceId.TryGetValue(packet.TrustedPacketSequenceId, out processDescriptor);
            }

            if (processDescriptor == null && threadDescriptor != null)
            {
                this.processTrackDescriptors.TryGetValue(threadDescriptor.Pid, out processDescriptor);
            }

            return processDescriptor;
        }

        public ThreadDescriptor GetThreadDescriptorByTid(int tid)
        {
            ThreadDescriptor threadDescriptor = null;
            this.threadTrackDescriptors.TryGetValue(tid, out threadDescriptor);
            return threadDescriptor;
        }

        public ProcessDescriptor GetProcessDescriptorByPid(int pid)
        {
            ProcessDescriptor processDescriptor = null;
            this.processTrackDescriptors.TryGetValue(pid, out processDescriptor);
            return processDescriptor;
        }
    }
}