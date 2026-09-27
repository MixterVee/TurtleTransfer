using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Reflection;

namespace CampTransfer;

internal static class TransferAnimationIntegration
{
    public static void Attach(MainForm form)
    {
        _ = new Controller(form);
    }

    private sealed class Controller
    {
        private readonly MainForm _form;
        private readonly DataGridView _grid;
        private readonly BindingList<TransferItem> _queue;
        private readonly AppSettings _settings;
        private readonly CheckBox _toggle;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly Random _random = new(45827);
        private readonly List<BubbleSeed> _bubbles = [];

        private DateTime _lastTickUtc = DateTime.UtcNow;
        private DateTime _completionUntilUtc = DateTime.MinValue;
        private double _swimX = -260;
        private bool _wasTransferActive;

        public Controller(MainForm form)
        {
            _form = form;

            _grid = FindControls<DataGridView>(form).FirstOrDefault()
                ?? throw new InvalidOperationException("Transfer queue grid was not found.");

            if (_grid.DataSource is not BindingSource bindingSource ||
                bindingSource.DataSource is not BindingList<TransferItem> queue)
            {
                throw new InvalidOperationException("Transfer queue data source was not found.");
            }

            _queue = queue;
            _settings = GetSettings(form) ?? AppSettings.Load();

            _toggle = new CheckBox
            {
                Text = "Animation",
                AutoSize = true,
                Checked = _settings.TransferAnimationEnabled,
                Margin = new Padding(10, 7, 2, 0)
            };

            var toolbar = FindControls<FlowLayoutPanel>(form)
                .FirstOrDefault(p => p.Controls.OfType<Button>().Any(b => b.Text == "Start"));
            toolbar?.Controls.Add(_toggle);

            _toggle.CheckedChanged += (_, _) =>
            {
                _settings.TransferAnimationEnabled = _toggle.Checked;
                try { _settings.Save(); } catch { }
                _grid.Invalidate();
            };

            EnableDoubleBuffering(_grid);
            CreateBubbleSeeds();

            _grid.Paint += GridOnPaint;

            _timer = new System.Windows.Forms.Timer { Interval = 40 };
            _timer.Tick += OnTick;
            _timer.Start();

            _form.FormClosed += (_, _) =>
            {
                _timer.Stop();
                _timer.Dispose();
                _grid.Paint -= GridOnPaint;
            };
        }

        private void OnTick(object? sender, EventArgs e)
        {
            var now = DateTime.UtcNow;
            var dt = Math.Clamp((now - _lastTickUtc).TotalSeconds, 0, 0.2);
            _lastTickUtc = now;

            var state = GetState(now);
            var active = state is AnimationState.Transferring or AnimationState.Paused;

            if (_wasTransferActive && !active && _queue.Count > 0 && _queue.All(i => i.Completed))
                _completionUntilUtc = now.AddSeconds(3.0);

            _wasTransferActive = active;

            if (!_toggle.Checked)
                return;

            if (state == AnimationState.Transferring)
            {
                _swimX += 30 * dt;
                if (_swimX > _grid.ClientSize.Width + 280)
                    _swimX = -280;
            }

            var area = GetAnimationArea();
            if (area.Height > 20)
                _grid.Invalidate(area);
        }

        private AnimationState GetState(DateTime now)
        {
            if (now < _completionUntilUtc)
                return AnimationState.Completing;

            var active = _queue.FirstOrDefault(i =>
                i.Status is "Transferring" or "Resuming" or "Paused" ||
                i.Status.StartsWith("Retrying", StringComparison.Ordinal));

            if (active is not null)
            {
                return active.Status == "Paused" ||
                       active.Status.StartsWith("Retrying", StringComparison.Ordinal)
                    ? AnimationState.Paused
                    : AnimationState.Transferring;
            }

            return _queue.Any(i => !i.Completed)
                ? AnimationState.Waiting
                : AnimationState.Idle;
        }

        private Rectangle GetAnimationArea()
        {
            var top = _grid.ColumnHeadersVisible ? _grid.ColumnHeadersHeight : 0;

            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (!row.Visible) continue;

                var rect = _grid.GetRowDisplayRectangle(row.Index, cutOverflow: true);
                if (rect.Height <= 0) continue;
                top = Math.Max(top, rect.Bottom);
            }

            top = Math.Min(_grid.ClientSize.Height, top + 2);
            return new Rectangle(
                1,
                top,
                Math.Max(0, _grid.ClientSize.Width - 2),
                Math.Max(0, _grid.ClientSize.Height - top - 1));
        }

        private void GridOnPaint(object? sender, PaintEventArgs e)
        {
            if (!_toggle.Checked)
                return;

            var area = GetAnimationArea();
            if (area.Width < 220 || area.Height < 120)
                return;

            var now = DateTime.UtcNow;
            var state = GetState(now);

            if (state == AnimationState.Idle)
                return;

            var g = e.Graphics;
            var saved = g.Save();
            try
            {
                g.SetClip(area);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;

                var tone = ContrastTone(_grid.BackgroundColor);
                var seconds = Environment.TickCount64 / 1000.0;

                DrawOceanHints(g, area, tone, state, seconds);
                DrawBubbleField(g, area, tone, seconds, state);

                var centerY = area.Top + area.Height * 0.50;
                var scale = Math.Clamp(area.Height / 190f, 1.35f, 2.15f);

                if (state == AnimationState.Transferring)
                {
                    var x = area.Left + (float)_swimX;
                    var y = (float)(centerY + Math.Sin(seconds * 1.15) * 8);
                    DrawTurtle(g, x, y, scale, tone, 100, seconds, true);
                    DrawTurtleBubbleTrail(g, area, x, y, scale, tone, seconds, 74);
                }
                else if (state == AnimationState.Paused)
                {
                    var x = (float)Math.Clamp(area.Left + _swimX, area.Left + 135, area.Right - 300);
                    var y = (float)(centerY + Math.Sin(seconds * 1.55) * 4);
                    DrawTurtle(g, x, y, scale, tone, 76, seconds * 0.35, false);
                    DrawTurtleBubbleTrail(g, area, x, y, scale, tone, seconds * 0.35, 44);
                }
                else if (state == AnimationState.Completing)
                {
                    var remaining = Math.Clamp((_completionUntilUtc - now).TotalSeconds / 3.0, 0, 1);
                    var alpha = (int)(94 * remaining);
                    var x = area.Left + area.Width * 0.61f;
                    var y = (float)(centerY + Math.Sin(seconds * 1.2) * 5);
                    DrawTurtle(g, x, y, scale, tone, alpha, seconds, true);
                    DrawCelebrationBubbles(g, area, tone, seconds, (int)(100 * remaining));
                }
                else if (state == AnimationState.Waiting)
                {
                    DrawTurtle(
                        g,
                        area.Left + area.Width * 0.67f,
                        (float)centerY,
                        scale * 0.90f,
                        tone,
                        34,
                        seconds * 0.2,
                        false);
                }
            }
            finally
            {
                g.Restore(saved);
            }
        }

        private static void DrawOceanHints(
            Graphics g,
            Rectangle area,
            int tone,
            AnimationState state,
            double seconds)
        {
            if (state is AnimationState.Idle or AnimationState.Waiting)
                return;

            using var pen = new Pen(Color.FromArgb(24, tone, tone, tone), 1.2f);

            for (var line = 0; line < 4; line++)
            {
                var yBase = area.Bottom - 24 - line * 24;
                using var path = new GraphicsPath();
                var started = false;

                for (var x = area.Left - 30; x <= area.Right + 30; x += 16)
                {
                    var y = yBase + Math.Sin((x * 0.021) + seconds * 0.52 + line * 0.8) * 4.5;
                    if (!started)
                    {
                        path.StartFigure();
                        path.AddLine(x, (float)y, x + 0.1f, (float)y);
                        started = true;
                    }
                    else
                    {
                        path.AddLine(path.GetLastPoint(), new PointF(x, (float)y));
                    }
                }

                g.DrawPath(pen, path);
            }
        }

        private void DrawBubbleField(
            Graphics g,
            Rectangle area,
            int tone,
            double seconds,
            AnimationState state)
        {
            var count = state switch
            {
                AnimationState.Transferring => 34,
                AnimationState.Paused => 18,
                AnimationState.Completing => 44,
                AnimationState.Waiting => 10,
                _ => 0
            };

            var alpha = state switch
            {
                AnimationState.Transferring => 55,
                AnimationState.Paused => 34,
                AnimationState.Completing => 66,
                AnimationState.Waiting => 20,
                _ => 0
            };

            count = Math.Min(count, _bubbles.Count);

            for (var i = 0; i < count; i++)
            {
                var seed = _bubbles[i];
                var speedScale = state == AnimationState.Paused ? 0.32 : 1.0;
                var cycle = (seconds * seed.Speed * speedScale + seed.Phase) % 1.0;
                if (cycle < 0) cycle += 1.0;

                var drift = Math.Sin(seconds * 0.70 + seed.Phase * 8 + i) * seed.Drift;
                var x = area.Left + (float)(seed.XRatio * area.Width + drift);
                var y = area.Bottom - 8 - (float)(cycle * Math.Max(30, area.Height - 18));
                DrawBubble(g, x, y, seed.Radius, tone, alpha, seed.Highlight);
            }
        }

        private static void DrawTurtleBubbleTrail(
            Graphics g,
            Rectangle area,
            float turtleX,
            float turtleY,
            float scale,
            int tone,
            double seconds,
            int alpha)
        {
            for (var i = 0; i < 14; i++)
            {
                var phase = (seconds * 0.22 + i * 0.071) % 1.0;
                var spread = (float)(Math.Sin(i * 2.1 + seconds * 0.7) * 16);
                var x = turtleX - scale * (72 + i * 8) + spread;
                var y = turtleY + scale * 6 - (float)(phase * 95 * scale) + (i % 3 - 1) * 7;
                if (x < area.Left - 20 || x > area.Right + 20 || y < area.Top - 20 || y > area.Bottom + 20)
                    continue;

                var r = scale * (2.4f + (i % 4) * 1.1f);
                DrawBubble(g, x, y, r, tone, alpha - i * 2, i % 2 == 0);
            }
        }

        private static void DrawCelebrationBubbles(
            Graphics g,
            Rectangle area,
            int tone,
            double seconds,
            int alpha)
        {
            for (var i = 0; i < 36; i++)
            {
                var phase = (seconds * (0.16 + (i % 5) * 0.018) + i * 0.057) % 1.0;
                var x = area.Left + area.Width * (0.18f + ((i * 37) % 63) / 100f);
                x += (float)Math.Sin(seconds + i) * 12;
                var y = area.Bottom - (float)(phase * area.Height);
                var r = 3f + (i % 6) * 1.3f;
                DrawBubble(g, x, y, r, tone, Math.Max(12, alpha - i), i % 3 != 0);
            }
        }

        private static void DrawBubble(
            Graphics g,
            float x,
            float y,
            float radius,
            int tone,
            int alpha,
            bool highlight)
        {
            alpha = Math.Clamp(alpha, 0, 120);
            if (alpha == 0) return;

            using var pen = new Pen(Color.FromArgb(alpha, tone, tone, tone), Math.Max(1f, radius * 0.16f));
            g.DrawEllipse(pen, x - radius, y - radius, radius * 2, radius * 2);

            if (!highlight || radius < 3f) return;

            using var highlightPen = new Pen(
                Color.FromArgb(Math.Min(110, alpha + 22), tone, tone, tone),
                Math.Max(0.8f, radius * 0.11f));
            var hr = radius * 0.42f;
            g.DrawArc(highlightPen, x - radius * 0.46f, y - radius * 0.48f, hr, hr, 195, 105);
        }

        private static void DrawTurtle(
            Graphics g,
            float centerX,
            float centerY,
            float scale,
            int tone,
            int alpha,
            double seconds,
            bool swimming)
        {
            alpha = Math.Clamp(alpha, 0, 140);
            if (alpha == 0) return;

            var saved = g.Save();
            try
            {
                g.TranslateTransform(centerX, centerY);
                g.ScaleTransform(scale, scale);

                var bodyAlpha = (int)(alpha * 0.78);
                var fillAlpha = (int)(alpha * 0.32);
                var deepAlpha = (int)(alpha * 0.55);

                using var bodyBrush = new SolidBrush(Color.FromArgb(bodyAlpha, tone, tone, tone));
                using var shellBrush = new SolidBrush(Color.FromArgb(fillAlpha, tone, tone, tone));
                using var shellShade = new SolidBrush(Color.FromArgb((int)(alpha * 0.16), tone, tone, tone));
                using var outline = new Pen(Color.FromArgb(alpha, tone, tone, tone), 1.75f);
                using var detail = new Pen(Color.FromArgb(deepAlpha, tone, tone, tone), 1.05f);

                var stroke = swimming ? Math.Sin(seconds * 2.3) : Math.Sin(seconds * 1.2) * 0.15;
                var topAngle = swimming ? (float)(-23 + stroke * 34) : -13f;
                var bottomAngle = swimming ? (float)(23 - stroke * 34) : 13f;
                var rearStroke = swimming ? (float)Math.Sin(seconds * 2.3 + 1.3) : 0f;

                DrawRearFlipper(g, -38, -18, -22 + rearStroke * 8, true, bodyBrush, outline);
                DrawRearFlipper(g, -38, 18, 22 - rearStroke * 8, false, bodyBrush, outline);

                DrawFrontFlipper(g, 25, -19, topAngle, true, bodyBrush, outline, detail);
                DrawFrontFlipper(g, 25, 19, bottomAngle, false, bodyBrush, outline, detail);

                using (var tail = new GraphicsPath())
                {
                    tail.AddPolygon([
                        new PointF(-58, -5),
                        new PointF(-75, 0),
                        new PointF(-58, 6)
                    ]);
                    g.FillPath(bodyBrush, tail);
                    g.DrawPath(detail, tail);
                }

                using var shellPath = new GraphicsPath();
                shellPath.AddBezier(-53, 0, -49, -31, -22, -38, 9, -34);
                shellPath.AddBezier(9, -34, 38, -29, 49, -12, 47, 0);
                shellPath.AddBezier(47, 0, 45, 21, 23, 33, -9, 34);
                shellPath.AddBezier(-9, 34, -40, 32, -54, 12, -53, 0);
                shellPath.CloseFigure();

                g.FillPath(shellBrush, shellPath);
                g.DrawPath(outline, shellPath);

                using (var innerShell = new GraphicsPath())
                {
                    innerShell.AddBezier(-41, 0, -39, -23, -17, -28, 8, -26);
                    innerShell.AddBezier(8, -26, 30, -23, 39, -10, 38, 0);
                    innerShell.AddBezier(38, 0, 36, 15, 19, 25, -7, 26);
                    innerShell.AddBezier(-7, 26, -31, 24, -41, 10, -41, 0);
                    innerShell.CloseFigure();
                    g.FillPath(shellShade, innerShell);
                    g.DrawPath(detail, innerShell);
                }

                // Shell scutes for a more detailed, higher-resolution look.
                DrawScute(g, detail, -18, -15, 19, 15);
                DrawScute(g, detail, 4, -15, 20, 15);
                DrawScute(g, detail, -19, 1, 20, 16);
                DrawScute(g, detail, 5, 1, 20, 16);
                g.DrawArc(detail, -38, -23, 29, 47, 270, 180);
                g.DrawArc(detail, 16, -22, 21, 44, 90, 180);

                // Neck.
                using (var neck = new GraphicsPath())
                {
                    neck.AddBezier(37, -9, 48, -10, 58, -9, 64, -5);
                    neck.AddBezier(64, -5, 66, 3, 58, 9, 39, 9);
                    neck.CloseFigure();
                    g.FillPath(bodyBrush, neck);
                }

                // Head with a slightly tapered snout.
                using (var head = new GraphicsPath())
                {
                    head.AddBezier(54, -13, 69, -16, 83, -8, 85, 0);
                    head.AddBezier(85, 0, 82, 10, 69, 15, 55, 11);
                    head.AddBezier(55, 11, 49, 4, 50, -4, 54, -13);
                    head.CloseFigure();
                    g.FillPath(bodyBrush, head);
                    g.DrawPath(outline, head);
                }

                // Eye and tiny mouth detail.
                using var eyeBrush = new SolidBrush(Color.FromArgb(Math.Min(165, alpha + 30), tone, tone, tone));
                g.FillEllipse(eyeBrush, 75, -5, 3.2f, 3.2f);
                g.DrawArc(detail, 75, 2, 8, 5, 10, 110);

                // A few subtle neck/head contour details.
                g.DrawArc(detail, 58, -10, 15, 17, 105, 90);
                g.DrawLine(detail, 59, 7, 69, 10);
            }
            finally
            {
                g.Restore(saved);
            }
        }

        private static void DrawFrontFlipper(
            Graphics g,
            float anchorX,
            float anchorY,
            float angle,
            bool upper,
            Brush brush,
            Pen outline,
            Pen detail)
        {
            var saved = g.Save();
            try
            {
                g.TranslateTransform(anchorX, anchorY);
                g.RotateTransform(upper ? angle : -angle);

                using var path = new GraphicsPath();
                if (upper)
                {
                    path.AddBezier(0, 0, 10, -14, 24, -31, 42, -40);
                    path.AddBezier(42, -40, 49, -39, 43, -28, 31, -11);
                    path.AddBezier(31, -11, 19, 2, 7, 8, 0, 0);
                }
                else
                {
                    path.AddBezier(0, 0, 10, 14, 24, 31, 42, 40);
                    path.AddBezier(42, 40, 49, 39, 43, 28, 31, 11);
                    path.AddBezier(31, 11, 19, -2, 7, -8, 0, 0);
                }
                path.CloseFigure();

                g.FillPath(brush, path);
                g.DrawPath(outline, path);

                using var crease = new GraphicsPath();
                if (upper)
                    crease.AddBezier(8, -2, 18, -12, 29, -26, 38, -33);
                else
                    crease.AddBezier(8, 2, 18, 12, 29, 26, 38, 33);
                g.DrawPath(detail, crease);
            }
            finally
            {
                g.Restore(saved);
            }
        }

        private static void DrawRearFlipper(
            Graphics g,
            float anchorX,
            float anchorY,
            float angle,
            bool upper,
            Brush brush,
            Pen outline)
        {
            var saved = g.Save();
            try
            {
                g.TranslateTransform(anchorX, anchorY);
                g.RotateTransform(upper ? angle : -angle);

                using var path = new GraphicsPath();
                if (upper)
                {
                    path.AddBezier(0, 0, -9, -10, -24, -21, -35, -18);
                    path.AddBezier(-35, -18, -43, -12, -28, -2, -7, 7);
                    path.AddBezier(-7, 7, -2, 5, 0, 0, 0, 0);
                }
                else
                {
                    path.AddBezier(0, 0, -9, 10, -24, 21, -35, 18);
                    path.AddBezier(-35, 18, -43, 12, -28, 2, -7, -7);
                    path.AddBezier(-7, -7, -2, -5, 0, 0, 0, 0);
                }
                path.CloseFigure();

                g.FillPath(brush, path);
                g.DrawPath(outline, path);
            }
            finally
            {
                g.Restore(saved);
            }
        }

        private static void DrawScute(Graphics g, Pen pen, float x, float y, float w, float h)
        {
            using var path = new GraphicsPath();
            path.AddPolygon([
                new PointF(x + w * 0.50f, y),
                new PointF(x + w, y + h * 0.45f),
                new PointF(x + w * 0.72f, y + h),
                new PointF(x + w * 0.28f, y + h),
                new PointF(x, y + h * 0.45f)
            ]);
            path.CloseFigure();
            g.DrawPath(pen, path);
        }

        private void CreateBubbleSeeds()
        {
            for (var i = 0; i < 56; i++)
            {
                _bubbles.Add(new BubbleSeed(
                    _random.NextDouble(),
                    _random.NextDouble(),
                    0.055 + _random.NextDouble() * 0.13,
                    2.3f + (float)_random.NextDouble() * 6.8f,
                    3.0f + (float)_random.NextDouble() * 13.0f,
                    _random.NextDouble() > 0.22));
            }
        }

        private static int ContrastTone(Color background)
        {
            var brightness = background.R * 0.299 + background.G * 0.587 + background.B * 0.114;
            return brightness < 135 ? 218 : 72;
        }

        private static AppSettings? GetSettings(MainForm form)
        {
            try
            {
                return typeof(MainForm)
                    .GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.GetValue(form) as AppSettings;
            }
            catch
            {
                return null;
            }
        }

        private static void EnableDoubleBuffering(DataGridView grid)
        {
            try
            {
                typeof(DataGridView)
                    .GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.SetValue(grid, true);
            }
            catch
            {
                // Animation still works without the optimization.
            }
        }

        private static IEnumerable<T> FindControls<T>(Control root) where T : Control
        {
            foreach (Control child in root.Controls)
            {
                if (child is T match) yield return match;
                foreach (var nested in FindControls<T>(child)) yield return nested;
            }
        }

        private sealed record BubbleSeed(
            double XRatio,
            double Phase,
            double Speed,
            float Radius,
            float Drift,
            bool Highlight);

        private enum AnimationState
        {
            Idle,
            Waiting,
            Transferring,
            Paused,
            Completing
        }
    }
}
