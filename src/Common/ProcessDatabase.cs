using System;
using System.Collections.Generic;

namespace InstantTraceViewer
{
    /// <summary>
    /// Tracks process and thread names.
    /// TODO: Tracks loaded modules to resolve symbols
    /// </summary>
    public class ProcessDatabase
    {
        record struct TrackedProcess
        {
            public TrackedProcess()
            {
            }

            public string? Name;
            public DateTime? Start;
            public DateTime? End;
        }

        record struct TrackedThread
        {
            public required string? Name;
            public DateTime? Start;
            public DateTime? End;
        }

        // Value is a List because PIDs may be reused.
        private Dictionary<int /* pid */, List<TrackedProcess>> _trackedProcesses = new();

        // Value is a List because TIDs may be reused.
        // This is kept outside of the process tracking because ETW thread start events may be observed before process datacollectionstart events.
        // PID is not used in the key because some events exclude PID and only include TID.
        private Dictionary<int, List<TrackedThread>> _trackedThreads = new();

        public string? GetProcessName(int pid, DateTime timestamp)
        {
            lock (_trackedProcesses)
            {
                if (_trackedProcesses.TryGetValue(pid, out var processes))
                {
                    TrackedProcess? nearestProcess = null;
                    TimeSpan? nearestDistance = null;
                    for (int i = processes.Count - 1; i >= 0; i--)
                    {
                        var process = processes[i];
                        if ((process.Start == null || process.Start <= timestamp) && (process.End == null || process.End >= timestamp))
                        {
                            return process.Name;
                        }

                        TimeSpan distance = GetDistance(timestamp, process.Start, process.End);
                        if (nearestDistance == null || distance < nearestDistance)
                        {
                            nearestProcess = process;
                            nearestDistance = distance;
                        }
                    }

                    return nearestProcess?.Name;
                }
            }

            return null;
        }

        public void ProcessStart(int pid, string? name, DateTime startTime)
        {
            lock (_trackedProcesses)
            {
                if (!_trackedProcesses.TryGetValue(pid, out var processes))
                {
                    processes = new List<TrackedProcess>();
                    _trackedProcesses[pid] = processes;
                }

                if (processes.Count > 0)
                {
                    var lastProcess = processes[^1];
                    if (lastProcess.End == null && (lastProcess.Start == null || lastProcess.Start < startTime))
                    {
                        processes[^1] = lastProcess with { End = startTime };
                    }
                }

                processes.Add(new TrackedProcess { Name = name, Start = startTime });
            }
        }

        public void ProcessStop(int pid, DateTime? endTime)
        {
            lock (_trackedProcesses)
            {
                if (_trackedProcesses.TryGetValue(pid, out var processes))
                {
                    processes[^1] = processes[^1] with { End = endTime };
                }
            }
        }

        public string? GetThreadName(int tid, DateTime timestamp)
        {
            lock (_trackedThreads)
            {
                if (_trackedThreads.TryGetValue(tid, out var threads))
                {
                    TrackedThread? nearestThread = null;
                    TimeSpan? nearestDistance = null;
                    for (int i = threads.Count - 1; i >= 0; i--)
                    {
                        var thread = threads[i];
                        if ((thread.Start == null || thread.Start <= timestamp) && (thread.End == null || thread.End >= timestamp))
                        {
                            return thread.Name;
                        }

                        TimeSpan distance = GetDistance(timestamp, thread.Start, thread.End);
                        if (nearestDistance == null || distance < nearestDistance)
                        {
                            nearestThread = thread;
                            nearestDistance = distance;
                        }
                    }

                    return nearestThread?.Name;
                }
            }

            return null;
        }

        public void ThreadStart(int tid, string? name, DateTime startTime)
        {
            lock (_trackedThreads)
            {
                if (!_trackedThreads.TryGetValue(tid, out var threads))
                {
                    threads = new List<TrackedThread>();
                    _trackedThreads[tid] = threads;
                }

                threads.Add(new TrackedThread { Name = name, Start = startTime });
            }
        }

        public void ThreadSetName(int tid, string name)
        {
            lock (_trackedThreads)
            {
                if (!_trackedThreads.TryGetValue(tid, out var threads))
                {
                    threads = new List<TrackedThread>();
                    _trackedThreads[tid] = threads;
                }

                if (threads.Count == 0 || threads[^1].End != null)
                {
                    threads.Add(new TrackedThread { Name = name });
                }
                else
                {
                    threads[^1] = threads[^1] with { Name = name };
                }
            }
        }

        public void ThreadStop(int tid, DateTime? endTime)
        {
            lock (_trackedThreads)
            {
                if (_trackedThreads.TryGetValue(tid, out var threads))
                {
                    threads[^1] = threads[^1] with { End = endTime };
                }
            }
        }

        private static TimeSpan GetDistance(DateTime timestamp, DateTime? start, DateTime? end)
        {
            if (end != null && timestamp > end)
            {
                return timestamp - end.Value;
            }

            if (start != null && timestamp < start)
            {
                return start.Value - timestamp;
            }

            return TimeSpan.Zero;
        }

    }
}
