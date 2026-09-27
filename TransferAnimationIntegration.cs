using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
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
        private readonly List<BubbleSeed> _backBubbles = [];
        private readonly List<BubbleSeed> _frontBubbles = [];
        private readonly List<ParticleSeed> _particles = [];

        private Bitmap? _background;
        private Bitmap? _turtle;
        private Bitmap? _foreground;

        private DateTime _lastTickUtc = DateTime.UtcNow;
        private DateTime _completionUntilUtc = DateTime.MinValue;
        private bool _wasTransferActive;
        private double _travel = -0.18;

        public Controller(MainForm form)
        {
            _form = form;
            _grid = FindControls<DataGridView>(form).FirstOrDefault()
                ?? throw new InvalidOperationException("Transfer queue grid was not found.");

            if (_grid.DataSource is not BindingSource source ||
                source.DataSource is not BindingList<TransferItem> queue)
                throw new InvalidOperationException("Transfer queue data source was not found.");

            _queue = queue;
            _settings = GetSettings(form) ?? AppSettings.Load();

            _toggle = new CheckBox
            {
                Text = "Animation",
                AutoSize = true,
                Checked = _settings.TransferAnimationEnabled,
                Margin = new Padding(10, 7, 2, 0)
            };

            FindControls<FlowLayoutPanel>(form)
                .FirstOrDefault(p => p.Controls.OfType<Button>().Any(b => b.Text == "Start"))
                ?.Controls.Add(_toggle);

            _toggle.CheckedChanged += (_, _) =>
            {
                _settings.TransferAnimationEnabled = _toggle.Checked;
                try { _settings.Save(); } catch { }
                _grid.Invalidate();
            };

            EnableDoubleBuffering(_grid);
            SeedScene();
            LoadArtwork();

            _grid.Paint += GridOnPaint;
            _timer = new System.Windows.Forms.Timer { Interval = 40 };
            _timer.Tick += OnTick;
            _timer.Start();

            _form.FormClosed += (_, _) =>
            {
                _timer.Stop();
                _timer.Dispose();
                _grid.Paint -= GridOnPaint;
                _background?.Dispose();
                _turtle?.Dispose();
                _foreground?.Dispose();
            };
        }

        private void LoadArtwork()
        {
            _background = DecodeArtwork(TransferAnimationOceanBackground.Data);
            _turtle = DecodeArtwork(TransferAnimationTurtleSprite.Data);
            _foreground = null;
        }

        private static Bitmap? DecodeArtwork(string base64)
        {
            try
            {
                var bytes = Convert.FromBase64String(base64);
                using var stream = new MemoryStream(bytes, writable: false);
                using var source = Image.FromStream(stream);
                return new Bitmap(source);
            }
            catch
            {
                return null;
            }
        }

        private void OnTick(object? sender, EventArgs e)
        {
            var now = DateTime.UtcNow;
            var dt = Math.Clamp((now - _lastTickUtc).TotalSeconds, 0, 0.20);
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
                _travel += dt * 0.055;
                if (_travel > 1.18)
                    _travel = -0.18;
            }

            var area = GetAnimationArea();
            if (area.Width > 0 && area.Height > 0)
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
                if (rect.Height > 0)
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
            if (area.Width < 260 || area.Height < 130)
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

                var seconds = Environment.TickCount64 / 1000.0;
                var ink = ContrastInk(_grid.BackgroundColor);

                if (_background is not null)
                    DrawImageCover(g, _background, area,
                        (float)Math.Sin(seconds * 0.10) * 5f, 0, 0.78f);
                else
                    DrawFallbackWater(g, area, ink);

                DrawLightRays(g, area, ink, seconds);
                DrawParticles(g, area, ink, seconds, state, front: false);
                DrawBubbles(g, area, ink, seconds, state, _backBubbles);

                DrawTurtle(g, area, seconds, state);

                DrawBubbles(g, area, ink, seconds, state, _frontBubbles);
                DrawParticles(g, area, ink, seconds, state, front: true);

                if (_foreground is not null)
                    DrawImageCover(g, _foreground, area,
                        (float)Math.Sin(seconds * 0.15 + 1.1) * -4f, 0, 0.88f);

                if (state == AnimationState.Completing)
                {
                    var remaining = Math.Clamp((_completionUntilUtc - now).TotalSeconds / 3.0, 0, 1);
                    DrawCompletionBurst(g, area, ink, seconds, (int)(110 * remaining));
                }

                DrawVignette(g, area, ink);
            }
            finally
            {
                g.Restore(saved);
            }
        }

        private void DrawTurtle(Graphics g, Rectangle area, double seconds, AnimationState state)
        {
            if (_turtle is null)
                return;

            var targetHeight = Math.Clamp(area.Height * 0.56f, 145f, 315f);
            var scale = targetHeight / _turtle.Height;
            var halfWidth = _turtle.Width * scale / 2f;
            var centerY = area.Top + area.Height * 0.43f;

            float x;
            float y;
            float angle;
            float alpha;

            switch (state)
            {
                case AnimationState.Transferring:
                    x = Lerp(area.Left - halfWidth - 25, area.Right + halfWidth + 25, (float)_travel);
                    y = centerY + (float)Math.Sin(seconds * 0.95) * 8f;
                    angle = (float)Math.Sin(seconds * 1.30) * 1.7f;
                    alpha = 1.0f;
                    break;

                case AnimationState.Paused:
                    x = Math.Clamp(
                        Lerp(area.Left + halfWidth, area.Right - halfWidth, (float)Math.Clamp(_travel, 0, 1)),
                        area.Left + halfWidth,
                        area.Right - halfWidth);
                    y = centerY + (float)Math.Sin(seconds * 1.25) * 4f;
                    angle = (float)Math.Sin(seconds * 0.80) * 0.8f;
                    alpha = 0.94f;
                    break;

                case AnimationState.Completing:
                    x = area.Left + area.Width * 0.60f;
                    y = centerY + (float)Math.Sin(seconds * 0.95) * 5f;
                    angle = (float)Math.Sin(seconds * 1.10) * 1.3f;
                    alpha = 1.0f;
                    break;

                case AnimationState.Waiting:
                    x = area.Left + area.Width * 0.68f;
                    y = centerY;
                    angle = 0;
                    alpha = 0.45f;
                    scale *= 0.92f;
                    break;

                default:
                    return;
            }

            // A tiny breathing/paddling illusion keeps the photographic sprite from feeling static.
            var swimPulse = state == AnimationState.Transferring
                ? 1f + (float)Math.Sin(seconds * 2.25) * 0.012f
                : 1f;

            DrawCenteredImage(g, _turtle, x, y, scale, scale * swimPulse, angle, alpha);
        }

        private static void DrawFallbackWater(Graphics g, Rectangle area, Color ink)
        {
            using var brush = new LinearGradientBrush(
                new Point(area.Left, area.Top),
                new Point(area.Left, area.Bottom),
                Color.FromArgb(20, ink),
                Color.FromArgb(48, ink));
            g.FillRectangle(brush, area);
        }

        private static void DrawLightRays(Graphics g, Rectangle area, Color ink, double seconds)
        {
            for (var i = 0; i < 4; i++)
            {
                var sway = (float)Math.Sin(seconds * 0.23 + i * 0.85) * 15f;
                var x = area.Left + (i + 1) * area.Width / 5f + sway;

                using var path = new GraphicsPath();
                path.AddPolygon([
                    new PointF(x - 11, area.Top),
                    new PointF(x + 11, area.Top),
                    new PointF(x + 55 + i * 10, area.Bottom),
                    new PointF(x - 55 - i * 10, area.Bottom)
                ]);

                using var brush = new SolidBrush(Color.FromArgb(9 + i * 2, ink));
                g.FillPath(brush, path);
            }
        }

        private void DrawParticles(
            Graphics g, Rectangle area, Color ink, double seconds,
            AnimationState state, bool front)
        {
            var alpha = state switch
            {
                AnimationState.Transferring => front ? 25 : 16,
                AnimationState.Paused => front ? 15 : 10,
                AnimationState.Completing => front ? 28 : 18,
                AnimationState.Waiting => front ? 10 : 7,
                _ => 0
            };

            if (alpha == 0) return;
            using var brush = new SolidBrush(Color.FromArgb(alpha, ink));

            foreach (var p in _particles)
            {
                if (p.Front != front) continue;

                var cycle = (seconds * p.Speed + p.Phase) % 1.0;
                if (cycle < 0) cycle += 1.0;

                var x = area.Left + (float)(p.XRatio * area.Width)
                    + (float)Math.Sin(seconds * 0.42 + p.Phase * 8) * p.Drift;
                var y = area.Bottom - (float)(cycle * Math.Max(22, area.Height - 14));

                g.FillEllipse(brush, x - p.Size / 2f, y - p.Size / 2f, p.Size, p.Size);
            }
        }

        private static void DrawBubbles(
            Graphics g, Rectangle area, Color ink, double seconds,
            AnimationState state, IEnumerable<BubbleSeed> bubbles)
        {
            var alpha = state switch
            {
                AnimationState.Transferring => 70,
                AnimationState.Paused => 42,
                AnimationState.Completing => 78,
                AnimationState.Waiting => 25,
                _ => 0
            };

            var speedScale = state == AnimationState.Paused ? 0.35 : 1.0;

            foreach (var b in bubbles)
            {
                var cycle = (seconds * b.Speed * speedScale + b.Phase) % 1.0;
                if (cycle < 0) cycle += 1.0;

                var x = area.Left + (float)(b.XRatio * area.Width)
                    + (float)Math.Sin(seconds * 0.58 + b.Phase * 7) * b.Drift;
                var y = area.Bottom - 10 - (float)(cycle * Math.Max(24, area.Height - 18));

                using var pen = new Pen(Color.FromArgb(alpha, ink), Math.Max(1f, b.Radius * 0.13f));
                g.DrawEllipse(pen, x - b.Radius, y - b.Radius, b.Radius * 2, b.Radius * 2);

                if (b.Highlight && b.Radius > 3f)
                {
                    using var highlight = new Pen(Color.FromArgb(Math.Min(120, alpha + 25), Color.White), 0.9f);
                    g.DrawArc(highlight,
                        x - b.Radius * 0.55f,
                        y - b.Radius * 0.58f,
                        b.Radius * 0.9f,
                        b.Radius * 0.9f,
                        190, 100);
                }
            }
        }

        private static void DrawCompletionBurst(
            Graphics g, Rectangle area, Color ink, double seconds, int alpha)
        {
            for (var i = 0; i < 30; i++)
            {
                var phase = (seconds * (0.15 + (i % 5) * 0.018) + i * 0.061) % 1.0;
                var x = area.Left + area.Width * (0.16f + ((i * 37) % 68) / 100f)
                    + (float)Math.Sin(seconds + i) * 11f;
                var y = area.Bottom - (float)(phase * area.Height);
                var r = 3f + (i % 5) * 1.3f;

                using var pen = new Pen(Color.FromArgb(Math.Max(10, alpha - i * 2), ink), 1f);
                g.DrawEllipse(pen, x - r, y - r, r * 2, r * 2);
            }
        }

        private static void DrawVignette(Graphics g, Rectangle area, Color ink)
        {
            var band = Math.Max(1, area.Height / 5);

            using var top = new LinearGradientBrush(
                new Rectangle(area.Left, area.Top, area.Width, band),
                Color.FromArgb(18, ink),
                Color.Transparent,
                LinearGradientMode.Vertical);
            g.FillRectangle(top, area.Left, area.Top, area.Width, band);

            using var bottom = new LinearGradientBrush(
                new Rectangle(area.Left, area.Bottom - band, area.Width, band),
                Color.Transparent,
                Color.FromArgb(24, ink),
                LinearGradientMode.Vertical);
            g.FillRectangle(bottom, area.Left, area.Bottom - band, area.Width, band);
        }

        private static void DrawCenteredImage(
            Graphics g, Image image, float centerX, float centerY,
            float scaleX, float scaleY, float angle, float alpha)
        {
            var saved = g.Save();
            try
            {
                g.TranslateTransform(centerX, centerY);
                g.RotateTransform(angle);

                var w = image.Width * scaleX;
                var h = image.Height * scaleY;
                var dest = Rectangle.Round(new RectangleF(-w / 2f, -h / 2f, w, h));

                using var attrs = AlphaAttributes(alpha);
                g.DrawImage(image, dest, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attrs);
            }
            finally
            {
                g.Restore(saved);
            }
        }

        private static void DrawImageCover(
            Graphics g, Image image, Rectangle area,
            float xOffset, float yOffset, float alpha)
        {
            var scale = Math.Max(
                (float)area.Width / image.Width,
                (float)area.Height / image.Height);

            var w = image.Width * scale;
            var h = image.Height * scale;
            var x = area.Left + (area.Width - w) / 2f + xOffset;
            var y = area.Top + (area.Height - h) / 2f + yOffset;
            var dest = Rectangle.Round(new RectangleF(x, y, w, h));

            using var attrs = AlphaAttributes(alpha);
            g.DrawImage(image, dest, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attrs);
        }

        private static ImageAttributes AlphaAttributes(float alpha)
        {
            var attrs = new ImageAttributes();
            var matrix = new ColorMatrix { Matrix33 = Math.Clamp(alpha, 0f, 1f) };
            attrs.SetColorMatrix(matrix);
            return attrs;
        }

        private void SeedScene()
        {
            for (var i = 0; i < 18; i++)
                _backBubbles.Add(NewBubble(front: false));

            for (var i = 0; i < 13; i++)
                _frontBubbles.Add(NewBubble(front: true));

            for (var i = 0; i < 52; i++)
            {
                _particles.Add(new ParticleSeed(
                    _random.NextDouble(),
                    _random.NextDouble(),
                    0.018 + _random.NextDouble() * 0.040,
                    1f + (float)_random.NextDouble() * 1.8f,
                    1f + (float)_random.NextDouble() * 4f,
                    _random.NextDouble() > 0.68));
            }
        }

        private BubbleSeed NewBubble(bool front) => new(
            _random.NextDouble(),
            _random.NextDouble(),
            0.050 + _random.NextDouble() * 0.11,
            (front ? 3.5f : 2.2f) + (float)_random.NextDouble() * (front ? 5.5f : 4.2f),
            3f + (float)_random.NextDouble() * 11f,
            _random.NextDouble() > 0.22);

        private static Color ContrastInk(Color background)
        {
            var brightness = background.R * 0.299 + background.G * 0.587 + background.B * 0.114;
            return brightness < 135
                ? Color.FromArgb(224, 224, 224)
                : Color.FromArgb(70, 70, 70);
        }

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;

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
            catch { }
        }

        private static IEnumerable<T> FindControls<T>(Control root) where T : Control
        {
            foreach (Control child in root.Controls)
            {
                if (child is T match) yield return match;
                foreach (var nested in FindControls<T>(child))
                    yield return nested;
            }
        }

        private sealed record BubbleSeed(
            double XRatio,
            double Phase,
            double Speed,
            float Radius,
            float Drift,
            bool Highlight);

        private sealed record ParticleSeed(
            double XRatio,
            double Phase,
            double Speed,
            float Size,
            float Drift,
            bool Front);

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
