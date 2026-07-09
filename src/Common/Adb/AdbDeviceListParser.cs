namespace InstantTraceViewer.Adb
{
    public static class AdbDeviceListParser
    {
        public static IReadOnlyList<AdbDevice> Parse(string deviceList)
        {
            var devices = new List<AdbDevice>();

            foreach (var line in deviceList.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length < 2)
                {
                    continue;
                }

                var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 2; i < parts.Length; i++)
                {
                    var separator = parts[i].IndexOf(':');
                    if (separator > 0)
                    {
                        properties[parts[i][..separator]] = parts[i][(separator + 1)..];
                    }
                }

                properties.TryGetValue("device", out var codename);
                properties.TryGetValue("model", out var model);
                properties.TryGetValue("product", out var product);

                devices.Add(new AdbDevice
                {
                    Serial = parts[0],
                    State = parts[1],
                    Codename = codename ?? model ?? parts[0],
                    Model = model ?? string.Empty,
                    Product = product ?? string.Empty,
                });
            }

            return devices;
        }
    }
}