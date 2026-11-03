using System.Runtime.InteropServices;

namespace PredatorControlApp;

internal static class FirmwareParsingNative
{
    [DllImport("predator_core.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int predator_smbios_parse(byte[] input, nuint length,
        [Out] uint[] records, nuint capacity, out int version);

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
}
