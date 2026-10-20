using System.Runtime.InteropServices;

namespace EasyLabel.Camera.Tests;

/// <summary>
/// Sizes and offsets are computed by hand from C:\Program Files (x86)\MVS\Development\Includes\CameraParams.h
/// (x64, natural alignment, no #pragma pack in that header). A mismatch here means the struct declaration drifted
/// from the header; the header itself still has to match the installed DLL.
/// </summary>
public sealed class MvsNativeLayoutTests
{
    [Fact]
    public void GigeDeviceInfo_Is216Bytes()
    {
        // CameraParams.h:25-42: 5 uint = 20; 32+32+32+48+16+16 = 176; nNetExport 4; nReserved 4 uint = 16 -> 216.
        Assert.Equal(216, Marshal.SizeOf<EasyLabel.Camera.Mvs.MV_GIGE_DEVICE_INFO>());
    }

    [Fact]
    public void Usb3DeviceInfo_Is540Bytes()
    {
        // CameraParams.h:48-69: 4 byte + 2 ushort(4) + uint = 12; 8 arrays * 64 = 512; nbcdUSB 4 + nDeviceAddress 4 + nReserved 2 uint = 8 -> 540.
        Assert.Equal(540, Marshal.SizeOf<EasyLabel.Camera.Mvs.MV_USB3_DEVICE_INFO>());
    }

    [Fact]
    public void DeviceInfo_Is636Bytes_WithUnionAt32()
    {
        // CameraParams.h:159-186: ushort+ushort = 4; MAC high/low, tlayer, devtype = 16 -> 20; nReserved 3 uint = 12 -> 32.
        // Union = max(GigE 216, USB3 540, CamL 6*64+38*4 = 536, CML/CXP/XoF 8*64+7*4 = 540, Vir 9*64+7*4 = 604) = 604.
        // 32 + 604 = 636.
        Assert.Equal(604, Marshal.SizeOf<EasyLabel.Camera.Mvs.MV_DEVICE_SPECIAL_INFO>());
        Assert.Equal(636, Marshal.SizeOf<EasyLabel.Camera.Mvs.MV_CC_DEVICE_INFO>());
        Assert.Equal(12, (int)Marshal.OffsetOf<EasyLabel.Camera.Mvs.MV_CC_DEVICE_INFO>("nTLayerType"));
        Assert.Equal(32, (int)Marshal.OffsetOf<EasyLabel.Camera.Mvs.MV_CC_DEVICE_INFO>("SpecialInfo"));
        Assert.Equal(EasyLabel.Camera.Mvs.MvsNativeSizes.DeviceInfo, Marshal.SizeOf<EasyLabel.Camera.Mvs.MV_CC_DEVICE_INFO>());
        Assert.Equal(EasyLabel.Camera.Mvs.MvsNativeSizes.DeviceInfoTLayerTypeOffset, (int)Marshal.OffsetOf<EasyLabel.Camera.Mvs.MV_CC_DEVICE_INFO>("nTLayerType"));
        Assert.Equal(EasyLabel.Camera.Mvs.MvsNativeSizes.DeviceInfoSpecialOffset, (int)Marshal.OffsetOf<EasyLabel.Camera.Mvs.MV_CC_DEVICE_INFO>("SpecialInfo"));
    }

    [Fact]
    public void DeviceInfoList_Is2056Bytes()
    {
        // CameraParams.h:194-199: nDeviceNum 4, 4 padding (pointer alignment 8), 256 pointers * 8 = 2048 -> 2056.
        Assert.Equal(2056, Marshal.SizeOf<EasyLabel.Camera.Mvs.MV_CC_DEVICE_INFO_LIST>());
        Assert.Equal(EasyLabel.Camera.Mvs.MvsNativeSizes.DeviceInfoList, Marshal.SizeOf<EasyLabel.Camera.Mvs.MV_CC_DEVICE_INFO_LIST>());
        Assert.Equal(EasyLabel.Camera.Mvs.MvsNativeSizes.DeviceInfoListPointersOffset, (int)Marshal.OffsetOf<EasyLabel.Camera.Mvs.MV_CC_DEVICE_INFO_LIST>("pDeviceInfo"));
    }

    [Fact]
    public void FrameOutInfoEx_Is256Bytes_WithHeaderOffsets()
    {
        // CameraParams.h:331-402. Offsets: 2 ushort 0,2; enum 4; 6 uint 8..23 (nFrameNum, TS high/low, nReserved0);
        // nHostTimeStamp int64 24; nFrameLen 32; 3 uint 36..44; 2 float 48,52; 4 uint (brightness, R, G, B) 56..68;
        // nFrameCounter 72, nTriggerIndex 76, nInput 80, nOutput 84; 4 ushort 88..94; nLostPacket 96; nUnparsedChunkNum 100;
        // union ptr 104; nExtendWidth 112; nExtendHeight 116; uint64 nFrameLenEx 120; nReserved1 128; nSubImageNum 132;
        // union ptr 136; union ptr 144; nReserved[26] 152 + 104 = 256.
        Type t = typeof(EasyLabel.Camera.Mvs.MV_FRAME_OUT_INFO_EX);
        Assert.Equal(256, Marshal.SizeOf<EasyLabel.Camera.Mvs.MV_FRAME_OUT_INFO_EX>());
        Assert.Equal(4, (int)Marshal.OffsetOf(t, "enPixelType"));
        Assert.Equal(8, (int)Marshal.OffsetOf(t, "nFrameNum"));
        Assert.Equal(24, (int)Marshal.OffsetOf(t, "nHostTimeStamp"));
        Assert.Equal(32, (int)Marshal.OffsetOf(t, "nFrameLen"));
        Assert.Equal(48, (int)Marshal.OffsetOf(t, "fGain"));
        Assert.Equal(88, (int)Marshal.OffsetOf(t, "nOffsetX"));
        Assert.Equal(96, (int)Marshal.OffsetOf(t, "nLostPacket"));
        Assert.Equal(104, (int)Marshal.OffsetOf(t, "UnparsedChunkList"));
        Assert.Equal(112, (int)Marshal.OffsetOf(t, "nExtendWidth"));
        Assert.Equal(120, (int)Marshal.OffsetOf(t, "nFrameLenEx"));
        Assert.Equal(136, (int)Marshal.OffsetOf(t, "SubImageList"));
        Assert.Equal(144, (int)Marshal.OffsetOf(t, "UserPtr"));
        Assert.Equal(152, (int)Marshal.OffsetOf(t, "nReserved"));
    }

    [Fact]
    public void NodeValueStructs_HaveHeaderSizes()
    {
        // CameraParams.h:1175 MVCC_INTVALUE: 4 uint + 4 reserved uint = 32.
        Assert.Equal(32, Marshal.SizeOf<EasyLabel.Camera.Mvs.MVCC_INTVALUE>());

        // CameraParams.h:1187 MVCC_INTVALUE_EX: 4 int64 = 32 + 16 reserved uint = 64 -> 96.
        Assert.Equal(96, Marshal.SizeOf<EasyLabel.Camera.Mvs.MVCC_INTVALUE_EX>());

        // CameraParams.h:1199 MVCC_FLOATVALUE: 3 float = 12 + 4 reserved uint = 16 -> 28.
        Assert.Equal(28, Marshal.SizeOf<EasyLabel.Camera.Mvs.MVCC_FLOATVALUE>());

        // CameraParams.h:1137 MVCC_ENUMVALUE: 2 uint = 8 + 64 uint = 256 + 4 reserved uint = 16 -> 280.
        Assert.Equal(280, Marshal.SizeOf<EasyLabel.Camera.Mvs.MVCC_ENUMVALUE>());

        // CameraParams.h:1151 MVCC_ENUMVALUE_EX: 8 + 256 uint = 1024 + 16 -> 1048.
        Assert.Equal(1048, Marshal.SizeOf<EasyLabel.Camera.Mvs.MVCC_ENUMVALUE_EX>());

        // CameraParams.h:1165 MVCC_ENUMENTRY: nValue 4 + 64 char + 4 reserved uint = 16 -> 84.
        Assert.Equal(84, Marshal.SizeOf<EasyLabel.Camera.Mvs.MVCC_ENUMENTRY>());
    }

    [Fact]
    public void PixelTypes_MatchPixelTypeHeader()
    {
        // PixelType.h:10-18: MONO 0x01000000 | (bits << 16) | id; COLOR 0x02000000.
        Assert.Equal(0x01000000u | (8u << 16) | 0x0001u, EasyLabel.Camera.Mvs.MvsPixelTypes.Mono8);
        Assert.Equal(0x01000000u | (16u << 16) | 0x0003u, EasyLabel.Camera.Mvs.MvsPixelTypes.Mono10);
        Assert.Equal(0x01000000u | (16u << 16) | 0x0005u, EasyLabel.Camera.Mvs.MvsPixelTypes.Mono12);
        Assert.Equal(0x01000000u | (8u << 16) | 0x0009u, EasyLabel.Camera.Mvs.MvsPixelTypes.BayerRG8);
        Assert.Equal(0x02000000u | (24u << 16) | 0x0014u, EasyLabel.Camera.Mvs.MvsPixelTypes.Rgb8Packed);
        Assert.Equal(0x02000000u | (24u << 16) | 0x0015u, EasyLabel.Camera.Mvs.MvsPixelTypes.Bgr8Packed);
        Assert.Equal(CameraPixelFormat.BayerGB8, EasyLabel.Camera.Mvs.MvsPixelTypes.ToCameraFormat(0x0108000A));
        Assert.Equal(CameraPixelFormat.Unknown, EasyLabel.Camera.Mvs.MvsPixelTypes.ToCameraFormat(0x02180003));
    }
}
