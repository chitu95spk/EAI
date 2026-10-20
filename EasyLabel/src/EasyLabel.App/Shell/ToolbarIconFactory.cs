using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace EasyLabel.App.Shell
{
    internal enum ToolbarIcon
    {
        OpenFolder,
        PreviousImage,
        NextImage,
        Save,
        Undo,
        Redo,
        Select,
        Rectangle,
        OrientedRectangle,
        Delete,
        ZoomOut,
        Fit,
        ZoomIn
    }

    internal sealed class ToolbarIconFactory : IDisposable
    {
        private const int IconSize = 20;
        private readonly Dictionary<ToolbarIcon, Bitmap> _icons = new Dictionary<ToolbarIcon, Bitmap>();
        private bool _disposed;

        public Image Get(ToolbarIcon icon)
        {
            ThrowIfDisposed();
            Bitmap image;
            if (!_icons.TryGetValue(icon, out image))
            {
                image = Create(icon);
                _icons.Add(icon, image);
            }

            return image;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            foreach (Bitmap image in _icons.Values)
            {
                image.Dispose();
            }

            _icons.Clear();
            _disposed = true;
        }

        private static Bitmap Create(ToolbarIcon icon)
        {
            Bitmap bitmap = new Bitmap(IconSize, IconSize, PixelFormat.Format32bppPArgb);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (Pen pen = new Pen(Color.FromArgb(48, 73, 94), 1.8f))
            using (Brush accent = new SolidBrush(Color.FromArgb(44, 123, 182)))
            using (Brush fill = new SolidBrush(Color.FromArgb(218, 233, 246)))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                DrawIcon(graphics, pen, accent, fill, icon);
            }

            return bitmap;
        }

        private static void DrawIcon(Graphics graphics, Pen pen, Brush accent, Brush fill, ToolbarIcon icon)
        {
            switch (icon)
            {
                case ToolbarIcon.OpenFolder:
                    graphics.FillRectangle(fill, 2, 6, 16, 10);
                    graphics.DrawRectangle(pen, 2, 6, 16, 10);
                    graphics.FillRectangle(accent, 3, 3, 7, 4);
                    graphics.DrawRectangle(pen, 3, 3, 7, 4);
                    break;
                case ToolbarIcon.PreviousImage:
                    DrawArrow(graphics, accent, false);
                    break;
                case ToolbarIcon.NextImage:
                    DrawArrow(graphics, accent, true);
                    break;
                case ToolbarIcon.Save:
                    graphics.FillRectangle(fill, 3, 2, 14, 16);
                    graphics.DrawRectangle(pen, 3, 2, 14, 16);
                    graphics.FillRectangle(accent, 6, 3, 7, 5);
                    graphics.DrawRectangle(pen, 6, 3, 7, 5);
                    graphics.DrawRectangle(pen, 6, 11, 8, 6);
                    break;
                case ToolbarIcon.Undo:
                    DrawUndoRedo(graphics, pen, accent, false);
                    break;
                case ToolbarIcon.Redo:
                    DrawUndoRedo(graphics, pen, accent, true);
                    break;
                case ToolbarIcon.Select:
                    Point[] cursor = new Point[]
                    {
                        new Point(4, 2), new Point(4, 15), new Point(8, 11),
                        new Point(11, 17), new Point(13, 16), new Point(10, 10), new Point(16, 10)
                    };
                    graphics.FillPolygon(fill, cursor);
                    graphics.DrawPolygon(pen, cursor);
                    break;
                case ToolbarIcon.Rectangle:
                    graphics.DrawRectangle(pen, 3, 4, 14, 12);
                    graphics.FillRectangle(accent, 3, 4, 3, 3);
                    graphics.FillRectangle(accent, 14, 13, 3, 3);
                    break;
                case ToolbarIcon.OrientedRectangle:
                    DrawOrientedRectangle(graphics, pen, accent);
                    break;
                case ToolbarIcon.Delete:
                    graphics.DrawLine(pen, 5, 6, 15, 6);
                    graphics.DrawLine(pen, 8, 3, 12, 3);
                    graphics.DrawRectangle(pen, 6, 7, 8, 10);
                    graphics.DrawLine(pen, 9, 9, 9, 15);
                    graphics.DrawLine(pen, 12, 9, 12, 15);
                    break;
                case ToolbarIcon.ZoomOut:
                    DrawZoom(graphics, pen, false);
                    break;
                case ToolbarIcon.Fit:
                    DrawFit(graphics, pen);
                    break;
                case ToolbarIcon.ZoomIn:
                    DrawZoom(graphics, pen, true);
                    break;
            }
        }

        private static void DrawArrow(Graphics graphics, Brush accent, bool forward)
        {
            Point[] points = forward
                ? new Point[] { new Point(5, 3), new Point(15, 10), new Point(5, 17) }
                : new Point[] { new Point(15, 3), new Point(5, 10), new Point(15, 17) };
            graphics.FillPolygon(accent, points);
        }

        private static void DrawUndoRedo(Graphics graphics, Pen pen, Brush accent, bool redo)
        {
            if (redo)
            {
                graphics.DrawArc(pen, 4, 4, 12, 12, 215, 210);
                graphics.FillPolygon(accent, new Point[] { new Point(15, 3), new Point(19, 7), new Point(14, 8) });
            }
            else
            {
                graphics.DrawArc(pen, 4, 4, 12, 12, 115, 210);
                graphics.FillPolygon(accent, new Point[] { new Point(5, 3), new Point(1, 7), new Point(6, 8) });
            }
        }

        private static void DrawOrientedRectangle(Graphics graphics, Pen pen, Brush accent)
        {
            Point[] points = new Point[]
            {
                new Point(4, 7), new Point(14, 3), new Point(17, 12), new Point(7, 16)
            };
            graphics.DrawPolygon(pen, points);
            graphics.FillEllipse(accent, 2, 5, 4, 4);
            graphics.FillEllipse(accent, 15, 10, 4, 4);
        }

        private static void DrawZoom(Graphics graphics, Pen pen, bool zoomIn)
        {
            graphics.DrawEllipse(pen, 3, 3, 10, 10);
            graphics.DrawLine(pen, 11, 11, 17, 17);
            graphics.DrawLine(pen, 5, 8, 11, 8);
            if (zoomIn)
            {
                graphics.DrawLine(pen, 8, 5, 8, 11);
            }
        }

        private static void DrawFit(Graphics graphics, Pen pen)
        {
            graphics.DrawLine(pen, 3, 8, 3, 3);
            graphics.DrawLine(pen, 3, 3, 8, 3);
            graphics.DrawLine(pen, 12, 3, 17, 3);
            graphics.DrawLine(pen, 17, 3, 17, 8);
            graphics.DrawLine(pen, 3, 12, 3, 17);
            graphics.DrawLine(pen, 3, 17, 8, 17);
            graphics.DrawLine(pen, 12, 17, 17, 17);
            graphics.DrawLine(pen, 17, 17, 17, 12);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ToolbarIconFactory));
            }
        }
    }
}
