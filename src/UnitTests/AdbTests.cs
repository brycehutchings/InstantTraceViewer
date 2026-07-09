using System.Buffers.Binary;
using System.Text;
using InstantTraceViewer.Adb;

namespace InstantTraceViewerTests
{
    [TestClass]
    public class AdbTests
    {
        [TestMethod]
        public void ParseDeviceList()
        {
            var devices = AdbDeviceListParser.Parse("emulator-5554\tdevice product:sdk_gphone64_x86_64 model:sdk_gphone64_x86_64 device:emu64x vendor:Google\r\n");

            Assert.AreEqual(1, devices.Count);
            Assert.AreEqual("emulator-5554", devices[0].Serial);
            Assert.AreEqual("device", devices[0].State);
            Assert.AreEqual("emu64x", devices[0].Codename);
            Assert.AreEqual("sdk_gphone64_x86_64", devices[0].Model);
            Assert.AreEqual("sdk_gphone64_x86_64", devices[0].Product);
        }

        [TestMethod]
        public void ParseModernProcessList()
        {
            var processes = AdbProcessParser.Parse("PID NAME\n1 init\n1234 com.example.app\n");

            Assert.AreEqual(2, processes.Count);
            Assert.AreEqual(1, processes[0].ProcessId);
            Assert.AreEqual("init", processes[0].Name);
            Assert.AreEqual(1234, processes[1].ProcessId);
            Assert.AreEqual("com.example.app", processes[1].Name);
        }

        [TestMethod]
        public void ParseLegacyProcessList()
        {
            var processes = AdbProcessParser.Parse("USER PID PPID VSIZE RSS WCHAN ADDR S NAME\nshell 345 1 123 456 0 0 S logcat\n");

            Assert.AreEqual(1, processes.Count);
            Assert.AreEqual(345, processes[0].ProcessId);
            Assert.AreEqual("logcat", processes[0].Name);
        }

        [TestMethod]
        public void ParsePackageList()
        {
            var packages = AdbPackageParser.Parse(
                "package:com.android.chrome uid:10123\r\n" +
                "package:com.google.android.gms uid:10012\r\n" +
                "package:com.google.android.gsf uid:10012\r\n" +
                "package:android uid:1000\r\n");

            Assert.AreEqual(4, packages.Count);
            Assert.AreEqual("com.android.chrome", packages[0].PackageName);
            Assert.AreEqual((uint)10123, packages[0].Uid);
            Assert.AreEqual("com.google.android.gms", packages[1].PackageName);
            Assert.AreEqual((uint)10012, packages[1].Uid);
            Assert.AreEqual("com.google.android.gsf", packages[2].PackageName);
            Assert.AreEqual((uint)10012, packages[2].Uid);
            Assert.AreEqual("android", packages[3].PackageName);
            Assert.AreEqual((uint)1000, packages[3].Uid);
        }

        [TestMethod]
        public void ParsePackageList_SkipsMalformedLines()
        {
            var packages = AdbPackageParser.Parse(
                "package:com.example uid:10001\n" +
                "package:no.uid.here\n" +               // no uid field
                "package: uid:12345\n" +                 // empty package name
                "not-a-package-line\n" +
                "package:another uid:notanumber\n" +    // non-numeric uid
                "package:okay uid:10002\n");

            Assert.AreEqual(2, packages.Count);
            Assert.AreEqual("com.example", packages[0].PackageName);
            Assert.AreEqual((uint)10001, packages[0].Uid);
            Assert.AreEqual("okay", packages[1].PackageName);
            Assert.AreEqual((uint)10002, packages[1].Uid);
        }

        [TestMethod]
        public void ParseBinaryLogcatEntry()
        {
            var payload = new byte[1 + "ActivityManager".Length + 1 + "Start proc 1234:com.example/u0a123 for activity".Length + 1];
            payload[0] = 4;
            Encoding.UTF8.GetBytes("ActivityManager").CopyTo(payload, 1);
            Encoding.UTF8.GetBytes("Start proc 1234:com.example/u0a123 for activity").CopyTo(payload, 1 + "ActivityManager".Length + 1);

            var header = new byte[24];
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0, 2), (ushort)payload.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2, 2), (ushort)header.Length);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), 4321);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, 4), 5678);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12, 4), 946684800);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16, 4), 123456789);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20, 4), (int)AdbLogId.System);

            var entry = AdbLogcatBinaryParser.ParseEntry(header, payload);

            Assert.AreEqual(4321, entry.ProcessId);
            Assert.AreEqual(5678, entry.ThreadId);
            Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(946684800).AddTicks(123456789 / TimeSpan.NanosecondsPerTick), entry.TimeStamp);
            Assert.AreEqual(AdbLogPriority.Info, entry.Priority);
            Assert.AreEqual(AdbLogId.System, entry.Id);
            Assert.IsNull(entry.Uid, "v3 headers do not carry a UID field.");
            Assert.AreEqual("ActivityManager", entry.Tag);
            Assert.AreEqual("Start proc 1234:com.example/u0a123 for activity", entry.Message);
        }

        [TestMethod]
        public void ParseBinaryLogcatEntryWithUid()
        {
            var payload = new byte[1 + "MyTag".Length + 1 + "hello".Length + 1];
            payload[0] = 4;
            Encoding.UTF8.GetBytes("MyTag").CopyTo(payload, 1);
            Encoding.UTF8.GetBytes("hello").CopyTo(payload, 1 + "MyTag".Length + 1);

            // v4 header: 28 bytes, includes uid at offset 24.
            var header = new byte[28];
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0, 2), (ushort)payload.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2, 2), (ushort)header.Length);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), 1000);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8, 4), 1000);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12, 4), 946684800);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16, 4), 0);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20, 4), (int)AdbLogId.Main);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24, 4), 10123);

            var entry = AdbLogcatBinaryParser.ParseEntry(header, payload);

            Assert.AreEqual((uint?)10123, entry.Uid);
            Assert.AreEqual("MyTag", entry.Tag);
            Assert.AreEqual("hello", entry.Message);
        }
    }
}