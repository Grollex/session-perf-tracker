using System.Globalization;

namespace SessionPerfTracker.Infrastructure.Gpu;

/// <summary>
/// Parses WDDM GPU performance counter instance names, e.g.
/// <c>pid_1234_luid_0x00000000_0x0000D1B4_phys_0_eng_3_engtype_Compute_0</c>.
/// </summary>
internal readonly record struct GpuCounterInstanceName(
    int? ProcessId,
    string AdapterLuid,
    int PhysicalIndex,
    int? EngineIndex,
    string? EngineType)
{
    public string EngineKey => $"{AdapterLuid}|{PhysicalIndex}|{EngineIndex}";
    public string AdapterKey => $"{AdapterLuid}|{PhysicalIndex}";

    public static bool TryParse(string instanceName, out GpuCounterInstanceName parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(instanceName))
        {
            return false;
        }

        var name = instanceName.Trim();
        var duplicateMarker = name.IndexOf('#');
        if (duplicateMarker > 0)
        {
            name = name[..duplicateMarker];
        }

        var tokens = name.Split('_');
        int? processId = null;
        string? luidHigh = null;
        string? luidLow = null;
        var physical = 0;
        int? engine = null;
        string? engineType = null;

        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index];
            if (token.Equals("pid", StringComparison.OrdinalIgnoreCase) && index + 1 < tokens.Length)
            {
                if (int.TryParse(tokens[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
                {
                    processId = pid;
                }

                index++;
            }
            else if (token.Equals("luid", StringComparison.OrdinalIgnoreCase) && index + 2 < tokens.Length)
            {
                luidHigh = tokens[index + 1];
                luidLow = tokens[index + 2];
                index += 2;
            }
            else if (token.Equals("phys", StringComparison.OrdinalIgnoreCase) && index + 1 < tokens.Length)
            {
                _ = int.TryParse(tokens[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out physical);
                index++;
            }
            else if (token.Equals("eng", StringComparison.OrdinalIgnoreCase) && index + 1 < tokens.Length)
            {
                if (int.TryParse(tokens[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var engineIndex))
                {
                    engine = engineIndex;
                }

                index++;
            }
            else if (token.Equals("engtype", StringComparison.OrdinalIgnoreCase))
            {
                engineType = index + 1 < tokens.Length
                    ? string.Join('_', tokens[(index + 1)..])
                    : string.Empty;
                break;
            }
        }

        if (luidHigh is null || luidLow is null)
        {
            return false;
        }

        parsed = new GpuCounterInstanceName(
            processId,
            $"{luidHigh}_{luidLow}".ToLowerInvariant(),
            physical,
            engine,
            engineType);
        return true;
    }

    public static bool TryParseLuid(string adapterLuid, out uint lowPart, out int highPart)
    {
        lowPart = 0;
        highPart = 0;
        var parts = adapterLuid.Split('_');
        if (parts.Length != 2)
        {
            return false;
        }

        if (!TryParseHex(parts[0], out var high) || !TryParseHex(parts[1], out lowPart))
        {
            return false;
        }

        highPart = unchecked((int)high);
        return true;
    }

    private static bool TryParseHex(string value, out uint parsed)
    {
        var text = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        return uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsed);
    }
}
