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
        private readonly List<ParticleSeed> _particles = [];
        private readonly List<FishSeed> _fish = [];

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
            SeedScene();

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

                var tone = ContrastTone(_grid.BackgroundColor);
                var seconds = Environment.TickCount64 / 1000.0;

                DrawWaterBackground(g, area, tone, seconds);
                DrawLightRays(g, area, tone, seconds);
                DrawDistantTerrain(g, area, tone, seconds);
                DrawParticles(g, area, tone, seconds, state, foreground: false);
                DrawBubbleField(g, area, tone, seconds, state, foreground: false);
                DrawDistantFish(g, area, tone, seconds, state);
                DrawOceanHints(g, area, tone, state, seconds);
                DrawSeafloor(g, area, tone, state, seconds);

                var centerY = area.Top + area.Height * 0.40;
                var scale = Math.Clamp(area.Height / 190f, 1.45f, 2.20f);

                if (state == AnimationState.Transferring)
                {
                    var x = area.Left + (float)_swimX;
                    var y = (float)(centerY + Math.Sin(seconds * 1.0) * 7);
                    DrawTurtle(g, x, y, scale, tone, 245, seconds, true);
                }
                else if (state == AnimationState.Paused)
                {
                    var x = (float)Math.Clamp(area.Left + _swimX, area.Left + 145, area.Right - 315);
                    var y = (float)(centerY + Math.Sin(seconds * 1.35) * 4);
                    DrawTurtle(g, x, y, scale, tone, 218, seconds * 0.35, false);
                }
                else if (state == AnimationState.Completing)
                {
                    var remaining = Math.Clamp((_completionUntilUtc - now).TotalSeconds / 3.0, 0, 1);
                    var alpha = (int)(240 * remaining);
                    var x = area.Left + area.Width * 0.61f;
                    var y = (float)(centerY + Math.Sin(seconds * 1.05) * 5);
                    DrawTurtle(g, x, y, scale, tone, alpha, seconds, true);
                    DrawCelebrationBubbles(g, area, tone, seconds, (int)(110 * remaining));
                }
                else if (state == AnimationState.Waiting)
                {
                    DrawTurtle(
                        g,
                        area.Left + area.Width * 0.67f,
                        (float)centerY,
                        scale * 0.90f,
                        tone,
                        125,
                        seconds * 0.20,
                        false);
                }

                DrawBubbleField(g, area, tone, seconds, state, foreground: true);
                DrawForegroundPlants(g, area, tone, seconds, state);
                DrawParticles(g, area, tone, seconds, state, foreground: true);
            }
            finally
            {
                g.Restore(saved);
            }
        }

        private static void DrawWaterBackground(Graphics g, Rectangle area, int tone, double seconds)
        {
            var dark = tone > 128;
            var top = dark ? Color.FromArgb(26, 31, 36) : Color.FromArgb(240, 242, 244);
            var bottom = dark ? Color.FromArgb(48, 53, 58) : Color.FromArgb(211, 216, 220);

            using var water = new LinearGradientBrush(
                new Point(area.Left, area.Top),
                new Point(area.Left, area.Bottom),
                top,
                bottom);

            g.FillRectangle(water, area);

            using var haze = new SolidBrush(Color.FromArgb(16, tone, tone, tone));
            g.FillRectangle(haze, area.Left, area.Top, area.Width, Math.Max(1, (int)(area.Height * 0.25f)));

            using var shimmer = new Pen(Color.FromArgb(18, tone, tone, tone), 1.0f);
            for (var row = 0; row < 3; row++)
            {
                using var path = new GraphicsPath();
                var first = true;
                var baseY = area.Top + 14 + row * 17;

                for (var x = area.Left - 10; x <= area.Right + 10; x += 13)
                {
                    var y = baseY + Math.Sin(x * 0.036 + seconds * 0.80 + row * 1.3) * 2.2;
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

                g.DrawPath(shimmer, path);
            }
        }

        private static void DrawLightRays(Graphics g, Rectangle area, int tone, double seconds)
        {
            for (var i = 0; i < 4; i++)
            {
                var sway = (float)Math.Sin(seconds * 0.23 + i * 0.9) * 18f;
                var topX = area.Left + (i + 1) * area.Width / 5f + sway;
                var topW = 14f + i * 4f;
                var bottomW = 58f + i * 15f;

                using var path = new GraphicsPath();
                path.AddPolygon([
                    new PointF(topX - topW, area.Top),
                    new PointF(topX + topW, area.Top),
                    new PointF(topX + bottomW, area.Bottom),
                    new PointF(topX - bottomW, area.Bottom)
                ]);

                using var brush = new SolidBrush(Color.FromArgb(9 + i * 2, tone, tone, tone));
                g.FillPath(brush, path);
            }
        }

        private static void DrawDistantTerrain(Graphics g, Rectangle area, int tone, double seconds)
        {
            var horizon = area.Bottom - Math.Max(56f, area.Height * 0.23f);
            using var brush = new SolidBrush(Color.FromArgb(20, tone, tone, tone));

            using var ridge = new GraphicsPath();
            ridge.StartFigure();
            ridge.AddLine(area.Left - 12, area.Bottom);
            ridge.AddLine(area.Left - 12, horizon + 18);

            for (var x = area.Left - 12; x <= area.Right + 24; x += 44)
            {
                var y = horizon
                    + (float)Math.Sin(x * 0.013 + seconds * 0.04) * 5f
                    + (float)Math.Sin(x * 0.028) * 7f;
                ridge.AddLine(x, y);
            }

            ridge.AddLine(area.Right + 24, area.Bottom);
            ridge.CloseFigure();
            g.FillPath(brush, ridge);
        }

        private void DrawParticles(
            Graphics g,
            Rectangle area,
            int tone,
            double seconds,
            AnimationState state,
            bool foreground)
        {
            var alpha = state switch
            {
                AnimationState.Transferring => foreground ? 22 : 15,
                AnimationState.Paused => foreground ? 15 : 10,
                AnimationState.Completing => foreground ? 26 : 18,
                AnimationState.Waiting => foreground ? 10 : 7,
                _ => 0
            };

            if (alpha == 0) return;

            using var brush = new SolidBrush(Color.FromArgb(alpha, tone, tone, tone));

            foreach (var p in _particles)
            {
                if (p.Foreground != foreground) continue;

                var cycle = (seconds * p.Speed + p.Phase) % 1.0;
                if (cycle < 0) cycle += 1.0;

                var x = area.Left + (float)(p.XRatio * area.Width)
                    + (float)Math.Sin(seconds * 0.43 + p.Phase * 9) * p.Drift;
                var y = area.Bottom - (float)(cycle * Math.Max(24, area.Height - 14));

                g.FillEllipse(brush, x - p.Size / 2f, y - p.Size / 2f, p.Size, p.Size);
            }
        }

        private void DrawDistantFish(
            Graphics g,
            Rectangle area,
            int tone,
            double seconds,
            AnimationState state)
        {
            if (state == AnimationState.Waiting)
                return;

            foreach (var fish in _fish)
            {
                var t = (seconds * fish.Speed + fish.Phase) % 1.0;
                if (t < 0) t += 1.0;

                var x = fish.RightToLeft
                    ? area.Right + 25 - (float)t * (area.Width + 50)
                    : area.Left - 25 + (float)t * (area.Width + 50);

                var y = area.Top + area.Height * fish.YRatio
                    + (float)Math.Sin(seconds * 0.63 + fish.Phase * 7) * 4f;

                DrawFish(g, x, y, fish.Size, fish.RightToLeft, tone);
            }
        }

        private static void DrawFish(Graphics g, float x, float y, float size, bool rightToLeft, int tone)
        {
            var saved = g.Save();
            try
            {
                g.TranslateTransform(x, y);
                if (rightToLeft) g.ScaleTransform(-1, 1);

                using var brush = new SolidBrush(Color.FromArgb(25, tone, tone, tone));
                g.FillEllipse(brush, -size * 0.42f, -size * 0.19f, size * 0.70f, size * 0.38f);

                using var tail = new GraphicsPath();
                tail.AddPolygon([
                    new PointF(-size * 0.36f, 0),
                    new PointF(-size * 0.69f, -size * 0.27f),
                    new PointF(-size * 0.69f, size * 0.27f)
                ]);
                g.FillPath(brush, tail);
            }
            finally
            {
                g.Restore(saved);
            }
        }

        private static void DrawForegroundPlants(
            Graphics g,
            Rectangle area,
            int tone,
            double seconds,
            AnimationState state)
        {
            if (state == AnimationState.Waiting)
                return;

            DrawForegroundKelp(g, area.Left + 28, area.Bottom + 4, 92, tone, seconds, 0.2);
            DrawForegroundKelp(g, area.Right - 42, area.Bottom + 4, 106, tone, seconds, 1.7);
        }

        private static void DrawForegroundKelp(
            Graphics g,
            float x,
            float baseY,
            float height,
            int tone,
            double seconds,
            double phase)
        {
            using var pen = new Pen(Color.FromArgb(32, tone, tone, tone), 2.2f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };

            for (var blade = 0; blade < 5; blade++)
            {
                var offset = (blade - 2f) * 7f;
                var sway = (float)Math.Sin(seconds * 0.37 + phase + blade * 0.55) * (9f + blade);
                var h = height * (0.68f + (blade % 3) * 0.14f);

                using var path = new GraphicsPath();
                path.StartFigure();
                path.AddBezier(
                    x + offset, baseY,
                    x + offset - sway * 0.24f, baseY - h * 0.34f,
                    x + offset + sway * 0.55f, baseY - h * 0.70f,
                    x + offset + sway, baseY - h);

                g.DrawPath(pen, path);
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

        private static void DrawSeafloor(
            Graphics g,
            Rectangle area,
            int tone,
            AnimationState state,
            double seconds)
        {
            if (state == AnimationState.Idle)
                return;

            var quiet = state == AnimationState.Waiting;
            var alpha = quiet ? 18 : 38;
            var floorY = area.Bottom - Math.Max(34f, area.Height * 0.15f);

            using var sandFill = new SolidBrush(Color.FromArgb(alpha, tone, tone, tone));
            using var sandPen = new Pen(Color.FromArgb(alpha + 13, tone, tone, tone), 1.1f);

            using (var sand = new GraphicsPath())
            {
                sand.StartFigure();
                sand.AddBezier(
                    area.Left - 12, floorY + 8,
                    area.Left + area.Width * 0.25f, floorY - 6,
                    area.Left + area.Width * 0.48f, floorY + 11,
                    area.Left + area.Width * 0.69f, floorY + 1);
                sand.AddBezier(
                    area.Left + area.Width * 0.69f, floorY + 1,
                    area.Left + area.Width * 0.83f, floorY - 7,
                    area.Right + 10, floorY + 7,
                    area.Right + 14, floorY + 8);
                sand.AddLine(sand.GetLastPoint(), new PointF(area.Right + 14, area.Bottom + 4));
                sand.AddLine(sand.GetLastPoint(), new PointF(area.Left - 14, area.Bottom + 4));
                sand.CloseFigure();
                g.FillPath(sandFill, sand);
                g.DrawPath(sandPen, sand);
            }

            DrawRock(g, area.Left + area.Width * 0.15f, area.Bottom - 17, 34, 17, tone, alpha + 24);
            DrawRock(g, area.Left + area.Width * 0.20f, area.Bottom - 13, 19, 10, tone, alpha + 13);
            DrawRock(g, area.Left + area.Width * 0.79f, area.Bottom - 18, 38, 19, tone, alpha + 22);
            DrawRock(g, area.Left + area.Width * 0.84f, area.Bottom - 12, 21, 10, tone, alpha + 12);

            DrawSeaGrassCluster(g, area.Left + area.Width * 0.08f, area.Bottom - 7, 46, tone, alpha + 22, seconds, 0.0);
            DrawSeaGrassCluster(g, area.Left + area.Width * 0.28f, area.Bottom - 7, 58, tone, alpha + 22, seconds, 1.0);
            DrawSeaGrassCluster(g, area.Left + area.Width * 0.66f, area.Bottom - 7, 50, tone, alpha + 20, seconds, 2.0);
            DrawSeaGrassCluster(g, area.Left + area.Width * 0.91f, area.Bottom - 7, 61, tone, alpha + 24, seconds, 2.8);

            DrawCoral(g, area.Left + area.Width * 0.41f, area.Bottom - 8, 38, tone, alpha + 23);
            DrawCoral(g, area.Left + area.Width * 0.73f, area.Bottom - 8, 32, tone, alpha + 18);

            using var smallPen = new Pen(Color.FromArgb(alpha + 28, tone, tone, tone), 1.05f);

            // Shell.
            var shellX = area.Left + area.Width * 0.55f;
            var shellY = area.Bottom - 12;
            g.DrawArc(smallPen, shellX - 8, shellY - 6, 16, 11, 180, 180);
            g.DrawLine(smallPen, shellX, shellY - 5, shellX, shellY + 3);
            g.DrawLine(smallPen, shellX - 5, shellY - 3, shellX - 2, shellY + 3);
            g.DrawLine(smallPen, shellX + 5, shellY - 3, shellX + 2, shellY + 3);

            // Small starfish-like bottom detail.
            var starX = area.Left + area.Width * 0.34f;
            var starY = area.Bottom - 13;
            using var star = new GraphicsPath();
            var points = new PointF[10];
            for (var i = 0; i < points.Length; i++)
            {
                var angle = -Math.PI / 2 + i * Math.PI / 5;
                var radius = i % 2 == 0 ? 8f : 3.4f;
                points[i] = new PointF(
                    starX + (float)Math.Cos(angle) * radius,
                    starY + (float)Math.Sin(angle) * radius);
            }
            star.AddPolygon(points);
            g.DrawPath(smallPen, star);
        }

        private static void DrawRock(
            Graphics g,
            float x,
            float y,
            float width,
            float height,
            int tone,
            int alpha)
        {
            using var fill = new SolidBrush(Color.FromArgb(Math.Clamp(alpha / 2, 0, 100), tone, tone, tone));
            using var pen = new Pen(Color.FromArgb(Math.Clamp(alpha, 0, 110), tone, tone, tone), 1f);
            using var path = new GraphicsPath();
            path.AddBezier(x - width / 2, y, x - width * 0.43f, y - height, x - width * 0.08f, y - height * 1.1f, x, y - height);
            path.AddBezier(x, y - height, x + width * 0.27f, y - height * 1.05f, x + width * 0.48f, y - height * 0.50f, x + width / 2, y);
            path.CloseFigure();
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }

        private static void DrawSeaGrassCluster(
            Graphics g,
            float x,
            float baseY,
            float height,
            int tone,
            int alpha,
            double seconds,
            double phase)
        {
            using var pen = new Pen(Color.FromArgb(Math.Clamp(alpha, 0, 110), tone, tone, tone), 1.35f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };

            for (var blade = 0; blade < 6; blade++)
            {
                var offset = (blade - 2.5f) * 4.0f;
                var sway = (float)Math.Sin(seconds * 0.55 + phase + blade * 0.55) * (5.5f + blade * 0.35f);
                var h = height * (0.62f + (blade % 3) * 0.16f);

                using var grass = new GraphicsPath();
                grass.StartFigure();
                grass.AddBezier(
                    x + offset, baseY,
                    x + offset - sway * 0.20f, baseY - h * 0.34f,
                    x + offset + sway * 0.55f, baseY - h * 0.68f,
                    x + offset + sway, baseY - h);
                g.DrawPath(pen, grass);
            }
        }

        private static void DrawCoral(
            Graphics g,
            float x,
            float baseY,
            float height,
            int tone,
            int alpha)
        {
            using var pen = new Pen(Color.FromArgb(Math.Clamp(alpha, 0, 110), tone, tone, tone), 2.0f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };

            g.DrawLine(pen, x, baseY, x, baseY - height);
            g.DrawLine(pen, x, baseY - height * 0.55f, x - 10, baseY - height * 0.78f);
            g.DrawLine(pen, x - 10, baseY - height * 0.78f, x - 13, baseY - height * 0.93f);
            g.DrawLine(pen, x, baseY - height * 0.38f, x + 11, baseY - height * 0.61f);
            g.DrawLine(pen, x + 11, baseY - height * 0.61f, x + 14, baseY - height * 0.80f);
            g.DrawLine(pen, x, baseY - height * 0.72f, x + 8, baseY - height * 0.92f);
        }

        private void DrawBubbleField(
            Graphics g,
            Rectangle area,
            int tone,
            double seconds,
            AnimationState state,
            bool foreground)
        {
            var count = state switch
            {
                AnimationState.Transferring => 42,
                AnimationState.Paused => 24,
                AnimationState.Completing => 48,
                AnimationState.Waiting => 12,
                _ => 0
            };

            var alpha = state switch
            {
                AnimationState.Transferring => foreground ? 64 : 42,
                AnimationState.Paused => foreground ? 40 : 28,
                AnimationState.Completing => foreground ? 68 : 46,
                AnimationState.Waiting => foreground ? 24 : 17,
                _ => 0
            };

            count = Math.Min(count, _bubbles.Count);

            for (var i = 0; i < count; i++)
            {
                // Split the same deterministic field into back/front layers.
                if (((i % 4) == 0) != foreground)
                    continue;

                var seed = _bubbles[i];
                var speedScale = state == AnimationState.Paused ? 0.32 : 1.0;
                var cycle = (seconds * seed.Speed * speedScale + seed.Phase) % 1.0;
                if (cycle < 0) cycle += 1.0;

                var drift = Math.Sin(seconds * 0.70 + seed.Phase * 8 + i) * seed.Drift;
                var x = area.Left + (float)(seed.XRatio * area.Width + drift);
                var y = area.Bottom - 12 - (float)(cycle * Math.Max(34, area.Height - 26));
                DrawBubble(g, x, y, seed.Radius, tone, alpha, seed.Highlight);
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
            alpha = Math.Clamp(alpha, 0, 255);
            if (alpha <= 0) return;

            var saved = g.Save();
            try
            {
                g.TranslateTransform(centerX, centerY);
                g.ScaleTransform(scale, scale);

                var darkScene = tone > 128;
                var outlineTone = darkScene ? 235 : 48;
                var lightTone = darkScene ? 218 : 118;
                var midTone = darkScene ? 184 : 88;
                var darkTone = darkScene ? 142 : 54;

                var stroke = swimming ? Math.Sin(seconds * 2.18) : Math.Sin(seconds * 0.95) * 0.12;
                var topAngle = swimming ? (float)(-14 + stroke * 29) : -8f;
                var bottomAngle = swimming ? (float)(14 - stroke * 29) : 8f;
                var rearStroke = swimming ? (float)Math.Sin(seconds * 2.18 + 1.15) * 6f : 0f;

                using var bodyBrush = new LinearGradientBrush(
                    new PointF(0, -46),
                    new PointF(0, 46),
                    Color.FromArgb((int)(alpha * 0.92), lightTone, lightTone, lightTone),
                    Color.FromArgb((int)(alpha * 0.94), darkTone, darkTone, darkTone));

                using var bodyOutline = new Pen(
                    Color.FromArgb((int)(alpha * 0.70), outlineTone, outlineTone, outlineTone),
                    1.35f);

                DrawRearFlipper(g, -45, -17, -11 + rearStroke, true, bodyBrush, bodyOutline);
                DrawRearFlipper(g, -45, 17, 11 - rearStroke, false, bodyBrush, bodyOutline);

                using var flipperDetail = new Pen(
                    Color.FromArgb((int)(alpha * 0.22), outlineTone, outlineTone, outlineTone),
                    0.78f);

                DrawFrontFlipper(g, 22, -17, topAngle, true, bodyBrush, bodyOutline, flipperDetail);
                DrawFrontFlipper(g, 22, 17, bottomAngle, false, bodyBrush, bodyOutline, flipperDetail);

                using (var tail = new GraphicsPath())
                {
                    tail.AddPolygon([
                        new PointF(-63, -5),
                        new PointF(-81, 0),
                        new PointF(-63, 6)
                    ]);
                    g.FillPath(bodyBrush, tail);
                }

                using (var neck = new GraphicsPath())
                {
                    neck.AddBezier(36, -9, 49, -11, 62, -10, 68, -5);
                    neck.AddBezier(68, -5, 70, 3, 60, 10, 37, 9);
                    neck.CloseFigure();
                    g.FillPath(bodyBrush, neck);
                }

                using var shellPath = new GraphicsPath();
                shellPath.AddBezier(-59, 0, -56, -35, -25, -44, 10, -40);
                shellPath.AddBezier(10, -40, 43, -35, 56, -16, 54, 0);
                shellPath.AddBezier(54, 0, 52, 24, 26, 40, -10, 40);
                shellPath.AddBezier(-10, 40, -46, 37, -60, 14, -59, 0);
                shellPath.CloseFigure();

                using var shellBrush = new LinearGradientBrush(
                    new PointF(-42, -34),
                    new PointF(43, 35),
                    Color.FromArgb((int)(alpha * 0.98), lightTone, lightTone, lightTone),
                    Color.FromArgb((int)(alpha * 0.99), darkTone, darkTone, darkTone));

                g.FillPath(shellBrush, shellPath);

                using var shellRim = new Pen(
                    Color.FromArgb((int)(alpha * 0.78), outlineTone, outlineTone, outlineTone),
                    1.65f);
                g.DrawPath(shellRim, shellPath);

                var clipped = g.Save();
                g.SetClip(shellPath, CombineMode.Intersect);
                try
                {
                    var patches = new (float X, float Y, float W, float H, int A)[]
                    {
                        (-43, -17, 30, 19, 28),
                        (-17, -29, 37, 22, 22),
                        (13, -20, 32, 21, 31),
                        (-34, 5, 36, 24, 26),
                        (4, 5, 41, 25, 29),
                        (-9, -7, 27, 19, 20)
                    };

                    foreach (var p in patches)
                    {
                        using var patch = new SolidBrush(
                            Color.FromArgb((int)(alpha * p.A / 100.0), midTone, midTone, midTone));
                        g.FillEllipse(patch, p.X, p.Y, p.W, p.H);
                    }

                    using var broadHighlight = new SolidBrush(
                        Color.FromArgb((int)(alpha * 0.18), 245, 245, 245));
                    g.FillEllipse(broadHighlight, -26, -30, 54, 24);

                    using var softHighlight = new SolidBrush(
                        Color.FromArgb((int)(alpha * 0.08), 250, 250, 250));
                    g.FillEllipse(softHighlight, -44, -18, 78, 47);
                }
                finally
                {
                    g.Restore(clipped);
                }

                using (var head = new GraphicsPath())
                {
                    head.AddBezier(55, -14, 72, -17, 88, -9, 91, 0);
                    head.AddBezier(91, 0, 87, 12, 72, 17, 55, 12);
                    head.AddBezier(55, 12, 49, 4, 50, -6, 55, -14);
                    head.CloseFigure();

                    g.FillPath(bodyBrush, head);
                    g.DrawPath(bodyOutline, head);
                }

                using var eyeWhite = new SolidBrush(Color.FromArgb((int)(alpha * 0.72), 245, 245, 245));
                using var pupil = new SolidBrush(Color.FromArgb(alpha, 22, 22, 22));
                g.FillEllipse(eyeWhite, 78f, -6.0f, 4.8f, 4.3f);
                g.FillEllipse(pupil, 79.6f, -5.1f, 2.2f, 2.2f);

                using var facePen = new Pen(
                    Color.FromArgb((int)(alpha * 0.38), outlineTone, outlineTone, outlineTone),
                    0.85f);
                g.DrawArc(facePen, 76, 3, 10, 5, 12, 105);

                using var rimHighlight = new Pen(
                    Color.FromArgb((int)(alpha * 0.25), 248, 248, 248),
                    1.15f);
                g.DrawArc(rimHighlight, -49, -35, 91, 63, 198, 118);
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

        private void SeedScene()
        {
            for (var i = 0; i < 56; i++)
            {
                _bubbles.Add(new BubbleSeed(
                    _random.NextDouble(),
                    _random.NextDouble(),
                    0.050 + _random.NextDouble() * 0.12,
                    2.5f + (float)_random.NextDouble() * 7.0f,
                    3.0f + (float)_random.NextDouble() * 13.0f,
                    _random.NextDouble() > 0.20));
            }

            for (var i = 0; i < 72; i++)
            {
                _particles.Add(new ParticleSeed(
                    _random.NextDouble(),
                    _random.NextDouble(),
                    0.018 + _random.NextDouble() * 0.045,
                    1.0f + (float)_random.NextDouble() * 2.0f,
                    1.0f + (float)_random.NextDouble() * 5.0f,
                    _random.NextDouble() > 0.68));
            }

            for (var i = 0; i < 5; i++)
            {
                _fish.Add(new FishSeed(
                    0.18f + (float)_random.NextDouble() * 0.34f,
                    (float)_random.NextDouble(),
                    0.012 + _random.NextDouble() * 0.018,
                    12f + (float)_random.NextDouble() * 10f,
                    _random.NextDouble() > 0.5));
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

        private sealed record ParticleSeed(
            double XRatio,
            double Phase,
            double Speed,
            float Size,
            float Drift,
            bool Foreground);

        private sealed record FishSeed(
            float YRatio,
            double Phase,
            double Speed,
            float Size,
            bool RightToLeft);

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
