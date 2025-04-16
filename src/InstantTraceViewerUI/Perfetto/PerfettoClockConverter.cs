using System;
using System.Collections.Generic;
using System.Linq;
using Perfetto.Protos;

namespace InstantTraceViewerUI.Perfetto
{
    internal class PerfettoClockConverter
    {
        public PerfettoClockConverter(Trace trace)
        {
            ClockSnapshots = trace.Packet
                .Where(p => p.ClockSnapshot != null)
                .Select(p => p.ClockSnapshot)
                .ToArray();

            var bootTimestamps = trace.Packet
                .Where(p => p.HasTimestamp && (p.TimestampClockId == (int)BuiltinClock.Boottime || p.TimestampClockId == 0 /* 0 indicates default which is boottime */))
                .Select(p => p.Timestamp);
            EarliestBootTimestamp = bootTimestamps.DefaultIfEmpty().Min();
        }

        public IReadOnlyCollection<ClockSnapshot> ClockSnapshots { get; private init; }

        // ui.perfetto.dev appears to use the earliest timestamp as time 0 so match that behavior here. This is in BuiltinClock.Boottime domain.
        public ulong EarliestBootTimestamp { get; private init; }

        public ulong ConvertTimestamp(BuiltinClock fromClockId, BuiltinClock toClockId, ulong fromTimestamp)
        {
            Func<ClockSnapshot.Types.Clock, bool> isFromClock = c => c.HasClockId && c.HasTimestamp && c.ClockId == (int)fromClockId;
            Func<ClockSnapshot.Types.Clock, bool> isToClock = c => c.HasClockId && c.HasTimestamp && c.ClockId == (int)toClockId;

            // Find the snapshot with the closest clock snapshot matching "from" clock.
            var closestFromMatch = ClockSnapshots
                .Where(s => s.Clocks.Any(isFromClock) && s.Clocks.Any(isToClock))
                .OrderBy(s => Math.Abs((long)s.Clocks.Single(isFromClock).Timestamp - (long)fromTimestamp))
                .FirstOrDefault();
            if (closestFromMatch == null)
            {
                // What to do here?
                System.Diagnostics.Debug.Fail("No matching clock snapshot");
                return 0;
            }

            // Get the from and to timestamps from the snapshot.
            ulong fromSnapshotTimestamp = closestFromMatch.Clocks.Single(isFromClock).Timestamp;
            ulong toSnapshotTimestamp = closestFromMatch.Clocks.Single(isToClock).Timestamp;

            // Convert from "from" to "to".
            // TODO: Technically a user-defined clock may specify "UnitMultiplierNs" (0 when not set).
            return (ulong)((long)fromTimestamp + ((long)toSnapshotTimestamp - (long)fromSnapshotTimestamp));
        }

        public DateTime GetPacketRealtimeTimestamp(TracePacket packet)
        {
            // Some packets like SystemInfo and TraceConfig do not have a timestamp so they will show at the top with the earliest timestamp.
            if (!packet.HasTimestamp)
            {
                return RealTimeClockToDateTime(ConvertTimestamp(BuiltinClock.Boottime, BuiltinClock.Realtime, EarliestBootTimestamp));
            }

            BuiltinClock fromClock = packet.HasTimestampClockId ? (BuiltinClock)packet.TimestampClockId : BuiltinClock.Boottime;
            return RealTimeClockToDateTime(ConvertTimestamp(fromClock, BuiltinClock.Realtime, packet.Timestamp));
        }

        public static DateTime RealTimeClockToDateTime(ulong timestamp)
        {
            var unixTime = (long)(timestamp / 1000000000);
            var unixTimeFraction = (long)(timestamp % 1000000000); // Fraction of a second in nanoseconds.
            return DateTime.UnixEpoch + TimeSpan.FromTicks(unixTime * TimeSpan.TicksPerSecond) + TimeSpan.FromTicks(unixTimeFraction / 100);
        }
    }
}
