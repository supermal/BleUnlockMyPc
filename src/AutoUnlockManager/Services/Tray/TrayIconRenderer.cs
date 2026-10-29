using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using AutoUnlockManager.Core.Telemetry;

namespace AutoUnlockManager.Services.Tray;

/// <summary>
/// Icono de la bandeja: candado + ondas de señal (trazo) y un distintivo abajo a la derecha cuya
/// forma y color indican el estado (UiDesign.md 2.5: el estado no depende solo del color).
/// Se dibuja en un sistema de 16 unidades y se escala al tamaño pedido; color del trazo según el
/// tema de la barra de tareas.
/// </summary>
internal static class TrayIconRenderer
{
    private static readonly Dictionary<(TrayState, bool, int), Icon> Cache = [];

    public static Icon Render(TrayState state, bool lightTaskbar, int size)
    {
        if (Cache.TryGetValue((state, lightTaskbar, size), out var cached))
        {
            return cached;
        }

        var icon = Draw(state, lightTaskbar, size);
        Cache[(state, lightTaskbar, size)] = icon;
        return icon;
    }

    private static Icon Draw(TrayState state, bool light, int size)
    {
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            g.ScaleTransform(size / 16f, size / 16f);

            var ink = light ? Color.FromArgb(0x1A, 0x1A, 0x1A) : Color.White;
            using (var pen = new Pen(ink, 1.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                // Cuerpo del candado y arco
                using var body = RoundedRect(new RectangleF(1.5f, 7f, 8f, 6.5f), 1.5f);
                g.DrawPath(pen, body);
                g.DrawLine(pen, 3.5f, 7f, 3.5f, 5f);
                g.DrawArc(pen, 3.5f, 3f, 4f, 4f, 180, 180);
                g.DrawLine(pen, 7.5f, 5f, 7.5f, 7f);

                // Ondas de señal
                g.DrawArc(pen, 9.6f - 4f, 5.7f - 4f, 8f, 8f, -77, 64);
                g.DrawArc(pen, 9.8f - 1.7f, 5.7f - 1.7f, 3.4f, 3.4f, -70, 50);
            }

            // Hueco transparente bajo el distintivo para que se lea sobre cualquier fondo
            g.CompositingMode = CompositingMode.SourceCopy;
            using (var clear = new SolidBrush(Color.Transparent))
            {
                g.FillEllipse(clear, 12.5f - 4f, 12.5f - 4f, 8f, 8f);
            }

            g.CompositingMode = CompositingMode.SourceOver;
            DrawBadge(g, state, light);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var fromHandle = Icon.FromHandle(handle);
            return (Icon)fromHandle.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static void DrawBadge(Graphics g, TrayState state, bool light)
    {
        var color = ColorOf(state, light);
        using var brush = new SolidBrush(color);

        switch (state)
        {
            case TrayState.Running:
                g.FillEllipse(brush, 12.5f - 3f, 12.5f - 3f, 6f, 6f);
                break;

            case TrayState.Leaving or TrayState.Suspended:
                g.FillPolygon(brush, [new PointF(12.5f, 9.2f), new PointF(15.7f, 14.8f), new PointF(9.3f, 14.8f)]);
                break;

            case TrayState.Stopped:
                g.FillEllipse(brush, 12.5f - 3.2f, 12.5f - 3.2f, 6.4f, 6.4f);
                using (var cross = new Pen(light ? Color.White : Color.FromArgb(0x20, 0x20, 0x20), 1.1f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawLine(cross, 11.2f, 11.2f, 13.8f, 13.8f);
                    g.DrawLine(cross, 13.8f, 11.2f, 11.2f, 13.8f);
                }

                break;

            default:
                using (var ring = new Pen(color, 1.2f))
                {
                    g.DrawEllipse(ring, 12.5f - 2.5f, 12.5f - 2.5f, 5f, 5f);
                }

                break;
        }
    }

    private static Color ColorOf(TrayState state, bool light) => state switch
    {
        TrayState.Running => light ? Color.FromArgb(0x0F, 0x7B, 0x0F) : Color.FromArgb(0x6C, 0xCB, 0x5F),
        TrayState.Leaving => light ? Color.FromArgb(0x9D, 0x5D, 0x00) : Color.FromArgb(0xFC, 0xE1, 0x00),
        TrayState.Suspended or TrayState.Stopped => light ? Color.FromArgb(0xC4, 0x2B, 0x1C) : Color.FromArgb(0xFF, 0x99, 0xA4),
        _ => light ? Color.FromArgb(0x7A, 0x7A, 0x7A) : Color.FromArgb(0x9E, 0x9E, 0xA6),
    };

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
