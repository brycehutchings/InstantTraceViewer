using System;
using System.Collections.Generic;
using System.Xml.Linq;

namespace InstantTraceViewer
{
    /// <summary>
    /// Tracks process and thread names.
    /// TODO: Tracks loaded modules to resolve symbols
    /// This class expects timestamp data happens chronologically but also allows for retroactive changes like a process name for an already-known pid to be assigned.
    /// When this happens the caller is instructed to bump the generation id so that any filtering can be re-evaluated with the new information.
    /// This class is very carefully designed so that the same timestamp never maps to a different process/thread when new start/stop/name events are observed.
    /// This is important because filtering that runs as events stream in won't know to re-evaluate unless told to by bumping the generation id.
    /// </summary>
    public class ProcessDatabase
    {
        record struct TrackedProcess
        {
            public string? Name;
            public DateTime Start;
            public DateTime? End;
        }

        record struct TrackedThread
        {
            public string? Name;
            public DateTime Start;
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
                    for (int i = processes.Count - 1; i >= 0; i--)
                    {
                        var process = processes[i];
                        DateTime? end = process.End ?? (i == processes.Count - 1 ? null : processes[i + 1].Start);
                        if (process.Start <= timestamp && (end == null || end >= timestamp))
                        {
                            return process.Name;
                        }
                    }
                }
            }

            return null;
        }

        // Returns true if an existing process had its name changed.
        public bool ProcessStart(int pid, string? name, DateTime startTime)
        {
            lock (_trackedProcesses)
            {
                if (!_trackedProcesses.TryGetValue(pid, out var processes))
                {
                    processes = new List<TrackedProcess>();
                    _trackedProcesses[pid] = processes;
                }

                // See if this event falls into an existing TrackedProcess already and its name must be updated.
                if (name != null && processes.Count > 0)
                {
                    TrackedProcess tp = processes[^1];
                    if (tp.End == null && name != tp.Name)
                    {
                        processes[^1] = tp with { Name = name };
                        return true; // Caller should bump the generation id so that any filtering can be re-evaluated with the new information.
                    }
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
                    for (int i = threads.Count - 1; i >= 0; i--)
                    {
                        var thread = threads[i];
                        DateTime? end = thread.End ?? (i == threads.Count - 1 ? null : threads[i + 1].Start);
                        if (thread.Start <= timestamp && (end == null || end >= timestamp))
                        {
                            return thread.Name;
                        }
                    }
                }
            }

            return null;
        }

        // Returns true if an existing thread had its name changed.
        public bool ThreadStart(int tid, string? name, DateTime startTime)
        {
            lock (_trackedThreads)
            {
                if (!_trackedThreads.TryGetValue(tid, out var threads))
                {
                    threads = new List<TrackedThread>();
                    _trackedThreads[tid] = threads;
                }

                // See if this event falls into an existing TrackedThread already and its name must be updated.
                if (name != null && threads.Count > 0)
                {
                    TrackedThread tt = threads[^1];
                    if (tt.End == null && name != tt.Name)
                    {
                        threads[^1] = tt with { Name = name };
                        return true; // Caller should bump the generation id so that any filtering can be re-evaluated with the new information.
                    }
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
                    return false; // No existing thread to update, so just ignore this event.
                }

                threads[^1] = threads[^1] with { Name = name };
                return true; // Caller should bump the generation id so that any filtering can be re-evaluated with the new information.
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

    }
}
