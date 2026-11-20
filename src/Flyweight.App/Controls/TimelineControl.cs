using System.Drawing.Drawing2D;
using Flyweight.Core.Editing;
using Flyweight.Core.Media;
using Flyweight.Core.Model;

namespace Flyweight.App.Controls;

/// <summary>
/// Owner-drawn single-track timeline with the audio track and annotation lanes underneath.
///
///   ruler       click/drag to scrub
///   clip track  click to select + seek, drag body to reorder, drag edges to trim
///   audio track drag to move in time, drag ends to trim (items stay at their timeline time)
///   annotations drag bar to move in time, drag ends to change duration
///   wheel       scroll; Ctrl+wheel zoom around the mouse
/// </summary>
public sealed class TimelineControl : Control
{
    const int RulerH = 24, TrackTop = 30, TrackH = 64, LaneGap = 8, LaneH = 18, LaneSpacing = 3, EdgeGrab = 7;
    const int AudioGap = 6;

    /// <summary>Scales with Windows display scaling, like the text in the other lanes.</summary>
    int AudioRowH => Math.Max(30, (int)(Font.Height * 1.8));
    static readonly Color ClipColor = Color.FromArgb(52, 99, 160);
    static readonly Color AudioColor = Color.FromArgb(36, 104, 80);
    static readonly Color Accent = Color.FromArgb(0, 170, 255);

    ProjectSession _session = null!;
    MediaLibrary _library = null!;
    Func<TimeSpan> _playhead = () => TimeSpan.Zero;
    readonly Dictionary<Guid, Image> _thumbs = new();

    double _pps = 40;       // pixels per second
    float _scrollX;         // pixels

    enum Drag { None, Scrub, ClipBody, TrimIn, TrimOut, AnnoMove, AnnoStart, AnnoEnd, AudioMove, AudioIn, AudioOut }
    Drag _drag;
    int _downX;
    bool _dragMoved;
    Guid _dragId;
    TimeSpan _originA, _originB;
    int _dropIndex = -1;

    public TimelineControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Color.FromArgb(28, 28, 32);
        ForeColor = Color.Gainsboro;
    }

    public Guid? SelectedClipId { get; set; }
    public Guid? SelectedAnnotationId { get; set; }
    public Guid? SelectedAudioId { get; set; }

    public event EventHandler<TimeSpan>? SeekRequested;
    public event EventHandler? ClipSelected;
    public event EventHandler? AnnotationSelected;
    public event EventHandler? AudioSelected;

    /// <summary>Right-click on a clip (already selected): clip id and screen position for a context menu.</summary>
    public event Action<Guid, Point>? ClipMenuRequested;

    /// <summary>Right-click on an audio item (already selected): its id and screen position.</summary>
    public event Action<Guid, Point>? AudioMenuRequested;

    public void Bind(ProjectSession session, MediaLibrary library, Func<TimeSpan> playhead)
    {
        _session = session;
        _library = library;
        _playhead = playhead;
    }

    // ---------------- Geometry ----------------

    float X(TimeSpan t) => (float)(t.TotalSeconds * _pps) - _scrollX;
    TimeSpan T(float x) => TimeSpan.FromSeconds(Math.Max(0, (x + _scrollX) / _pps));
    int LaneTop(int lane) => AudioTop + AudioSectionH() + LaneGap + lane * (LaneH + LaneSpacing);

    // ---- Audio track: rows under the video track (overlapping items stack) ----

    const int AudioTop = TrackTop + TrackH + AudioGap;

    /// <summary>Audio items with a row each, so overlapping items don't hide each other.</summary>
    List<(AudioItem A, int Row)> AudioRows()
    {
        var rowEnds = new List<TimeSpan>();
        var result = new List<(AudioItem, int)>();
        foreach (var a in _session.Project.AudioTrack.OrderBy(a => a.Start))
        {
            int row = rowEnds.FindIndex(end => end <= a.Start);
            if (row < 0) { row = rowEnds.Count; rowEnds.Add(a.End); } else rowEnds[row] = a.End;
            result.Add((a, row));
        }
        return result;
    }

    /// <summary>Height of the audio section: at least one row, so there's always a place to see it belongs.</summary>
    int AudioSectionH()
    {
        int rows = Math.Max(1, AudioRows().Select(r => r.Row + 1).DefaultIfEmpty(0).Max());
        return rows * (AudioRowH + LaneSpacing);
    }

    RectangleF AudioRect(AudioItem a, int row) =>
        new(X(a.Start), AudioTop + row * (AudioRowH + LaneSpacing), Math.Max(4, (float)(a.Duration.TotalSeconds * _pps)), AudioRowH);

    RectangleF ClipRect(int index)
    {
        var p = _session.Project;
        var start = TimelineOps.ClipStart(p, index);
        var c = p.Timeline[index];
        return new RectangleF(X(start), TrackTop, (float)(c.Duration.TotalSeconds * _pps), TrackH);
    }

    /// <summary>
    /// One bar per annotation (a run across cuts is ONE bar, represented by its first piece), with its
    /// timeline range and a lane assigned so bars don't overlap.
    /// </summary>
    List<(Annotation A, TimeSpan S, TimeSpan E, int Lane)> AnnotationBars()
    {
        var p = _session.Project;
        var bars = p.Annotations
            .Where(a => a.LinkId is null || TimelineOps.LinkedPieces(p, a)[0].Id == a.Id)
            .Select(a => (A: a, R: TimelineOps.RunRange(p, a)))
            .Where(x => x.R is not null)
            .Select(x => (x.A, S: x.R!.Value.Start, E: x.R!.Value.End))
            .OrderBy(x => x.S)
            .ToList();
        var laneEnds = new List<TimeSpan>();
        var result = new List<(Annotation, TimeSpan, TimeSpan, int)>();
        foreach (var (a, s, e) in bars)
        {
            int lane = laneEnds.FindIndex(end => end <= s);
            if (lane < 0) { lane = laneEnds.Count; laneEnds.Add(e); } else laneEnds[lane] = e;
            result.Add((a, s, e, lane));
        }
        return result;
    }

    RectangleF BarRect(TimeSpan s, TimeSpan e, int lane) =>
        new(X(s), LaneTop(lane), Math.Max(4, (float)((e - s).TotalSeconds * _pps)), LaneH);

    // ---------------- View ----------------

    public void ZoomToFit()
    {
        var total = TimelineOps.TotalDuration(_session.Project).TotalSeconds;
        _pps = total > 0 ? Math.Clamp((ClientSize.Width - 40) / total, 0.5, 400) : 40;
        _scrollX = 0;
        Invalidate();
    }

    /// <summary>Scroll so the playhead stays on screen during playback.</summary>
    public void EnsurePlayheadVisible()
    {
        float x = X(_playhead());
        if (x < 0 || x > ClientSize.Width - 20)
        {
            _scrollX = Math.Max(0, _scrollX + x - ClientSize.Width * 0.2f);
        }
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if ((ModifierKeys & Keys.Control) != 0)
        {
            var at = T(e.X);
            _pps = Math.Clamp(_pps * (e.Delta > 0 ? 1.25 : 0.8), 0.5, 400);
            _scrollX = Math.Max(0, (float)(at.TotalSeconds * _pps) - e.X);
        }
        else
        {
            _scrollX = Math.Max(0, _scrollX - e.Delta);
        }
        Invalidate();
    }

    // ---------------- Painting ----------------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (_session is null) return;
        var p = _session.Project;

        DrawRuler(g);

        for (int i = 0; i < p.Timeline.Count; i++) DrawClip(g, p, i);

        if (_drag == Drag.ClipBody && _dragMoved && _dropIndex >= 0)
        {
            float x = _dropIndex < p.Timeline.Count ? ClipRect(_dropIndex).X : X(TimelineOps.TotalDuration(p));
            using var pen = new Pen(Accent, 3);
            g.DrawLine(pen, x, TrackTop - 4, x, TrackTop + TrackH + 4);
        }

        DrawAudioTrack(g, p);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        foreach (var (a, s, en, lane) in AnnotationBars()) DrawBar(g, a, BarRect(s, en, lane));

        // Playhead
        float px = X(_playhead());
        using (var pen = new Pen(Color.FromArgb(255, 70, 70), 2)) g.DrawLine(pen, px, 0, px, Height);
        using (var b = new SolidBrush(Color.FromArgb(255, 70, 70)))
            g.FillPolygon(b, new[] { new PointF(px - 6, 0), new PointF(px + 6, 0), new PointF(px, 9) });
    }

    void DrawRuler(Graphics g)
    {
        using var bg = new SolidBrush(Color.FromArgb(38, 38, 44));
        g.FillRectangle(bg, 0, 0, Width, RulerH);
        // Pick a label step that leaves ~80px between labels.
        double[] steps = { 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600 };
        double step = steps.FirstOrDefault(s => s * _pps >= 80, 600);
        double first = Math.Floor(_scrollX / _pps / step) * step;
        using var pen = new Pen(Color.FromArgb(90, 90, 100));
        for (double s = first; ; s += step)
        {
            float x = X(TimeSpan.FromSeconds(s));
            if (x > Width) break;
            g.DrawLine(pen, x, RulerH - 8, x, RulerH);
            float half = x + (float)(step * _pps / 2);
            g.DrawLine(pen, half, RulerH - 4, half, RulerH);
            TextRenderer.DrawText(g, FormatTime(TimeSpan.FromSeconds(s), step < 1), Font, new Point((int)x + 3, 4), Color.Silver);
        }
    }

    void DrawClip(Graphics g, Project p, int i)
    {
        var c = p.Timeline[i];
        var r = ClipRect(i);
        if (r.Right < 0 || r.Left > Width) return;
        var media = p.FindMedia(c.SourceId);
        var state = _library.GetState(c.SourceId);

        using (var b = new SolidBrush(ClipColor)) g.FillRectangle(b, r);

        // Tile the poster thumbnail along the clip.
        if (Thumb(c.SourceId, state.ThumbnailPath) is { } img)
        {
            var region = g.Clip;
            g.SetClip(r);
            float tw = TrackH * img.Width / (float)img.Height;
            for (float x = r.X; x < r.Right; x += tw) g.DrawImage(img, x, r.Y, tw, TrackH);
            g.Clip = region;
            using var shade = new SolidBrush(Color.FromArgb(90, ClipColor));
            g.FillRectangle(shade, r);
        }

        var label = media?.DisplayName ?? "(missing)";
        if (state.Status is ProxyStatus.Optimising or ProxyStatus.Queued) label += $"  ·  optimising {state.Progress:P0}";
        if (state.Status == ProxyStatus.Failed) label += "  ·  failed";
        if (media is { HasAudio: true })
        {
            if (c.Muted) label += "  ·  muted";
            else if (Math.Abs(c.Volume - 1) >= 0.005) label += $"  ·  vol {c.Volume:P0}";
        }
        TextRenderer.DrawText(g, label, Font, Rectangle.Round(new RectangleF(r.X + 6, r.Y + 4, r.Width - 12, 18)),
            Color.White, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        bool sel = SelectedClipId == c.Id;
        using var pen = new Pen(sel ? Accent : Color.FromArgb(20, 20, 24), sel ? 2.5f : 1.5f);
        g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
        // Trim grips
        using var grip = new SolidBrush(Color.FromArgb(sel ? 200 : 90, 255, 255, 255));
        g.FillRectangle(grip, r.X + 1, r.Y + TrackH / 2 - 10, 3, 20);
        g.FillRectangle(grip, r.Right - 4, r.Y + TrackH / 2 - 10, 3, 20);
    }

    void DrawAudioTrack(Graphics g, Project p)
    {
        var rows = AudioRows();
        using (var lane = new SolidBrush(Color.FromArgb(33, 33, 38))) g.FillRectangle(lane, 0, AudioTop, Width, AudioSectionH() - LaneSpacing);
        if (rows.Count == 0)
        {
            TextRenderer.DrawText(g, "♪  Audio track: import an MP3, WAV or M4A (Ctrl+I) for music or narration", Font,
                new Rectangle(8, AudioTop, Math.Max(10, Width - 16), AudioRowH), Color.Gray, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            return;
        }
        foreach (var (a, row) in rows) DrawAudio(g, p, a, AudioRect(a, row));
    }

    void DrawAudio(Graphics g, Project p, AudioItem a, RectangleF r)
    {
        if (r.Right < 0 || r.Left > Width) return;
        var media = p.FindMedia(a.SourceId);
        bool silent = a.EffectiveVolume <= 0;
        using (var b = new SolidBrush(silent ? Color.FromArgb(60, 70, 66) : AudioColor)) g.FillRectangle(b, r);

        // Waveform of the whole file, cropped to this item's in/out.
        if (media is { Duration.Ticks: > 0 } && Thumb(a.SourceId, _library.GetState(a.SourceId).ThumbnailPath) is { } wave)
        {
            float x0 = (float)(a.In.TotalSeconds / media.Duration.TotalSeconds * wave.Width);
            float w = (float)(a.Duration.TotalSeconds / media.Duration.TotalSeconds * wave.Width);
            var region = g.Clip;
            g.SetClip(r);
            using var attrs = new System.Drawing.Imaging.ImageAttributes();
            if (silent) attrs.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = 0.35f });
            g.DrawImage(wave, Rectangle.Round(new RectangleF(r.X, r.Y + 2, r.Width, r.Height - 4)), x0, 0, Math.Max(1, w), wave.Height, GraphicsUnit.Pixel, attrs);
            g.Clip = region;
        }

        // No text on audio items: just the waveform (greyed when muted). The name, times and volume
        // are in the hover tooltip and the Properties panel.
        bool sel = SelectedAudioId == a.Id;
        using var pen = new Pen(sel ? Accent : Color.FromArgb(20, 20, 24), sel ? 2.5f : 1.5f);
        g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
        using var grip = new SolidBrush(Color.FromArgb(sel ? 200 : 90, 255, 255, 255));
        g.FillRectangle(grip, r.X + 1, r.Y + AudioRowH / 2 - 7, 3, 14);
        g.FillRectangle(grip, r.Right - 4, r.Y + AudioRowH / 2 - 7, 3, 14);
    }

    void DrawBar(Graphics g, Annotation a, RectangleF r)
    {
        var col = a.Kind switch
        {
            AnnotationKind.Arrow => Color.FromArgb(214, 72, 72),
            AnnotationKind.Box => Color.FromArgb(200, 160, 30),
            _ => Color.FromArgb(120, 120, 135),
        };
        using var path = Rounded(r, 4);
        using (var b = new SolidBrush(col)) g.FillPath(b, path);
        if (SelectedAnnotationId is { } sel && (sel == a.Id || (a.LinkId is { } l && _session.Project.FindAnnotation(sel)?.LinkId == l)))
            using (var pen = new Pen(Accent, 2)) g.DrawPath(pen, path);
        var text = a.Kind == AnnotationKind.Text ? $"T  {a.Text.Split('\n')[0]}" : a.Kind == AnnotationKind.Arrow ? "→  Arrow" : "▭  Box";
        TextRenderer.DrawText(g, text, Font, Rectangle.Round(new RectangleF(r.X + 4, r.Y, r.Width - 6, r.Height)),
            Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    static GraphicsPath Rounded(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
        if (d < 1) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    Image? Thumb(Guid sourceId, string? path)
    {
        if (_thumbs.TryGetValue(sourceId, out var img)) return img;
        if (path is null || !File.Exists(path)) return null;
        // Load via a copy so the file isn't locked by GDI+.
        using var fs = File.OpenRead(path);
        using var tmp = Image.FromStream(fs);
        img = new Bitmap(tmp);
        _thumbs[sourceId] = img;
        return img;
    }

    public static string FormatTime(TimeSpan t, bool tenths = false) =>
        tenths ? $"{(int)t.TotalMinutes}:{t.Seconds:00}.{t.Milliseconds / 100}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    // ---------------- Mouse ----------------

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.Button == MouseButtons.Right && _session is not null)
        {
            if (AudioHit(e.Location) is { } hitAudio)
            {
                SelectAudio(hitAudio.A.Id);
                AudioMenuRequested?.Invoke(hitAudio.A.Id, PointToScreen(e.Location));
                return;
            }
            for (int i = 0; i < _session.Project.Timeline.Count; i++)
            {
                if (!ClipRect(i).Contains(e.Location)) continue;
                var id = _session.Project.Timeline[i].Id;
                SelectClip(id);
                ClipMenuRequested?.Invoke(id, PointToScreen(e.Location));
                return;
            }
            return;
        }
        if (e.Button != MouseButtons.Left || _session is null) return;
        var p = _session.Project;
        _downX = e.X;
        _dragMoved = false;

        // Annotation bars
        foreach (var (a, s, en, lane) in AnnotationBars())
        {
            var r = BarRect(s, en, lane);
            if (!r.Contains(e.Location)) continue;
            SelectAnnotation(a.Id);
            _dragId = a.Id;
            _originA = s;
            _originB = en;
            _drag = e.X <= r.Left + EdgeGrab ? Drag.AnnoStart : e.X >= r.Right - EdgeGrab ? Drag.AnnoEnd : Drag.AnnoMove;
            _session.BeginEdit();
            return;
        }

        // Audio items
        if (AudioHit(e.Location) is { } hit)
        {
            var a = hit.A;
            SelectAudio(a.Id);
            _dragId = a.Id;
            _originA = a.Start;
            _originB = e.X <= hit.R.Left + EdgeGrab ? a.In : a.Out;
            _drag = e.X <= hit.R.Left + EdgeGrab ? Drag.AudioIn : e.X >= hit.R.Right - EdgeGrab ? Drag.AudioOut : Drag.AudioMove;
            _session.BeginEdit();
            return;
        }

        // Clips
        for (int i = 0; i < p.Timeline.Count; i++)
        {
            var r = ClipRect(i);
            if (!r.Contains(e.Location)) continue;
            var c = p.Timeline[i];
            SelectClip(c.Id);
            _dragId = c.Id;
            _originA = c.In;
            _originB = c.Out;
            if (e.X <= r.Left + EdgeGrab) { _drag = Drag.TrimIn; _session.BeginEdit(); }
            else if (e.X >= r.Right - EdgeGrab) { _drag = Drag.TrimOut; _session.BeginEdit(); }
            else { _drag = Drag.ClipBody; SeekRequested?.Invoke(this, T(e.X)); }
            return;
        }

        // Ruler or empty space: scrub
        SelectedAudioId = null;
        SelectClip(null);
        SelectAnnotation(null);
        _drag = Drag.Scrub;
        SeekRequested?.Invoke(this, T(e.X));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_session is null) return;
        if (_drag == Drag.None) { UpdateCursor(e.Location); return; }

        var dt = TimeSpan.FromSeconds((e.X - _downX) / _pps);
        if (Math.Abs(e.X - _downX) > 3) _dragMoved = true;
        var id = _dragId;
        // A click is not a drag: nothing changes until the mouse has really moved (a stray 1 px move
        // used to "move" an annotation piece and break its link).
        if (!_dragMoved && _drag is not Drag.Scrub) return;

        switch (_drag)
        {
            case Drag.Scrub:
                SeekRequested?.Invoke(this, T(e.X));
                break;
            case Drag.ClipBody:
                if (_dragMoved) { _dropIndex = DropIndex(e.X); Invalidate(); }
                break;
            case Drag.TrimIn:
                _session.Preview(p => TimelineOps.TrimIn(p, id, _originA + dt));
                break;
            case Drag.TrimOut:
                _session.Preview(p => TimelineOps.TrimOut(p, id, _originB + dt));
                break;
            case Drag.AnnoMove:
                var len = _originB - _originA;
                _session.Preview(p => TimelineOps.MoveAnnotation(p, id, _originA + dt, len)); // fixed length; spans cuts as one run
                break;
            case Drag.AnnoStart:
                _session.Preview(p => TimelineOps.SetRunRange(p, id, _originA + dt, _originB));
                break;
            case Drag.AnnoEnd:
                _session.Preview(p => TimelineOps.SetRunRange(p, id, _originA, _originB + dt));
                break;
            case Drag.AudioMove:
                _session.Preview(p => TimelineOps.MoveAudio(p, id, _originA + dt));
                break;
            case Drag.AudioIn:
                // Absolute target (origin + delta), so repeated Preview steps don't accumulate.
                _session.Preview(p => TimelineOps.TrimAudioIn(p, id, _originB + dt));
                break;
            case Drag.AudioOut:
                _session.Preview(p => TimelineOps.TrimAudioOut(p, id, _originB + dt));
                break;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_drag == Drag.ClipBody && _dragMoved && _dropIndex >= 0)
        {
            var id = _dragId;
            int from = _session.Project.Timeline.FindIndex(c => c.Id == id);
            int to = _dropIndex > from ? _dropIndex - 1 : _dropIndex; // index after removal
            if (to != from) _session.Execute(p => TimelineOps.Move(p, id, to));
        }
        else if (_drag is Drag.TrimIn or Drag.TrimOut or Drag.AnnoMove or Drag.AnnoStart or Drag.AnnoEnd
                 or Drag.AudioMove or Drag.AudioIn or Drag.AudioOut)
        {
            _session.CommitEdit();
        }
        _drag = Drag.None;
        _dropIndex = -1;
        Invalidate();
    }

    int DropIndex(int x)
    {
        var p = _session.Project;
        for (int i = 0; i < p.Timeline.Count; i++)
        {
            var r = ClipRect(i);
            if (x < r.X + r.Width / 2) return i;
        }
        return p.Timeline.Count;
    }

    readonly ToolTip _tip = new() { InitialDelay = 300, ReshowDelay = 100, AutoPopDelay = 10000 };
    Guid? _tipFor;

    void UpdateCursor(Point pt)
    {
        var p = _session.Project;
        foreach (var (a, s, en, lane) in AnnotationBars())
        {
            var r = BarRect(s, en, lane);
            if (!r.Contains(pt)) continue;
            Cursor = pt.X <= r.Left + EdgeGrab || pt.X >= r.Right - EdgeGrab ? Cursors.SizeWE : Cursors.Hand;
            if (_tipFor != a.Id) { _tipFor = a.Id; _tip.SetToolTip(this, Summary(a, s, en)); }
            return;
        }
        if (AudioHit(pt) is { } hit)
        {
            Cursor = pt.X <= hit.R.Left + EdgeGrab || pt.X >= hit.R.Right - EdgeGrab ? Cursors.SizeWE : Cursors.Hand;
            if (_tipFor != hit.A.Id) { _tipFor = hit.A.Id; _tip.SetToolTip(this, AudioSummary(hit.A)); }
            return;
        }
        if (_tipFor is not null) { _tipFor = null; _tip.SetToolTip(this, null); }
        for (int i = 0; i < p.Timeline.Count; i++)
        {
            var r = ClipRect(i);
            if (r.Contains(pt)) { Cursor = pt.X <= r.Left + EdgeGrab || pt.X >= r.Right - EdgeGrab ? Cursors.SizeWE : Cursors.Hand; return; }
        }
        Cursor = Cursors.Default;
    }

    /// <summary>Hover text for an annotation bar: what it is, when, and which clip it's on.</summary>
    string Summary(Annotation a, TimeSpan start, TimeSpan end)
    {
        var p = _session.Project;
        var what = a.Kind switch
        {
            AnnotationKind.Text => $"Text: \"{(a.Text.Length > 40 ? a.Text[..40] + "…" : a.Text)}\"",
            AnnotationKind.Box => a.FillColor is null ? "Box" : "Box (filled)",
            _ => "Arrow",
        };
        var clip = p.FindClip(a.ClipId) is { } c && p.FindMedia(c.SourceId) is { } m ? m.DisplayName : "?";
        var pieces = TimelineOps.LinkedPieces(p, a);
        var part = pieces.Count > 1 ? $"\nRuns across {pieces.Count} clips" : "";
        return $"{what}\n{FormatTime(start, tenths: true)} – {FormatTime(end, tenths: true)}  ({(end - start).TotalSeconds:0.0}s)\nOn: {clip}{part}";
    }

    /// <summary>The audio item under a point (the last drawn wins), with its rectangle.</summary>
    (AudioItem A, RectangleF R)? AudioHit(Point pt)
    {
        foreach (var (a, row) in AudioRows().AsEnumerable().Reverse())
        {
            var r = AudioRect(a, row);
            if (r.Contains(pt)) return (a, r);
        }
        return null;
    }

    string AudioSummary(AudioItem a)
    {
        var name = _session.Project.FindMedia(a.SourceId)?.DisplayName ?? "?";
        var vol = a.Muted ? "muted" : $"volume {a.Volume:P0}";
        return $"♪ {name}\n{FormatTime(a.Start, tenths: true)} – {FormatTime(a.End, tenths: true)}  ({a.Duration.TotalSeconds:0.0}s, {vol})\n" +
               "Drag to move · drag ends to trim · right-click for more";
    }

    void SelectAudio(Guid? id)
    {
        SelectedAudioId = id;
        if (id is not null) { SelectedClipId = null; SelectedAnnotationId = null; }
        AudioSelected?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    void SelectClip(Guid? id)
    {
        SelectedClipId = id;
        if (id is not null) { SelectedAnnotationId = null; SelectedAudioId = null; }
        ClipSelected?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    void SelectAnnotation(Guid? id)
    {
        SelectedAnnotationId = id;
        if (id is not null) { SelectedClipId = null; SelectedAudioId = null; }
        AnnotationSelected?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) foreach (var img in _thumbs.Values) img.Dispose();
        base.Dispose(disposing);
    }
}
