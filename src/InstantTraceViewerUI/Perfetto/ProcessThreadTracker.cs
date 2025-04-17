using System.Collections.Generic;
using System.Linq;
using Perfetto.Protos;
using Windows.Networking.Sockets;

namespace InstantTraceViewerUI.Perfetto
{
    internal class ProcessThreadTracker
    {
        public record class ThreadData(int Tid, int Pid, string Name);

        // TODO: Add Uid? ParentId? Currently not needed.
        public record class ProcessData(int Pid, string Name);

        private Dictionary<ulong, ThreadData> _threadNameByUuid = new(); // Uuid is key
        private Dictionary<ulong, ProcessData> _processNameByUuid = new(); // Uuid is key

        private Dictionary<uint, ThreadData> _threadNameByTrustedPacketSequenceId = new(); // TrustedPacketSequenceId is key
        private Dictionary<uint, ProcessData> _processNameByTrustedPacketSequenceId = new(); // TrustedPacketSequenceId is key

        private Dictionary<int, ThreadData> _threadNameByTid = new(); // Tid is key
        private Dictionary<int, ProcessData> _processNameByPid = new(); // Pid is key

        public void ProcessPacket(TracePacket packet)
        {
            // For unknown reasons there may be repeats (same Uuid or same TrustedPacketSequenceId).

            if (packet.TrackDescriptor?.Process != null)
            {
                string processName = packet.TrackDescriptor.Process.ProcessName;
                if (!string.IsNullOrEmpty(processName))
                {
                    _processNameByUuid[packet.TrackDescriptor.Uuid] = new ProcessData(packet.TrackDescriptor.Process.Pid, processName);
                    _processNameByTrustedPacketSequenceId[packet.TrustedPacketSequenceId] = new ProcessData(packet.TrackDescriptor.Process.Pid, processName);
                    _processNameByPid[packet.TrackDescriptor.Process.Pid] = new ProcessData(packet.TrackDescriptor.Process.Pid, processName);
                }
            }

            if (packet.TrackDescriptor?.Thread != null)
            {
                string threadName = packet.TrackDescriptor.Thread.ThreadName;
                if (!string.IsNullOrEmpty(threadName))
                {
                    _threadNameByUuid[packet.TrackDescriptor.Uuid] = new ThreadData(packet.TrackDescriptor.Thread.Tid, packet.TrackDescriptor.Thread.Pid, threadName);
                    _threadNameByTrustedPacketSequenceId[packet.TrustedPacketSequenceId] = new ThreadData(packet.TrackDescriptor.Thread.Tid, packet.TrackDescriptor.Thread.Pid, threadName);
                    _threadNameByTid[packet.TrackDescriptor.Thread.Tid] = new ThreadData(packet.TrackDescriptor.Thread.Tid, packet.TrackDescriptor.Thread.Pid, threadName);
                }
            }

            if (packet.ProcessTree != null)
            {
                foreach (var process in packet.ProcessTree.Processes)
                {
                    if (process.HasPid && process.Cmdline.Count > 0)
                    {
                        _processNameByPid[process.Pid] = new ProcessData(process.Pid, process.Cmdline.First());
                    }
                }

                foreach (var thread in packet.ProcessTree.Threads)
                {
                    if (thread.HasTid && thread.HasName)
                    {
                        _threadNameByTid[thread.Tid] = new ThreadData(thread.Tid, thread.Tgid /* pid */, thread.Name);
                    }
                }
            }
        }

        public ThreadData GetThreadData(TracePacket packet)
        {
            ThreadData threadData = null;
            if (packet.TrackEvent?.HasTrackUuid ?? false)
            {
                _threadNameByUuid.TryGetValue(packet.TrackEvent.TrackUuid, out threadData);
            }

            if (threadData == null)
            {
                _threadNameByTrustedPacketSequenceId.TryGetValue(packet.TrustedPacketSequenceId, out threadData);
            }

            return threadData;
        }

        public ProcessData GetProcessData(TracePacket packet, ThreadData? threadData)
        {
            ProcessData processData = null;
            if (packet.TrackEvent?.HasTrackUuid ?? false)
            {
                _processNameByUuid.TryGetValue(packet.TrackEvent.TrackUuid, out processData);
            }

            if (processData == null)
            {
                _processNameByTrustedPacketSequenceId.TryGetValue(packet.TrustedPacketSequenceId, out processData);
            }

            if (processData == null && threadData != null)
            {
                _processNameByPid.TryGetValue(threadData.Pid, out processData);
            }

            return processData;
        }

        public ThreadData GetThreadDataByTid(int tid)
        {
            ThreadData threadData = null;
            _threadNameByTid.TryGetValue(tid, out threadData);
            return threadData;
        }

        public ProcessData GetProcessDataByPid(int pid)
        {
            ProcessData processData = null;
            _processNameByPid.TryGetValue(pid, out processData);
            return processData;
        }
    }
}