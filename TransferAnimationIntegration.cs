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
        private double _swimX = -120;
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

            _timer = new System.Windows.Forms.Timer { Interval = 50 };
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
                _completionUntilUtc = now.AddSeconds(2.6);

            _wasTransferActive = active;

            if (!_toggle.Checked)
                return;

            if (state == AnimationState.Transferring)
            {
                _swimX += 34 * dt;
                if (_swimX > _grid.ClientSize.Width + 150)
                    _swimX = -150;
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
            if (area.Width < 180 || area.Height < 95)
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

                var tone = ContrastTone(_grid.BackgroundColor);
                DrawOceanHints(g, area, tone, state, now);

                var seconds = Environment.TickCount64 / 1000.0;
                var centerY = area.Top + area.Height * 0.52;

                if (state == AnimationState.Transferring)
                {
                    var x = area.Left + (float)_swimX;
                    var y = (float)(centerY + Math.Sin(seconds * 1.35) * 7);
                    DrawTurtle(g, x, y, 1.0f, tone, 74);
                    DrawBubbles(g, area, tone, seconds, 12, 46);
                }
                else if (state == AnimationState.Paused)
                {
                    var x = (float)Math.Clamp(area.Left + _swimX, area.Left + 80, area.Right - 170);
                    var y = (float)(centerY + Math.Sin(seconds * 1.8) * 4);
                    DrawTurtle(g, x, y, 1.0f, tone, 58);
                    DrawBubbles(g, area, tone, seconds * 0.35, 5, 30);
                }
                else if (state == AnimationState.Completing)
                {
                    var remaining = Math.Clamp((_completionUntilUtc - now).TotalSeconds / 2.6, 0, 1);
                    var alpha = (int)(68 * remaining);
                    DrawTurtle(
                        g,
                        area.Left + area.Width * 0.62f,
                        (float)(centerY + Math.Sin(seconds * 1.4) * 5),
                        1.0f,
                        tone,
                        alpha);
                    DrawBubbles(g, area, tone, seconds * 1.8, 26, (int)(70 * remaining));
                }
                else if (state == AnimationState.Waiting)
                {
                    // A queued-but-not-running transfer gets only a very faint stationary turtle.
                    DrawTurtle(
                        g,
                        area.Left + area.Width * 0.72f,
                        (float)centerY,
                        0.92f,
                        tone,
                        25);
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
            DateTime now)
        {
            if (state is AnimationState.Idle or AnimationState.Waiting)
                return;

            var seconds = now.TimeOfDay.TotalSeconds;
            using var pen = new Pen(Color.FromArgb(20, tone, tone, tone), 1.2f);

            for (var line = 0; line < 3; line++)
            {
                var yBase = area.Bottom - 26 - line * 22;
                using var path = new GraphicsPath();
                var first = true;

                for (var x = area.Left - 20; x <= area.Right + 20; x += 18)
                {
                    var y = yBase + Math.Sin((x * 0.025) + seconds * 0.6 + line) * 4;
                    if (first)
                    {
                        path.StartFigure();
                        path.AddLine(x, (float)y, x + 0.1f, (float)y);
                        first = false;
                    }
                    else
                    {
                        path.AddLine(path.GetLastPoint(), new PointF(x, (float)y));
                    }
                }

                g.DrawPath(pen, path);
            }
        }

        private void DrawBubbles(
            Graphics g,
            Rectangle area,
            int tone,
            double time,
            int count,
            int alpha)
        {
            count = Math.Min(count, _bubbles.Count);
            using var pen = new Pen(Color.FromArgb(Math.Clamp(alpha, 0, 110), tone, tone, tone), 1.15f);

            for (var i = 0; i < count; i++)
            {
                var seed = _bubbles[i];
                var cycle = (time * seed.Speed + seed.Phase) % 1.0;
                if (cycle < 0) cycle += 1.0;

                var x = area.Left + (float)(seed.XRatio * area.Width + Math.Sin(time * 0.8 + i) * 6);
                var y = area.Bottom - 10 - (float)(cycle * Math.Max(20, area.Height - 20));
                var r = seed.Radius;

                g.DrawEllipse(pen, x - r, y - r, r * 2, r * 2);
            }
        }

        private static void DrawTurtle(
            Graphics g,
            float centerX,
            float centerY,
            float scale,
            int tone,
            int alpha)
        {
            alpha = Math.Clamp(alpha, 0, 120);
            if (alpha == 0) return;

            var saved = g.Save();
            try
            {
                g.TranslateTransform(centerX, centerY);
                g.ScaleTransform(scale, scale);

                using var shellBrush = new SolidBrush(Color.FromArgb(alpha / 2, tone, tone, tone));
                using var bodyBrush = new SolidBrush(Color.FromArgb((int)(alpha * 0.72), tone, tone, tone));
                using var outline = new Pen(Color.FromArgb(alpha, tone, tone, tone), 1.6f);
                using var detail = new Pen(Color.FromArgb(alpha / 2, tone, tone, tone), 1.05f);

                // Rear flippers.
                using (var rearTop = new GraphicsPath())
                {
                    rearTop.AddBezier(-43, -20, -65, -42, -77, -36, -55, -9);
                    rearTop.AddBezier(-55, -9, -45, -7, -40, -12, -43, -20);
                    g.FillPath(bodyBrush, rearTop);
                }

                using (var rearBottom = new GraphicsPath())
                {
                    rearBottom.AddBezier(-42, 20, -67, 42, -78, 34, -54, 8);
                    rearBottom.AddBezier(-54, 8, -46, 7, -40, 12, -42, 20);
                    g.FillPath(bodyBrush, rearBottom);
                }

                // Front flippers.
                using (var frontTop = new GraphicsPath())
                {
                    frontTop.AddBezier(24, -20, 43, -48, 63, -50, 48, -13);
                    frontTop.AddBezier(48, -13, 39, -7, 31, -9, 24, -20);
                    g.FillPath(bodyBrush, frontTop);
                }

                using (var frontBottom = new GraphicsPath())
                {
                    frontBottom.AddBezier(25, 20, 46, 47, 64, 46, 48, 12);
                    frontBottom.AddBezier(48, 12, 40, 7, 32, 8, 25, 20);
                    g.FillPath(bodyBrush, frontBottom);
                }

                // Tail.
                using (var tail = new GraphicsPath())
                {
                    tail.AddPolygon([
                        new PointF(-53, -5),
                        new PointF(-69, 0),
                        new PointF(-53, 6)
                    ]);
                    g.FillPath(bodyBrush, tail);
                }

                // Shell.
                var shell = new RectangleF(-50, -29, 94, 58);
                g.FillEllipse(shellBrush, shell);
                g.DrawEllipse(outline, shell);

                // Subtle shell pattern.
                g.DrawArc(detail, -36, -21, 66, 42, 205, 130);
                g.DrawArc(detail, -33, -18, 62, 36, 25, 130);
                g.DrawLine(detail, -4, -26, -4, 26);
                g.DrawArc(detail, -20, -25, 34, 50, 82, 196);

                // Neck and head.
                g.FillEllipse(bodyBrush, 36, -12, 24, 24);
                g.FillEllipse(bodyBrush, 51, -14, 28, 27);
                g.DrawEllipse(outline, 51, -14, 28, 27);

                // Eye.
                using var eye = new SolidBrush(Color.FromArgb(Math.Min(150, alpha + 30), tone, tone, tone));
                g.FillEllipse(eye, 69, -6, 2.8f, 2.8f);
            }
            finally
            {
                g.Restore(saved);
            }
        }

        private void CreateBubbleSeeds()
        {
            for (var i = 0; i < 30; i++)
            {
                _bubbles.Add(new BubbleSeed(
                    _random.NextDouble(),
                    _random.NextDouble(),
                    0.07 + _random.NextDouble() * 0.12,
                    2.0f + (float)_random.NextDouble() * 4.5f));
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

        private sealed record BubbleSeed(double XRatio, double Phase, double Speed, float Radius);

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
