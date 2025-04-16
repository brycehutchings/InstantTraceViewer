using System.Collections.Generic;
using Perfetto.Protos;

namespace InstantTraceViewerUI.Perfetto
{
    internal class TrackDescriptorManager
    {
        private Dictionary<ulong, ThreadDescriptor> _threadDescriptorsByUuid = new(); // Uuid is key
        private Dictionary<ulong, ProcessDescriptor> _processDescriptorsByUuid = new(); // Uuid is key

        private Dictionary<uint, ThreadDescriptor> _threadDescriptorsByTrustedPacketSequenceId = new(); // TrustedPacketSequenceId is key
        private Dictionary<uint, ProcessDescriptor> _processDescriptorsByTrustedPacketSequenceId = new(); // TrustedPacketSequenceId is key

        private Dictionary<int, ProcessDescriptor> _processTrackDescriptors = new(); // Pid is key
        private Dictionary<int, ThreadDescriptor> _threadTrackDescriptors = new(); // Tid is key

        public void ProcessPacket(TracePacket packet)
        {
            if (packet.TrackDescriptor == null)
            {
                return;
            }

            // For unknown reasons there may be repeats (same Uuid or same TrustedPacketSequenceId).

            if (packet.TrackDescriptor.Process != null)
            {
                _processDescriptorsByUuid[packet.TrackDescriptor.Uuid] = packet.TrackDescriptor.Process;
                _processDescriptorsByTrustedPacketSequenceId[packet.TrustedPacketSequenceId] = packet.TrackDescriptor.Process;
                _processTrackDescriptors[packet.TrackDescriptor.Process.Pid] = packet.TrackDescriptor.Process;
            }

            if (packet.TrackDescriptor.Thread != null)
            {
                _threadDescriptorsByUuid[packet.TrackDescriptor.Uuid] = packet.TrackDescriptor.Thread;
                _threadDescriptorsByTrustedPacketSequenceId[packet.TrustedPacketSequenceId] = packet.TrackDescriptor.Thread;
                _threadTrackDescriptors[packet.TrackDescriptor.Thread.Tid] = packet.TrackDescriptor.Thread;
            }
        }

        public ThreadDescriptor GetThreadDescriptor(TracePacket packet)
        {
            ThreadDescriptor threadDescriptor = null;
            if (packet.TrackEvent?.HasTrackUuid ?? false)
            {
                _threadDescriptorsByUuid.TryGetValue(packet.TrackEvent.TrackUuid, out threadDescriptor);
            }

            if (threadDescriptor == null)
            {
                _threadDescriptorsByTrustedPacketSequenceId.TryGetValue(packet.TrustedPacketSequenceId, out threadDescriptor);
            }

            return threadDescriptor;
        }

        public ProcessDescriptor GetProcessDescriptor(TracePacket packet, ThreadDescriptor threadDescriptor)
        {
            ProcessDescriptor processDescriptor = null;
            if (packet.TrackEvent?.HasTrackUuid ?? false)
            {
                _processDescriptorsByUuid.TryGetValue(packet.TrackEvent.TrackUuid, out processDescriptor);
            }

            if (processDescriptor == null)
            {
                _processDescriptorsByTrustedPacketSequenceId.TryGetValue(packet.TrustedPacketSequenceId, out processDescriptor);
            }

            if (processDescriptor == null && threadDescriptor != null)
            {
                _processTrackDescriptors.TryGetValue(threadDescriptor.Pid, out processDescriptor);
            }

            return processDescriptor;
        }

        public ThreadDescriptor GetThreadDescriptorByTid(int tid)
        {
            ThreadDescriptor threadDescriptor = null;
            _threadTrackDescriptors.TryGetValue(tid, out threadDescriptor);
            return threadDescriptor;
        }

        public ProcessDescriptor GetProcessDescriptorByPid(int pid)
        {
            ProcessDescriptor processDescriptor = null;
            _processTrackDescriptors.TryGetValue(pid, out processDescriptor);
            return processDescriptor;
        }
    }
}