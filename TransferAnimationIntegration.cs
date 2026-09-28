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
        private readonly AnimationSurface _surface;
        private readonly Random _random = new(45827);
        private readonly List<BubbleSeed> _backBubbles = [];
        private readonly List<BubbleSeed> _frontBubbles = [];
        private readonly List<ParticleSeed> _particles = [];
        private readonly List<CritterSeed> _critters = [];

        private Bitmap? _background;
        private Bitmap? _backgroundFrame;
        private Size _backgroundFrameSize = Size.Empty;
        private Bitmap? _turtle;
        private Bitmap? _turtleBody;
        private Bitmap? _turtleNearFlipper;
        private Bitmap? _turtleFarFlipper;
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
                UpdateSurfaceBounds();
                _surface.Invalidate();
            };

            EnableDoubleBuffering(_grid);
            SeedScene();
            LoadArtwork();

            _surface = new AnimationSurface(this)
            {
                Visible = false,
                BackColor = _grid.BackgroundColor
            };
            _grid.Controls.Add(_surface);
            _surface.BringToFront();

            _grid.Resize += (_, _) =>
            {
                ResetBackgroundFrame();
                UpdateSurfaceBounds();
            };
            _grid.Scroll += (_, _) => UpdateSurfaceBounds();
            _grid.RowsAdded += (_, _) => UpdateSurfaceBounds();
            _grid.RowsRemoved += (_, _) => UpdateSurfaceBounds();
            _grid.RowHeightChanged += (_, _) => UpdateSurfaceBounds();
            _grid.ColumnHeadersHeightChanged += (_, _) => UpdateSurfaceBounds();

            UpdateSurfaceBounds();

            _timer = new System.Windows.Forms.Timer { Interval = 16 };
            _timer.Tick += OnTick;
            _timer.Start();

            _form.FormClosed += (_, _) =>
            {
                _timer.Stop();
                _timer.Dispose();
                _surface.Dispose();
                _background?.Dispose();
                _backgroundFrame?.Dispose();
                _turtle?.Dispose();
                _turtleBody?.Dispose();
                _turtleNearFlipper?.Dispose();
                _turtleFarFlipper?.Dispose();
                _foreground?.Dispose();
            };
        }

        private void LoadArtwork()
        {
            _background = DecodeArtwork(TransferAnimationOceanBackground.Data);
            _turtle = DecodeArtwork(TransferAnimationTurtleSprite.Data);
            _foreground = null;

            ResetBackgroundFrame();

            if (_turtle is not null)
                BuildTurtleLayers(_turtle);
        }

        private void ResetBackgroundFrame()
        {
            _backgroundFrame?.Dispose();
            _backgroundFrame = null;
            _backgroundFrameSize = Size.Empty;
        }

        private void EnsureBackgroundFrame(Size size)
        {
            if (_background is null || size.Width <= 0 || size.Height <= 0)
                return;

            if (_backgroundFrame is not null && _backgroundFrameSize == size)
                return;

            ResetBackgroundFrame();

            _backgroundFrame = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(_backgroundFrame);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            DrawImageCover(
                g,
                _background,
                new Rectangle(0, 0, size.Width, size.Height),
                0,
                0,
                0.78f);

            _backgroundFrameSize = size;
        }

        private void BuildTurtleLayers(Bitmap source)
        {
            _turtleBody?.Dispose();
            _turtleNearFlipper?.Dispose();
            _turtleFarFlipper?.Dispose();

            _turtleBody = new Bitmap(source);

            var nearExtract = ScalePolygon(source,
            [
                new PointF(173, 147), new PointF(197, 140), new PointF(218, 147),
                new PointF(231, 164), new PointF(226, 184), new PointF(210, 205),
                new PointF(190, 224), new PointF(164, 242), new PointF(137, 241),
                new PointF(129, 231), new PointF(133, 219), new PointF(145, 198),
                new PointF(154, 177)
            ]);

            var farExtract = ScalePolygon(source,
            [
                new PointF(274, 141), new PointF(293, 143), new PointF(311, 153),
                new PointF(327, 171), new PointF(340, 191), new PointF(343, 207),
                new PointF(335, 215), new PointF(322, 211), new PointF(307, 200),
                new PointF(293, 186), new PointF(280, 170), new PointF(269, 155)
            ]);

            var nearErase = ScalePolygon(source,
            [
                new PointF(181, 153), new PointF(203, 148), new PointF(220, 157),
                new PointF(227, 172), new PointF(220, 188), new PointF(204, 207),
                new PointF(185, 226), new PointF(160, 241), new PointF(138, 239),
                new PointF(131, 230), new PointF(137, 216), new PointF(149, 197),
                new PointF(159, 176)
            ]);

            var farErase = ScalePolygon(source,
            [
                new PointF(281, 148), new PointF(296, 147), new PointF(312, 157),
                new PointF(327, 175), new PointF(339, 192), new PointF(341, 207),
                new PointF(334, 213), new PointF(322, 209), new PointF(308, 199),
                new PointF(295, 187), new PointF(283, 172), new PointF(274, 157)
            ]);

            _turtleNearFlipper = ExtractPolygonLayer(source, nearExtract);
            _turtleFarFlipper = ExtractPolygonLayer(source, farExtract);

            ClearPolygon(_turtleBody, nearErase);
            ClearPolygon(_turtleBody, farErase);
        }

        private static PointF[] ScalePolygon(Bitmap source, PointF[] points)
        {
            const float designWidth = 360f;
            const float designHeight = 270f;
            var sx = source.Width / designWidth;
            var sy = source.Height / designHeight;

            return points
                .Select(p => new PointF(p.X * sx, p.Y * sy))
                .ToArray();
        }

        private static Bitmap ExtractPolygonLayer(Bitmap source, PointF[] polygon)
        {
            var layer = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(layer);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = new GraphicsPath();
            path.AddPolygon(polygon);
            g.SetClip(path);
            g.DrawImageUnscaled(source, 0, 0);
            return layer;
        }

        private static void ClearPolygon(Bitmap target, PointF[] polygon)
        {
            using var g = Graphics.FromImage(target);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.CompositingMode = CompositingMode.SourceCopy;

            using var path = new GraphicsPath();
            path.AddPolygon(polygon);
            using var transparent = new SolidBrush(Color.Transparent);
            g.FillPath(transparent, path);
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

            UpdateSurfaceBounds(state);
            if (_surface.Visible)
                _surface.Invalidate();
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

        private void UpdateSurfaceBounds(AnimationState? knownState = null)
        {
            var area = GetAnimationArea();
            var state = knownState ?? GetState(DateTime.UtcNow);

            var shouldShow =
                _toggle.Checked &&
                state != AnimationState.Idle &&
                area.Width >= 260 &&
                area.Height >= 130;

            if (!shouldShow)
            {
                _surface.Visible = false;
                return;
            }

            if (_surface.Bounds != area)
            {
                _surface.Bounds = area;
                ResetBackgroundFrame();
            }

            _surface.Visible = true;
            _surface.BringToFront();
        }

        private void PaintScene(Graphics g, Rectangle area)
        {
            if (!_toggle.Checked || area.Width < 260 || area.Height < 130)
                return;

            var now = DateTime.UtcNow;
            var state = GetState(now);
            if (state == AnimationState.Idle)
                return;

            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighSpeed;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            var seconds = Environment.TickCount64 / 1000.0;
            var ink = ContrastInk(_grid.BackgroundColor);

            if (_background is not null)
            {
                EnsureBackgroundFrame(area.Size);
                if (_backgroundFrame is not null)
                    g.DrawImageUnscaled(_backgroundFrame, 0, 0);
                else
                    DrawFallbackWater(g, area, ink);
            }
            else
            {
                DrawFallbackWater(g, area, ink);
            }

            DrawLightRays(g, area, ink, seconds);
            DrawParticles(g, area, ink, seconds, state, front: false);
            DrawCritters(g, area, ink, seconds, state);
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
                    y = centerY + (float)Math.Sin(seconds * 0.95) * 8.5f;
                    angle = (float)Math.Sin(seconds * 1.18) * 1.55f;
                    alpha = 1.0f;
                    break;

                case AnimationState.Paused:
                    x = Math.Clamp(
                        Lerp(area.Left + halfWidth, area.Right - halfWidth, (float)Math.Clamp(_travel, 0, 1)),
                        area.Left + halfWidth,
                        area.Right - halfWidth);
                    y = centerY + (float)Math.Sin(seconds * 1.10) * 3f;
                    angle = (float)Math.Sin(seconds * 0.70) * 0.55f;
                    alpha = 0.94f;
                    break;

                case AnimationState.Completing:
                    x = area.Left + area.Width * 0.60f;
                    y = centerY + (float)Math.Sin(seconds * 0.95) * 4f;
                    angle = (float)Math.Sin(seconds * 1.00) * 0.95f;
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

            var stroke = (float)Math.Sin(seconds * 2.30);
            var activeStroke = state is AnimationState.Transferring or AnimationState.Completing;
            var nearFlipperAngle = activeStroke ? stroke * 9.0f :
                state == AnimationState.Paused ? stroke * 1.8f : 0f;
            var farFlipperAngle = activeStroke ? -stroke * 7.2f :
                state == AnimationState.Paused ? -stroke * 1.3f : 0f;

            if (activeStroke)
            {
                // A tiny pitch and surge on the power stroke makes the motion read as propulsion
                // rather than a sprite simply sliding across the screen.
                angle += stroke * 0.38f;
                y += stroke * 1.6f;
                x += Math.Max(0f, -stroke) * 3.8f;
            }

            if (_turtleBody is null || _turtleNearFlipper is null || _turtleFarFlipper is null)
            {
                DrawCenteredImage(g, _turtle, x, y, scale, scale, angle, alpha);
                return;
            }

            DrawLayeredTurtle(
                g,
                x,
                y,
                scale,
                angle,
                alpha,
                nearFlipperAngle,
                farFlipperAngle);
        }

        private void DrawLayeredTurtle(
            Graphics g,
            float centerX,
            float centerY,
            float scale,
            float bodyAngle,
            float alpha,
            float nearFlipperAngle,
            float farFlipperAngle)
        {
            if (_turtleBody is null || _turtleNearFlipper is null || _turtleFarFlipper is null)
                return;

            var saved = g.Save();
            try
            {
                g.TranslateTransform(centerX, centerY);
                g.RotateTransform(bodyAngle);
                g.ScaleTransform(scale, scale);

                var w = _turtleBody.Width;
                var h = _turtleBody.Height;
                var originX = -w / 2f;
                var originY = -h / 2f;

                const float designWidth = 360f;
                const float designHeight = 270f;
                var sx = w / designWidth;
                var sy = h / designHeight;

                var farPivot = new PointF(282f * sx - w / 2f, 151f * sy - h / 2f);
                var nearPivot = new PointF(193f * sx - w / 2f, 151f * sy - h / 2f);

                DrawLayerAroundPivot(
                    g,
                    _turtleFarFlipper,
                    originX,
                    originY,
                    farPivot,
                    farFlipperAngle,
                    alpha);

                DrawImageLayer(g, _turtleBody, originX, originY, alpha);

                DrawLayerAroundPivot(
                    g,
                    _turtleNearFlipper,
                    originX,
                    originY,
                    nearPivot,
                    nearFlipperAngle,
                    alpha);
            }
            finally
            {
                g.Restore(saved);
            }
        }

        private static void DrawLayerAroundPivot(
            Graphics g,
            Image layer,
            float originX,
            float originY,
            PointF pivot,
            float angle,
            float alpha)
        {
            var saved = g.Save();
            try
            {
                g.TranslateTransform(pivot.X, pivot.Y);
                g.RotateTransform(angle);
                g.TranslateTransform(-pivot.X, -pivot.Y);

                DrawImageLayer(g, layer, originX, originY, alpha);
            }
            finally
            {
                g.Restore(saved);
            }
        }

        private static void DrawImageLayer(
            Graphics g,
            Image image,
            float originX,
            float originY,
            float alpha)
        {
            var dest = Rectangle.Round(new RectangleF(
                originX,
                originY,
                image.Width,
                image.Height));

            if (alpha >= 0.999f)
            {
                g.DrawImage(image, dest);
                return;
            }

            using var attrs = AlphaAttributes(alpha);
            g.DrawImage(
                image,
                dest,
                0,
                0,
                image.Width,
                image.Height,
                GraphicsUnit.Pixel,
                attrs);
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

            if (alpha <= 0)
                return;

            var speedScale = state == AnimationState.Paused ? 0.35 : 1.0;
            using var pen = new Pen(Color.FromArgb(alpha, ink), 1.1f);
            using var highlight = new Pen(Color.FromArgb(Math.Min(120, alpha + 25), Color.White), 0.9f);

            foreach (var b in bubbles)
            {
                var cycle = (seconds * b.Speed * speedScale + b.Phase) % 1.0;
                if (cycle < 0) cycle += 1.0;

                var x = area.Left + (float)(b.XRatio * area.Width)
                    + (float)Math.Sin(seconds * 0.58 + b.Phase * 7) * b.Drift;
                var y = area.Bottom - 10 - (float)(cycle * Math.Max(24, area.Height - 18));

                pen.Width = Math.Max(1f, b.Radius * 0.12f);
                g.DrawEllipse(pen, x - b.Radius, y - b.Radius, b.Radius * 2, b.Radius * 2);

                if (b.Highlight && b.Radius > 3f)
                {
                    g.DrawArc(highlight,
                        x - b.Radius * 0.55f,
                        y - b.Radius * 0.58f,
                        b.Radius * 0.9f,
                        b.Radius * 0.9f,
                        190, 100);
                }
            }
        }

        private void DrawCritters(
            Graphics g,
            Rectangle area,
            Color ink,
            double seconds,
            AnimationState state)
        {
            var alpha = state switch
            {
                AnimationState.Transferring => 58,
                AnimationState.Completing => 58,
                AnimationState.Paused => 38,
                AnimationState.Waiting => 28,
                _ => 0
            };

            if (alpha <= 0)
                return;

            using var brush = new SolidBrush(Color.FromArgb(alpha, ink));

            foreach (var c in _critters)
            {
                var t = (seconds * c.Speed + c.Phase) % 1.0;
                if (t < 0) t += 1.0;

                var x = c.RightToLeft
                    ? area.Right + c.Size - (float)t * (area.Width + c.Size * 2f)
                    : area.Left - c.Size + (float)t * (area.Width + c.Size * 2f);

                var y = area.Top + area.Height * c.YRatio
                    + (float)Math.Sin(seconds * 0.45 + c.Phase * 6.0) * 3f;

                if (c.Kind == CritterKind.Ray)
                    DrawRay(g, brush, x, y, c.Size, c.RightToLeft);
                else
                    DrawFish(g, brush, x, y, c.Size, c.RightToLeft);
            }
        }

        private static void DrawFish(
            Graphics g,
            Brush brush,
            float x,
            float y,
            float size,
            bool rightToLeft)
        {
            var saved = g.Save();
            try
            {
                g.TranslateTransform(x, y);
                if (rightToLeft)
                    g.ScaleTransform(-1, 1);

                g.FillEllipse(
                    brush,
                    -size * 0.34f,
                    -size * 0.16f,
                    size * 0.62f,
                    size * 0.32f);

                using var tail = new GraphicsPath();
                tail.AddPolygon([
                    new PointF(-size * 0.29f, 0),
                    new PointF(-size * 0.58f, -size * 0.22f),
                    new PointF(-size * 0.58f, size * 0.22f)
                ]);
                g.FillPath(brush, tail);
            }
            finally
            {
                g.Restore(saved);
            }
        }

        private static void DrawRay(
            Graphics g,
            Brush brush,
            float x,
            float y,
            float size,
            bool rightToLeft)
        {
            var saved = g.Save();
            try
            {
                g.TranslateTransform(x, y);
                if (rightToLeft)
                    g.ScaleTransform(-1, 1);

                using var body = new GraphicsPath();
                body.AddBezier(
                    -size * 0.42f, 0,
                    -size * 0.15f, -size * 0.26f,
                    size * 0.16f, -size * 0.26f,
                    size * 0.42f, 0);
                body.AddBezier(
                    size * 0.42f, 0,
                    size * 0.14f, size * 0.20f,
                    -size * 0.16f, size * 0.20f,
                    -size * 0.42f, 0);
                body.CloseFigure();
                g.FillPath(brush, body);

                using var tail = new Pen(((SolidBrush)brush).Color, Math.Max(1f, size * 0.035f));
                g.DrawLine(tail, size * 0.40f, 0, size * 0.82f, size * 0.02f);
            }
            finally
            {
                g.Restore(saved);
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
            // Keep the animated workload intentionally small for smooth 60 fps playback:
            // fewer bubbles, but each is more visually distinct.
            for (var i = 0; i < 8; i++)
                _backBubbles.Add(NewBubble(front: false));

            for (var i = 0; i < 6; i++)
                _frontBubbles.Add(NewBubble(front: true));

            for (var i = 0; i < 16; i++)
            {
                _particles.Add(new ParticleSeed(
                    _random.NextDouble(),
                    _random.NextDouble(),
                    0.018 + _random.NextDouble() * 0.032,
                    1f + (float)_random.NextDouble() * 1.6f,
                    1f + (float)_random.NextDouble() * 3.2f,
                    _random.NextDouble() > 0.72));
            }

            _critters.Add(new CritterSeed(CritterKind.Fish, 0.25f, 0.16, 0.024, 28f, false));
            _critters.Add(new CritterSeed(CritterKind.Fish, 0.36f, 0.56, 0.020, 23f, true));
            _critters.Add(new CritterSeed(CritterKind.Ray, 0.54f, 0.74, 0.015, 34f, true));
        }

        private BubbleSeed NewBubble(bool front)
        {
            var roll = _random.NextDouble();

            float radius;
            if (!front)
            {
                radius = roll switch
                {
                    < 0.55 => 2.4f + (float)_random.NextDouble() * 2.2f,
                    < 0.88 => 4.8f + (float)_random.NextDouble() * 2.8f,
                    _ => 7.6f + (float)_random.NextDouble() * 2.8f
                };
            }
            else
            {
                radius = roll switch
                {
                    < 0.30 => 4.8f + (float)_random.NextDouble() * 2.8f,
                    < 0.78 => 7.8f + (float)_random.NextDouble() * 4.2f,
                    _ => 12.5f + (float)_random.NextDouble() * 5.5f
                };
            }

            var speed = 0.028 + radius * 0.0092 + _random.NextDouble() * 0.018;

            var x = _random.NextDouble();
            if (_random.NextDouble() < 0.28)
            {
                var cluster = _random.Next(3) switch
                {
                    0 => 0.20,
                    1 => 0.53,
                    _ => 0.80
                };
                x = Math.Clamp(cluster + (_random.NextDouble() - 0.5) * 0.14, 0.04, 0.96);
            }

            return new BubbleSeed(
                x,
                _random.NextDouble(),
                speed,
                radius,
                2f + (float)_random.NextDouble() * (front ? 9f : 5f),
                _random.NextDouble() > 0.12);
        }

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

        private sealed record CritterSeed(
            CritterKind Kind,
            float YRatio,
            double Phase,
            double Speed,
            float Size,
            bool RightToLeft);

        private enum CritterKind
        {
            Fish,
            Ray
        }

        private enum AnimationState
        {
            Idle,
            Waiting,
            Transferring,
            Paused,
            Completing
        }


        private sealed class AnimationSurface : Control
        {
            private readonly Controller _owner;

            public AnimationSurface(Controller owner)
            {
                _owner = owner;
                SetStyle(
                    ControlStyles.UserPaint |
                    ControlStyles.AllPaintingInWmPaint |
                    ControlStyles.OptimizedDoubleBuffer |
                    ControlStyles.Opaque,
                    true);
                TabStop = false;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                _owner.PaintScene(e.Graphics, ClientRectangle);
            }

            protected override void WndProc(ref Message m)
            {
                const int WM_NCHITTEST = 0x0084;
                const int HTTRANSPARENT = -1;

                if (m.Msg == WM_NCHITTEST)
                {
                    m.Result = (IntPtr)HTTRANSPARENT;
                    return;
                }

                base.WndProc(ref m);
            }
        }
    }
}
