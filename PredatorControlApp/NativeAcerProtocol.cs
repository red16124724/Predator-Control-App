using System.Runtime.InteropServices;

namespace PredatorControlApp;

internal static class NativeAcerProtocol
{
    [DllImport("predator_core.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int predator_protocol_encode(uint operation, ulong a, ulong b, ulong c, ulong d, out ulong result);

    internal static bool TryEncode(uint operation, ulong a, ulong b, ulong c, ulong d, out ulong result)
    {
        result = 0;
        if (!PredatorNativeCore.IsNativeCoreAvailable) return false;
        try { return predator_protocol_encode(operation, a, b, c, d, out result) == 1; }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
        catch (BadImageFormatException) { return false; }
    }

    internal static ulong Signed(int value) => unchecked((ulong)(long)value);
}
