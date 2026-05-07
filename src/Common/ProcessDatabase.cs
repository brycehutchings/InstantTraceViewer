using System;
using System.Collections.Generic;

namespace InstantTraceViewer
{
    /// <summary>
    /// Tracks process and thread names.
    /// TODO: Tracks loaded modules to resolve symbols
    /// This class expects timestamp data happens chronologically but also allows for retroactive changes
    /// like a process name for an already-known pid to be assigned.
    /// </summary>
    public class ProcessDatabase
    {
        record struct TrackedProcess
        {
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

        public void ProcessEnsure(int pid, DateTime timestamp)
        {
            ProcessStart(pid, null, timestamp);
        }

        // Returns true if an existing process had its name changed.
        public bool ProcessStart(int pid, string? name, DateTime? startTime)
        {
            lock (_trackedProcesses)
            {
                if (!_trackedProcesses.TryGetValue(pid, out var processes))
                {
                    processes = new List<TrackedProcess>();
                    _trackedProcesses[pid] = processes;
                }

                // See if this event falls into an existing TrackedProcess already.
                if (processes.Count > 0 && processes[^1].End == null)
                {
                    TrackedProcess tp = processes[^1];
                    if (name != null && name != tp.Name)
                    {
                        tp.Name = name;
                        processes[^1] = tp;
                        return true;
                    }

                    return false;
                }

                processes.Add(new TrackedProcess { Name = name, Start = startTime });
                return false;
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

        public void ThreadEnsure(int tid, DateTime timestamp)
        {
            ThreadStart(tid, null, timestamp);
        }

        // Returns true if an existing thread had its name changed.
        public bool ThreadStart(int tid, string? name, DateTime? startTime)
        {
            lock (_trackedThreads)
            {
                if (!_trackedThreads.TryGetValue(tid, out var threads))
                {
                    threads = new List<TrackedThread>();
                    _trackedThreads[tid] = threads;
                }

                // See if this event falls into an existing TrackedThread already.
                if (threads.Count > 0 && threads[^1].End == null)
                {
                    TrackedThread tt = threads[^1];
                    if (name != null && name != tt.Name)
                    {
                        tt.Name = name;
                        threads[^1] = tt;
                        return true;
                    }

                    return false;
                }

                threads.Add(new TrackedThread { Name = name, Start = startTime });
                return false;
            }
        }

        // Returns true if an existing thread had its name changed.
        public bool ThreadSetName(int tid, string name)
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
                    return false;
                }
                else
                {
                    threads[^1] = threads[^1] with { Name = name };
                    return true;
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
