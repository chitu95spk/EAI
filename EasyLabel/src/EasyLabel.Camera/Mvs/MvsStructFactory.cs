namespace EasyLabel.Camera.Mvs;

/// <summary>
/// Creates native structs with their ByValArray fields allocated, so the marshaller never meets a null array
/// when the struct is passed by ref (the SDK fills the struct on return).
/// </summary>
internal static class MvsStructFactory
{
    internal static MV_FRAME_OUT_INFO_EX FrameInfo()
        => new MV_FRAME_OUT_INFO_EX { nReserved = new uint[26] };

    internal static MVCC_INTVALUE_EX IntValueEx()
        => new MVCC_INTVALUE_EX { nReserved = new uint[16] };

    internal static MVCC_FLOATVALUE FloatValue()
        => new MVCC_FLOATVALUE { nReserved = new uint[4] };

    internal static MVCC_ENUMVALUE EnumValue()
        => new MVCC_ENUMVALUE { nSupportValue = new uint[64], nReserved = new uint[4] };

    internal static MVCC_ENUMVALUE_EX EnumValueEx()
        => new MVCC_ENUMVALUE_EX { nSupportValue = new uint[256], nReserved = new uint[4] };

    internal static MVCC_ENUMENTRY EnumEntry(uint value)
        => new MVCC_ENUMENTRY { nValue = value, chSymbolic = new byte[64], nReserved = new uint[4] };
}
