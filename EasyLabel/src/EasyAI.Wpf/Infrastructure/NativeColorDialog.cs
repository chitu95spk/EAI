using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace EasyAI.Desktop.Infrastructure;

/// <summary>
/// The standard Windows colour dialog (comdlg32 ChooseColor). WPF has no colour dialog of its own and WinForms is not referenced,
/// so the native one is called directly.
/// </summary>
public static class NativeColorDialog
{
    private const int CcRgbInit = 0x00000001;
    private const int CcFullOpen = 0x00000002;
    private const int CcAnyColor = 0x00000100;
    private const int CustomColorCount = 16;

    [StructLayout(LayoutKind.Sequential)]
    private struct ChooseColorData
    {
        public int StructSize;
        public IntPtr Owner;
        public IntPtr Instance;
        public int RgbResult;
        public IntPtr CustomColors;
        public int Flags;
        public IntPtr CustomData;
        public IntPtr Hook;
        public IntPtr TemplateName;
    }

    [DllImport("comdlg32.dll", EntryPoint = "ChooseColorW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChooseColor(ref ChooseColorData data);

    /// <summary>Shows the dialog over the active window, starting at <paramref name="currentRgb"/> (0xRRGGBB). False when cancelled.</summary>
    public static bool TryChoose(uint currentRgb, out uint chosenRgb)
    {
        chosenRgb = currentRgb;
        IntPtr custom = Marshal.AllocHGlobal(CustomColorCount * sizeof(int));
        try
        {
            for (int i = 0; i < CustomColorCount; i++)
            {
                Marshal.WriteInt32(custom, i * sizeof(int), 0x00FFFFFF);
            }

            var data = new ChooseColorData
            {
                StructSize = Marshal.SizeOf<ChooseColorData>(),
                Owner = ActiveWindowHandle(),
                RgbResult = ToColorRef(currentRgb),
                CustomColors = custom,
                Flags = CcRgbInit | CcFullOpen | CcAnyColor,
            };
            if (!ChooseColor(ref data))
            {
                return false;
            }

            chosenRgb = FromColorRef(data.RgbResult);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(custom);
        }
    }

    /// <summary>COLORREF is 0x00BBGGRR.</summary>
    private static int ToColorRef(uint rgb)
    {
        uint r = (rgb >> 16) & 0xFF;
        uint g = (rgb >> 8) & 0xFF;
        uint b = rgb & 0xFF;
        return (int)((b << 16) | (g << 8) | r);
    }

    private static uint FromColorRef(int colorRef)
    {
        uint value = (uint)colorRef;
        uint r = value & 0xFF;
        uint g = (value >> 8) & 0xFF;
        uint b = (value >> 16) & 0xFF;
        return (r << 16) | (g << 8) | b;
    }

    private static IntPtr ActiveWindowHandle()
    {
        Application? app = Application.Current;
        if (app == null)
        {
            return IntPtr.Zero;
        }

        foreach (Window window in app.Windows)
        {
            if (window.IsActive)
            {
                return new WindowInteropHelper(window).Handle;
            }
        }

        return app.MainWindow == null ? IntPtr.Zero : new WindowInteropHelper(app.MainWindow).Handle;
    }
}
