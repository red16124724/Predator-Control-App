using System.Runtime.InteropServices;

namespace PredatorControlApp;

internal static class FirmwareParsingNative
{
    [DllImport("predator_core.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int predator_smbios_parse(byte[] input, nuint length,
        [Out] uint[] records, nuint capacity, out int version);

    [DllImport("predator_core.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int predator_acpi_fetch_and_parse_ppcc(out PredatorNativeCore.NativePpccSummary outSummary);

    [DllImport("predator_core.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int predator_smbios_fetch_and_parse(out uint outGaming, out uint outHotkey, out byte outDgpu, out ushort outVersion);

    [DllImport("predator_core.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int predator_uefi_fetch_and_scan_power(out NativeUefiPowerConfig outConfig);

    internal static bool TryParseSmbios(ReadOnlySpan<byte> table, out AcerSmbios result)
    {
        result = AcerSmbios.Empty;
        if (table.IsEmpty || !PredatorNativeCore.IsNativeCoreAvailable) return false;
        try
        {
            byte[] bytes = table.ToArray();
            uint[] records = new uint[table.Length / 3 + 1];
            int count = predator_smbios_parse(bytes, (nuint)bytes.Length, records, (nuint)records.Length, out int version);
            if (count < 0 || count > records.Length || version < -1 || version > 65535) return false;
            var gaming = new List<AcerSmbiosRecord>();
            var hotkeys = new List<AcerSmbiosRecord>();
            for (int i = 0; i < count; i++)
            {
                uint record = records[i];
                if ((record & 0xfe000000u) != 0) return false;
                var item = new AcerSmbiosRecord((byte)record, (ushort)(record >> 8));
                if ((record & 0x01000000u) != 0) hotkeys.Add(item);
                else gaming.Add(item);
            }
            byte? major = version < 0 ? null : (byte)(version >> 8);
            byte? minor = version < 0 ? null : (byte)version;
            result = new AcerSmbios(major, minor, gaming, hotkeys);
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or SEHException)
        {
            return false;
        }
    }

    internal static bool TryFetchAndParsePpccDirect(out PredatorNativeCore.NativePpccSummary summary)
    {
        summary = default;
        if (!PredatorNativeCore.IsNativeCoreAvailable) return false;
        try
        {
            return predator_acpi_fetch_and_parse_ppcc(out summary) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or SEHException)
        {
            return false;
        }
    }

    internal static bool TryFetchAndParseSmbiosDirect(out uint gaming, out uint hotkey, out byte dgpu, out ushort ver)
    {
        gaming = 0; hotkey = 0; dgpu = 0; ver = 0;
        if (!PredatorNativeCore.IsNativeCoreAvailable) return false;
        try
        {
            return predator_smbios_fetch_and_parse(out gaming, out hotkey, out dgpu, out ver) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or SEHException)
        {
            return false;
        }
    }

    internal static bool TryFetchAndScanUefiPowerDirect(out NativeUefiPowerConfig config)
    {
        config = default;
        if (!PredatorNativeCore.IsNativeCoreAvailable) return false;
        try
        {
            return predator_uefi_fetch_and_scan_power(out config) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or SEHException)
        {
            return false;
        }
    }
}
