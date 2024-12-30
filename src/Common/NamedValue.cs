using System.Text;

namespace InstantTraceViewer
{
    public delegate bool TryGetCustomizedValue(string? name, object value, out string customValue);

    public struct NamedValue
    {
        private static readonly IFormatProvider FormatProvider = System.Globalization.CultureInfo.InvariantCulture;

        public string? Name;
        public object Value;

        public NamedValue(string? name, object value)
        {
            Name = name;
            Value = value;
        }

        public static string GetCollectionString(IReadOnlyCollection<NamedValue> namedValues, bool allowMultiline, TryGetCustomizedValue? tryGetCustomizedValue = null)
        {
            if (namedValues == null || namedValues.Count == 0)
            {
                return string.Empty;
            }

            StringBuilder sb = new();
            foreach (var namedValue in namedValues)
            {
                if (sb.Length > 0)
                {
                    sb.Append(allowMultiline ? '\n' : ' ');
                }

                sb.Append(namedValue.ToString(allowMultiline, tryGetCustomizedValue));
            }
            return sb.ToString();
        }

        public string GetValueString(bool allowMultiline = false) => ObjectToString(Name, Value, allowMultiline, null, 0);

        private static string ObjectToString(string? name, object value, bool allowMultiline, TryGetCustomizedValue? tryGetCustomizedValue, int nestingLevel)
        {
            if (tryGetCustomizedValue != null && tryGetCustomizedValue(name, value, out string customValue))
            {
                return customValue;
            }
            else if (value == null)
            {
                return "";
            }
            else if (value is ulong valueUlong)
            {
                return "0x" + valueUlong.ToString("X8", FormatProvider);
            }
            else if (value is int valueInt)
            {
                if (valueInt != 0 && name == "IPv4Address")
                {
                    return $"{(valueInt & 0xFF)}.{((valueInt >> 8) & 0xFF)}.{((valueInt >> 16) & 0xFF)}.{((valueInt >> 24) & 0xFF)}";
                }
                else if (string.Equals(name, "hresult", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "hr", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "ntstatus", StringComparison.OrdinalIgnoreCase))
                {
                    return $"0x{valueInt:X8}";
                }

                return valueInt.ToString(FormatProvider);
            }
            else if (value is long valueLong)
            {
                if (name == "objectId")
                {
                    return "0x" + valueLong.ToString("X8");
                }

                return valueLong.ToString(FormatProvider);
            }
            else if (value is double valueDouble)
            {
                return valueDouble.ToString(FormatProvider);
            }
            else if (value is DateTime dateTimeValue)
            {
                return dateTimeValue.ToString("yyyy-MM-dd HH:mm:ss.ffffff", FormatProvider);
            }
            else if (value is DateTimeOffset dateTimeOffsetValue)
            {
                return dateTimeOffsetValue.ToString("yyyy-MM-dd HH:mm:ss.ffffff", FormatProvider);
            }
            else if (value is byte[] byteArrayValue)
            {
                return BitConverter.ToString(byteArrayValue).Replace("-", " ");
            }
            else if (value is Array arrayValue)
            {
                StringBuilder sb = new();
                sb.Append('[');
                foreach (object item in arrayValue)
                {
                    if (sb.Length > 1)
                    {
                        sb.Append(", ");
                    }

                    sb.Append(ObjectToString(name, item, allowMultiline, tryGetCustomizedValue, nestingLevel));
                }
                sb.Append(']');
                return sb.ToString();
            }
            else if (value is IDictionary<string, object> structValue)
            {
                StringBuilder sb = new();
                sb.Append('{');
                nestingLevel++;
                if (allowMultiline && structValue.Count > 0)
                {
                    sb.AppendLine();
                }
                foreach (var item in structValue)
                {
                    if (!allowMultiline && sb.Length > 1)
                    {
                        sb.Append(", ");
                    }
                    else if (allowMultiline)
                    {
                        sb.Append(' ', nestingLevel * 4);
                    }

                    sb.Append(item.Key);
                    sb.Append(allowMultiline ? ": " : ":");
                    sb.Append(ObjectToString(item.Key, item.Value, allowMultiline, tryGetCustomizedValue, nestingLevel));

                    if (allowMultiline)
                    {
                        sb.AppendLine();
                    }
                }
                nestingLevel--;
                if (allowMultiline && nestingLevel > 0)
                {
                    sb.Append(' ', nestingLevel * 4);
                }
                sb.Append('}');
                return sb.ToString();
            }
            else
            {
                try
                {
                    return value.ToString()!;
                }
                catch (Exception ex)
                {
                    return $"VALUE LOOKUP EXCEPTION: {ex.Message}";
                }
            }
        }

        public override string ToString()
        {
            if (Name == null)
            {
                return GetValueString();
            }

            return $"{Name}:{GetValueString()}";
        }

        public string ToString(bool allowMultiline, TryGetCustomizedValue? tryGetCustomizedValue = null)
        {
            if (Name == null)
            {
                return ObjectToString(Name, Value, allowMultiline, tryGetCustomizedValue, 0);
            }

            string separator = allowMultiline ? ": " : ":";
            return $"{Name}{separator}{ObjectToString(Name, Value, allowMultiline, tryGetCustomizedValue, 0)}";
        }
    }
}
