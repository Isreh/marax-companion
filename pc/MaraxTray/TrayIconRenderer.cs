using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace MaraxTray;

internal readonly record struct IconFace(string Text, Color Background)
{
    // White text on these colors stays readable on light and dark taskbars.
    private static readonly Color Heating = Color.FromArgb(0xC2, 0x41, 0x0C);
    private static readonly Color Ready = Color.FromArgb(0x15, 0x80, 0x3D);
    private static readonly Color PumpRunning = Color.FromArgb(0x1D, 0x4E, 0xD8);
    private static readonly Color Inactive = Color.FromArgb(0x4B, 0x55, 0x63);
    private static readonly Color Warning = Color.FromArgb(0xB9, 0x1C, 0x1C);

    public static IconFace For(MachineView view)
    {
        if (view.WaterMissing)
        {
            return new IconFace("H2O", Warning);
        }

        switch (view.Phase)
        {
            case MachinePhase.Heating:
            case MachinePhase.Ready:
                Color color = view.Pump ? PumpRunning : view.Phase == MachinePhase.Ready ? Ready : Heating;
                return new IconFace(view.Packet!.HxTemp.ToString(), color);
            case MachinePhase.MachineOff:
                return new IconFace("AUS", Inactive);
            case MachinePhase.UnknownFormat:
                return new IconFace("?", Inactive);
            default:
                return new IconFace("--", Inactive);
        }
    }
}

internal static class TrayIconRenderer
{
    public static Bitmap RenderBitmap(IconFace face, int size)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        using (GraphicsPath background = RoundedSquare(size, size / 6f))
        using (var brush = new SolidBrush(face.Background))
        {
            graphics.FillPath(brush, background);
        }

        using var text = new GraphicsPath();
        using (var family = new FontFamily("Segoe UI"))
        {
            text.AddString(face.Text, family, (int)FontStyle.Bold, size, Point.Empty, StringFormat.GenericTypographic);
        }

        // three digits don't fit at full height, so the text is squeezed sideways to fill the icon
        RectangleF bounds = text.GetBounds();
        float padding = Math.Max(1f, size / 16f);
        float available = size - 2 * padding;
        float scaleY = Math.Min(available * 0.8f / bounds.Height, available / bounds.Width);
        float scaleX = Math.Min(available / bounds.Width, scaleY);
        if (face.Text.Length >= 3)
        {
            scaleY = available * 0.8f / bounds.Height;
        }

        using var matrix = new Matrix();
        matrix.Translate(size / 2f, size / 2f);
        matrix.Scale(scaleX, scaleY);
        matrix.Translate(-(bounds.Left + bounds.Width / 2), -(bounds.Top + bounds.Height / 2));
        text.Transform(matrix);
        graphics.FillPath(Brushes.White, text);

        return bitmap;
    }

    // The caller owns the returned icon and has to dispose it.
    public static Icon RenderIcon(IconFace face, int size)
    {
        using Bitmap bitmap = RenderBitmap(face, size);
        IntPtr handle = bitmap.GetHicon();
        try
        {
            using Icon borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static GraphicsPath RoundedSquare(int size, float radius)
    {
        float diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(0, 0, diameter, diameter, 180, 90);
        path.AddArc(size - diameter, 0, diameter, diameter, 270, 90);
        path.AddArc(size - diameter, size - diameter, diameter, diameter, 0, 90);
        path.AddArc(0, size - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
