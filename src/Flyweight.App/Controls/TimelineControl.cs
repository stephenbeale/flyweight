using System.Drawing.Drawing2D;
using Flyweight.Core.Editing;
using Flyweight.Core.Media;
using Flyweight.Core.Model;

namespace Flyweight.App.Controls;

/// <summary>
/// Owner-drawn timeline: annotation lanes and overlay tracks (V2, V3…) in a scrollable area at the top, the main
/// clip track and audio track pinned at the bottom (they're the core of the edit, so they never scroll away).
///
///   ruler       click/drag to scrub
///   empty space drag a box to select everything it touches (Shift/Ctrl adds); a click clears and seeks
///   overlays    drag to move in time and between tracks, drag ends to trim (they stay at their timeline time)
///   clip track  click to select + seek, drag body to reorder, drag edges to trim; gaps (left by Delete) too
///   audio track drag to move in time, drag ends to trim (items stay at their timeline time)
///   annotations drag bar to move in time, drag ends to change duration
///   wheel       scroll; Ctrl+wheel zoom around the mouse
/// </summary>
public sealed class TimelineControl : Control
{
    const int RulerH = 24, TrackH = 64, LaneGap = 8, LaneH = 18, LaneSpacing = 3, EdgeGrab = 7;
    const int AudioGap = 6;

    /// <summary>Scales with Windows display scaling, like the text in the other lanes.</summary>
    int AudioRowH => Math.Max(30, (int)(Font.Height * 1.8));
    int OverlayRowH => AudioRowH + 4;

    // Layout, top to bottom:
    //   ruler                                   fixed
    //   annotation lanes (lane 0 lowest)        ┐ scroll region: scrolls vertically when it doesn't fit,
    //   overlay tracks (V2 lowest, V3 above…)   ┘ bottom-anchored so V2 always sits right above the video
    //   main video track                        ┐ pinned to the bottom: the core of the edit never scrolls away
    //   audio track                             ┘

    int RegionTop => RulerH + 2;

    /// <summary>Height of the pinned video + audio area at the bottom.</summary>
    int BottomH => TrackH + AudioGap + AudioSectionH() + 2;

    /// <summary>The main video track, pinned near the bottom of the control.</summary>
    int TrackTop => Math.Max(RegionTop + 4, ClientSize.Height - BottomH);

    int RegionBottom => TrackTop - 4;

    int OverlayBlockH => OverlayRows() * (OverlayRowH + LaneSpacing);

    /// <summary>Smallest an annotation lane gets squeezed to (its label is hidden below <see cref="LabelMinLaneH"/>).</summary>
    const int MinLaneH = 10, LabelMinLaneH = 14;

    int LaneCount() => AnnotationBars().Select(b => b.Lane + 1).DefaultIfEmpty(0).Max();

    /// <summary>
    /// Annotation lane height right now. Full height while everything fits; once the panel can't grow any more
    /// (the form caps it), lanes are squeezed down to <see cref="MinLaneH"/> so every annotation stays in view, and
    /// only past that does the region scroll (owner: "I need to see all annotations").
    /// </summary>
    int CurLaneH
    {
        get
        {
            int lanes = LaneCount();
            if (lanes == 0) return LaneH;
            int room = RegionBottom - RegionTop - OverlayBlockH - LaneGap;
            return Math.Clamp(room / lanes - LaneSpacing, MinLaneH, LaneH);
        }
    }

    /// <summary>Unscrolled height of the scroll region's content (annotation lanes + overlay tracks) at a lane height.</summary>
    int ContentH(int laneH)
    {
        int lanes = LaneCount();
        return lanes * (laneH + LaneSpacing) + (lanes > 0 ? LaneGap : 0) + OverlayBlockH;
    }

    int ContentH() => ContentH(CurLaneH);

    bool InRegion(Point pt) => pt.Y >= RegionTop && pt.Y <= RegionBottom;
    static readonly Color ClipColor = Color.FromArgb(52, 99, 160);
    static readonly Color AudioColor = Color.FromArgb(36, 104, 80);
    static readonly Color OverlayColor = Color.FromArgb(112, 72, 160);
    static readonly Color Accent = Theme.Accent;

    ProjectSession _session = null!;
    MediaLibrary _library = null!;
    Func<TimeSpan> _playhead = () => TimeSpan.Zero;
    readonly Dictionary<Guid, Image> _thumbs = new();

    double _pps = 40;       // pixels per second
    float _scrollX;         // pixels
    int _scrollY;           // pixels scrolled UP from the bottom of the scroll region (0 = overlays and lowest lanes showing)
    // Placed over the scroll region only (not docked), so it never covers the ruler or the pinned tracks' trim grips.
    readonly VScrollBar _vScroll = new() { Visible = false, SmallChange = 20 };

    /// <summary>Top of the overlay tracks: bottom-anchored in the scroll region, moved down as you scroll up.</summary>
    int OverlayTop => RegionBottom - OverlayBlockH + LaneSpacing + _scrollY;

    enum Drag { None, Scrub, ClipBody, TrimIn, TrimOut, AnnoMove, AnnoStart, AnnoEnd, AudioMove, AudioIn, AudioOut, Marker, OverlayMove, OverlayIn, OverlayOut, Marquee }
    static readonly Color MarkerColor = Color.FromArgb(255, 196, 0);
    Drag _drag;
    int _downX, _downY;
    /// <summary>Drag-select box: where it started, where the mouse is now, and the selection to add to (Shift/Ctrl).</summary>
    Point _marqueeFrom, _marqueeTo;
    (HashSet<Guid> Clips, HashSet<Guid> Audio, HashSet<Guid> Overlays, HashSet<Guid> Annotations)? _marqueeBase;
    bool _dragMoved;
    Guid _dragId;
    TimeSpan _originA, _originB;
    TimelineOps.TrackSnapshot? _trackSnap; // overlay/audio tracks when a clip-edge drag began, so they stay in sync
    int _dropIndex = -1;
    /// <summary>Clip-body drag past the end of the other clips: the time it will start at (a gap fills the space before it).</summary>
    TimeSpan? _dropTime;
    TimeSpan _dragClipStart;

    public TimelineControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Color.FromArgb(28, 28, 32);
        ForeColor = Color.Gainsboro;
        // The bar's top is the top of the content, so its value counts DOWN from the top (our scroll counts up).
        _vScroll.Scroll += (_, _) => { _scrollY = MaxScrollY() - _vScroll.Value; Invalidate(); };
        Controls.Add(_vScroll);
    }

    // ---------------- Vertical scroll / preferred height ----------------

    int MaxScrollY() => Math.Max(0, ContentH() - (RegionBottom - RegionTop));

    /// <summary>Height that shows everything without scrolling. The form grows the panel towards it (up to a limit).</summary>
    public int PreferredHeight => RegionTop + ContentH(LaneH) + 4 + BottomH; // full-height lanes: grow before squeezing

    /// <summary>
    /// Raised when a timeline drag ends, since the form doesn't refit the panel mid-drag (<see cref="IsDragging"/>).
    /// Edits refit straight away through the session's Changed event.
    /// </summary>
    public event EventHandler? PreferredHeightChanged;

    /// <summary>A mouse drag on the timeline is in progress: hold the panel's size until it ends.</summary>
    public bool IsDragging => _drag != Drag.None;

    void SetScrollY(int y)
    {
        _scrollY = Math.Clamp(y, 0, MaxScrollY());
        Invalidate();
    }

    /// <summary>Show the scrollbar only when the lanes overflow, and keep the scroll in range as content shrinks.</summary>
    void SyncVScroll()
    {
        int max = MaxScrollY();
        if (_scrollY > max) _scrollY = max;
        bool show = max > 0;
        if (_vScroll.Visible != show) _vScroll.Visible = show;
        if (!show) return;
        int page = Math.Max(1, RegionBottom - RegionTop);
        int w = SystemInformation.VerticalScrollBarWidth;
        var bounds = new Rectangle(ClientSize.Width - w, RegionTop, w, page);
        if (_vScroll.Bounds != bounds) _vScroll.Bounds = bounds;
        _vScroll.Minimum = 0;
        _vScroll.LargeChange = page;
        _vScroll.Maximum = max + page - 1; // WinForms: the reachable value is Maximum - LargeChange + 1
        _vScroll.Value = Math.Clamp(max - _scrollY, 0, max);
    }

    /// <summary>Scroll so an annotation's bar is on screen (e.g. one just added in the preview).</summary>
    public void RevealAnnotation(Guid id)
    {
        if (_session is null) return;
        var a = _session.Project.FindAnnotation(id);
        if (a is null) return;
        var first = TimelineOps.LinkedPieces(_session.Project, a)[0].Id;
        foreach (var (bar, s, e, lane) in AnnotationBars())
        {
            if (bar.Id != first) continue;
            var r = BarRect(s, e, lane);
            // Scrolling UP (bigger _scrollY) moves content down, bringing higher lanes into view.
            if (r.Top < RegionTop) SetScrollY(_scrollY + (int)Math.Ceiling(RegionTop - r.Top) + 4);
            else if (r.Bottom > RegionBottom) SetScrollY(_scrollY - (int)Math.Ceiling(r.Bottom - RegionBottom) - 4);
            // Only scroll sideways when the bar is entirely off screen (a long bar partly in view stays put).
            if (r.Right < 0 || r.Left > ClientSize.Width - 20) _scrollX = Math.Max(0, _scrollX + r.X - ClientSize.Width * 0.2f);
            Invalidate();
            return;
        }
    }

    Guid? _primaryClip;
    readonly HashSet<Guid> _selectedClips = new();

    /// <summary>
    /// The MAIN selected clip (the one the Properties panel shows). Setting it selects just that clip; use
    /// Ctrl/Shift+click or <see cref="SelectAllClips"/> for more.
    /// </summary>
    public Guid? SelectedClipId
    {
        get => _primaryClip;
        set
        {
            _primaryClip = value;
            _selectedClips.Clear();
            if (value is { } v) _selectedClips.Add(v);
        }
    }

    /// <summary>Every selected clip (includes <see cref="SelectedClipId"/>). Edits in the Properties panel apply to all.</summary>
    public IReadOnlyCollection<Guid> SelectedClipIds => _selectedClips;

    /// <summary>Ctrl+A: select every clip on the timeline (the one under the playhead stays the main one).</summary>
    public void SelectAllClips(Guid? main)
    {
        var all = _session.Project.Timeline.Select(c => c.Id).ToList();
        if (all.Count == 0) return;
        SelectedAnnotationId = null;
        SelectedAudioId = null;
        SelectedMarkerId = null;
        SelectedOverlayId = null;
        _primaryClip = main is { } m && all.Contains(m) ? m : all[0];
        _selectedClips.Clear();
        _selectedClips.UnionWith(all);
        ClipSelected?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    /// <summary>Select exactly these clips (e.g. just pasted); the first becomes the main one.</summary>
    public void SelectClips(IEnumerable<Guid> ids)
    {
        var list = ids.ToList();
        if (list.Count == 0) return;
        SelectedAnnotationId = null;
        SelectedAudioId = null;
        SelectedMarkerId = null;
        SelectedOverlayId = null;
        _primaryClip = list[0];
        _selectedClips.Clear();
        _selectedClips.UnionWith(list);
        ClipSelected?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    /// <summary>Forget selected items that no longer exist (after delete / undo).</summary>
    public void PruneSelection(Project p)
    {
        _selectedClips.RemoveWhere(id => p.FindClip(id) is null);
        if (_primaryClip is { } pc && p.FindClip(pc) is null) _primaryClip = _selectedClips.Count > 0 ? _selectedClips.First() : null;
        _selAudio.RemoveWhere(id => p.FindAudio(id) is null);
        if (_primaryAudio is { } pa && p.FindAudio(pa) is null) _primaryAudio = null;
        _selOverlays.RemoveWhere(id => p.FindOverlay(id) is null);
        if (_primaryOverlay is { } po && p.FindOverlay(po) is null) _primaryOverlay = null;
        _selAnnotations.RemoveWhere(id => p.FindAnnotation(id) is null);
        if (_primaryAnnotation is { } pn && p.FindAnnotation(pn) is null) _primaryAnnotation = null;
    }

    // One "main" item per kind (what the Properties panel shows; setting it selects just that one), plus everything a
    // drag-select box picked up (the *Ids sets, which include the main one). Delete acts on all of them.
    Guid? _primaryAudio, _primaryOverlay, _primaryAnnotation;
    readonly HashSet<Guid> _selAudio = new(), _selOverlays = new(), _selAnnotations = new();

    static void SetOne(ref Guid? primary, HashSet<Guid> set, Guid? value)
    {
        primary = value;
        set.Clear();
        if (value is { } v) set.Add(v);
    }

    public Guid? SelectedAnnotationId { get => _primaryAnnotation; set => SetOne(ref _primaryAnnotation, _selAnnotations, value); }
    public Guid? SelectedAudioId { get => _primaryAudio; set => SetOne(ref _primaryAudio, _selAudio, value); }
    public Guid? SelectedOverlayId { get => _primaryOverlay; set => SetOne(ref _primaryOverlay, _selOverlays, value); }
    public Guid? SelectedMarkerId { get; set; }

    /// <summary>Every selected audio item / overlay / annotation (a drag-select box can pick up several).</summary>
    public IReadOnlyCollection<Guid> SelectedAudioIds => _selAudio;
    public IReadOnlyCollection<Guid> SelectedOverlayIds => _selOverlays;
    public IReadOnlyCollection<Guid> SelectedAnnotationIds => _selAnnotations;

    /// <summary>A drag-select box on the timeline finished (the selection may mix clips, overlays, audio and annotations).</summary>
    public event EventHandler? MarqueeSelected;

    /// <summary>How many things are selected on the timeline, of every kind.</summary>
    public int SelectionCount => _selectedClips.Count + _selAudio.Count + _selOverlays.Count + _selAnnotations.Count;

    public event EventHandler? OverlaySelected;

    /// <summary>Right-click on an overlay (already selected): its id and screen position.</summary>
    public event Action<Guid, Point>? OverlayMenuRequested;

    /// <summary>Select an overlay (e.g. clicked in the preview); null clears it.</summary>
    public void SelectOverlay(Guid? id)
    {
        SelectedOverlayId = id;
        if (id is not null) { SelectedClipId = null; SelectedAnnotationId = null; SelectedAudioId = null; SelectedMarkerId = null; }
        OverlaySelected?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    public event EventHandler<TimeSpan>? SeekRequested;
    public event EventHandler? ClipSelected;
    public event EventHandler? AnnotationSelected;
    public event EventHandler? AudioSelected;

    /// <summary>Right-click on a clip (already selected): clip id and screen position for a context menu.</summary>
    public event Action<Guid, Point>? ClipMenuRequested;

    /// <summary>Right-click on an audio item (already selected): its id and screen position.</summary>
    public event Action<Guid, Point>? AudioMenuRequested;

    public event EventHandler? MarkerSelected;

    /// <summary>Double-click on a marker flag: the form asks for a new title.</summary>
    public event EventHandler<Guid>? MarkerRenameRequested;

    public void Bind(ProjectSession session, MediaLibrary library, Func<TimeSpan> playhead)
    {
        _session = session;
        _library = library;
        _playhead = playhead;
    }

    // ---------------- Geometry ----------------

    float X(TimeSpan t) => (float)(t.TotalSeconds * _pps) - _scrollX;
    TimeSpan T(float x) => TimeSpan.FromSeconds(Math.Max(0, (x + _scrollX) / _pps));
    /// <summary>Annotation lanes stack UPWARDS above the overlay tracks: lane 0 is just above V2.</summary>
    int LaneTop(int lane) => OverlayTop - LaneGap - (lane + 1) * (CurLaneH + LaneSpacing) + LaneSpacing;

    // ---- Overlay tracks: one row per track above the main track, the highest track at the top ----

    /// <summary>Track a drag is hovering over, so a row appears for a NEW track while dragging above the top one.</summary>
    int _dragTrack;

    int OverlayRows()
    {
        int used = _session.Project.Overlays.Select(o => o.Track).DefaultIfEmpty(1).Max();
        return Math.Clamp(Math.Max(used, _dragTrack), 1, TimelineOps.MaxOverlayTracks);
    }

    float OverlayRowTop(int track) => OverlayTop + (OverlayRows() - track) * (OverlayRowH + LaneSpacing);

    RectangleF OverlayRect(OverlayClip o) =>
        new(X(o.Start), OverlayRowTop(o.Track), Math.Max(4, (float)(o.Duration.TotalSeconds * _pps)), OverlayRowH);

    /// <summary>The overlay track at height <paramref name="y"/>; above the top row means "a new track on top".</summary>
    int TrackAtY(int y)
    {
        int rows = OverlayRows();
        if (y < OverlayTop)
        {
            // One above the highest OTHER overlay: stable while the rows grow under the pointer.
            int others = _session.Project.Overlays.Where(o => o.Id != _dragId).Select(o => o.Track).DefaultIfEmpty(0).Max();
            return Math.Clamp(others + 1, 1, TimelineOps.MaxOverlayTracks);
        }
        int row = (y - OverlayTop) / (OverlayRowH + LaneSpacing);
        return Math.Clamp(rows - row, 1, TimelineOps.MaxOverlayTracks);
    }

    (OverlayClip O, RectangleF R)? OverlayHit(Point pt)
    {
        if (!InRegion(pt)) return null; // scrolled out of view
        foreach (var o in Enumerable.Reverse(_session.Project.Overlays))
        {
            var r = OverlayRect(o);
            if (r.Contains(pt)) return (o, r);
        }
        return null;
    }

    // ---- Audio track: rows under the video track (overlapping items stack) ----

    int AudioTop => TrackTop + TrackH + AudioGap;

    /// <summary>Audio items with a row each, so overlapping items don't hide each other.</summary>
    // Layout lists reused for the duration of ONE paint (geometry asks for them per bar/clip/overlay; without this a
    // repaint re-sorted the audio track and rebuilt the annotation runs dozens of times, ~60×/s while playing).
    List<(AudioItem A, int Row)>? _audioRowsCache;
    List<(Annotation A, TimeSpan S, TimeSpan E, int Lane)>? _barsCache;

    List<(AudioItem A, int Row)> AudioRows() => _audioRowsCache ?? ComputeAudioRows();

    /// <summary>How close (on screen) an audio edge has to be to another edge or a cut to stick to it while dragging.</summary>
    const int SnapPx = 10;
    TimeSpan SnapTolerance => TimeSpan.FromSeconds(SnapPx / _pps);

    /// <summary>Items overlapping by less than this on screen still share a row (they look like they touch).</summary>
    const int RowOverlapPx = 6;

    List<(AudioItem A, int Row)> ComputeAudioRows()
    {
        var rowEnds = new List<TimeSpan>();
        var result = new List<(AudioItem, int)>();
        var slack = TimeSpan.FromSeconds(RowOverlapPx / _pps);
        foreach (var a in _session.Project.AudioTrack.OrderBy(a => a.Start))
        {
            int row = rowEnds.FindIndex(end => end - slack <= a.Start);
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

    /// <summary>Clip start times for ONE paint (ClipStart walks the timeline; per clip that was n² with many freeze frames).</summary>
    TimeSpan[]? _clipStartsCache;

    static TimeSpan[] ComputeClipStarts(Project p)
    {
        var starts = new TimeSpan[p.Timeline.Count];
        var t = TimeSpan.Zero;
        for (int i = 0; i < starts.Length; i++) { starts[i] = t; t += p.Timeline[i].Duration; }
        return starts;
    }

    RectangleF ClipRect(int index)
    {
        var p = _session.Project;
        var start = _clipStartsCache is { } cs && index < cs.Length ? cs[index] : TimelineOps.ClipStart(p, index);
        var c = p.Timeline[index];
        return new RectangleF(X(start), TrackTop, (float)(c.Duration.TotalSeconds * _pps), TrackH);
    }

    /// <summary>
    /// One bar per annotation (a run across cuts is ONE bar, represented by its first piece), with its
    /// timeline range and a lane assigned so bars don't overlap.
    /// </summary>
    List<(Annotation A, TimeSpan S, TimeSpan E, int Lane)> AnnotationBars() => _barsCache ?? ComputeAnnotationBars();

    List<(Annotation A, TimeSpan S, TimeSpan E, int Lane)> ComputeAnnotationBars()
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
        new(X(s), LaneTop(lane), Math.Max(4, (float)((e - s).TotalSeconds * _pps)), CurLaneH);

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
        else if ((ModifierKeys & Keys.Shift) != 0)
        {
            SetScrollY(_scrollY + e.Delta / 3); // Shift+wheel: wheel up shows the higher annotation lanes
            return;
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
        _audioRowsCache = ComputeAudioRows();
        _barsCache = ComputeAnnotationBars();
        _clipStartsCache = ComputeClipStarts(_session.Project);
        try { PaintTimeline(g, _session.Project); }
        finally { _audioRowsCache = null; _barsCache = null; _clipStartsCache = null; } // hit-testing between paints computes fresh
    }

    void PaintTimeline(Graphics g, Project p)
    {
        SyncVScroll();

        // Scroll region (annotation lanes + overlay tracks), clipped between the ruler and the pinned video track.
        g.SetClip(new Rectangle(0, RulerH, Width, Math.Max(0, RegionBottom + 1 - RulerH)));
        DrawOverlayTracks(g, p);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        foreach (var (a, s, en, lane) in AnnotationBars()) DrawBar(g, a, BarRect(s, en, lane));
        g.SmoothingMode = SmoothingMode.Default;
        g.ResetClip();

        // Pinned: main video track and audio track.
        for (int i = 0; i < p.Timeline.Count; i++) DrawClip(g, p, i);

        if (_drag == Drag.ClipBody && _dragMoved && _dropTime is { } dropAt && p.FindClip(_dragId) is { } moving)
        {
            // Ghost of the clip where it will land, and a dashed line from the end of the others (the gap it leaves).
            var ghost = new RectangleF(X(dropAt), TrackTop, (float)(moving.Duration.TotalSeconds * _pps), TrackH);
            using var fill = new SolidBrush(Color.FromArgb(60, Accent));
            g.FillRectangle(fill, ghost);
            using var pen = new Pen(Accent, 2) { DashStyle = DashStyle.Dash };
            g.DrawRectangle(pen, ghost.X, ghost.Y, ghost.Width, ghost.Height);
            var gapFrom = X(TimelineOps.EndWithout(p, _dragId));
            if (ghost.X - gapFrom > 30)
                TextRenderer.DrawText(g, $"gap {(dropAt - TimelineOps.EndWithout(p, _dragId)).TotalSeconds:0.0}s", Font,
                    Rectangle.Round(new RectangleF(gapFrom, TrackTop + TrackH / 2 - 9, ghost.X - gapFrom, 18)), Color.Silver,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
        else if (_drag == Drag.ClipBody && _dragMoved && _dropIndex >= 0)
        {
            float x = _dropIndex < p.Timeline.Count ? ClipRect(_dropIndex).X : X(TimelineOps.TotalDuration(p));
            using var pen = new Pen(Accent, 3);
            g.DrawLine(pen, x, TrackTop - 4, x, TrackTop + TrackH + 4);
        }

        DrawAudioTrack(g, p);

        if (_drag == Drag.Marquee && _dragMoved)
        {
            var box = MarqueeRect();
            using var fill = new SolidBrush(Color.FromArgb(40, Accent));
            g.FillRectangle(fill, box);
            using var pen = new Pen(Accent, 1) { DashStyle = DashStyle.Dash };
            g.DrawRectangle(pen, box.X, box.Y, box.Width, box.Height);
        }

        DrawRuler(g);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        foreach (var (m, t) in MarkerOps.Visible(p)) DrawMarker(g, m, t);

        // Playhead
        float px = X(_playhead());
        using (var pen = new Pen(Color.FromArgb(255, 70, 70), 2)) g.DrawLine(pen, px, 0, px, Height);
        using (var b = new SolidBrush(Color.FromArgb(255, 70, 70)))
            g.FillPolygon(b, new[] { new PointF(px - 6, 0), new PointF(px + 6, 0), new PointF(px, 9) });
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_session is not null) SetScrollY(_scrollY); // re-clamp: a taller panel may need less scroll
    }

    /// <summary>Chapter marker: a flag on the ruler with its title, and a thin line down through the track.</summary>
    void DrawMarker(Graphics g, Marker m, TimeSpan t)
    {
        float x = X(t);
        if (x < -200 || x > Width + 10) return;
        bool sel = SelectedMarkerId == m.Id;
        using (var line = new Pen(Color.FromArgb(sel ? 220 : 120, MarkerColor), sel ? 2 : 1) { DashStyle = DashStyle.Dot })
            g.DrawLine(line, x, RulerH, x, TrackTop + TrackH);
        var flag = MarkerFlag(m, t);
        using (var b = new SolidBrush(sel ? MarkerColor : Color.FromArgb(225, MarkerColor)))
            g.FillRectangle(b, flag);
        if (sel)
            using (var pen = new Pen(Color.White, 1.5f)) g.DrawRectangle(pen, flag.X, flag.Y, flag.Width, flag.Height);
        TextRenderer.DrawText(g, m.Title, Font, Rectangle.Round(new RectangleF(flag.X + 3, flag.Y, flag.Width - 4, flag.Height)),
            Color.Black, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    RectangleF MarkerFlag(Marker m, TimeSpan t)
    {
        var width = Math.Min(140, TextRenderer.MeasureText(m.Title, Font).Width + 8);
        return new RectangleF(X(t), 2, width, RulerH - 6);
    }

    (Marker Marker, TimeSpan Time)? MarkerAt(Point pt)
    {
        if (pt.Y > RulerH) return null;
        // Topmost (latest drawn) first.
        foreach (var (m, t) in Enumerable.Reverse(MarkerOps.Visible(_session.Project)))
        {
            var r = MarkerFlag(m, t);
            r.Inflate(3, 2);
            if (r.Contains(pt)) return (m, t);
        }
        return null;
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
        r = Gap(r); // a hair of space between clips so the rounded ends read as separate clips
        if (c.IsGap) { DrawGap(g, c, r); return; }
        var media = p.FindMedia(c.SourceId);
        var state = _library.GetState(c.SourceId);
        using var shape = Theme.Rounded(r);

        Theme.FillRounded(g, ClipColor, r);

        // Tile the poster thumbnail along the clip, inside its rounded outline.
        if (Thumb(c.SourceId, state.ThumbnailPath) is { } img)
        {
            var region = g.Clip;
            g.SetClip(shape, CombineMode.Intersect);
            TileThumb(g, c.SourceId, img, r, TrackH);
            using var shade = new SolidBrush(Color.FromArgb(90, ClipColor));
            g.FillRectangle(shade, r);
            // Darker strip behind the label so it stays readable on bright thumbnails.
            using var strip = new LinearGradientBrush(new RectangleF(r.X, r.Y, r.Width, 26), Color.FromArgb(150, 10, 14, 22), Color.FromArgb(0, 10, 14, 22), LinearGradientMode.Vertical);
            g.FillRectangle(strip, r.X, r.Y, r.Width, 26);
            g.Clip = region;
        }

        var label = media?.DisplayName ?? "(missing)";
        if (Math.Abs(c.Speed - 1) > 0.0005) label = $"{c.Speed:0.##}×  ·  {label}";
        if (state.Status is ProxyStatus.Optimising or ProxyStatus.Queued) label += $"  ·  optimising {state.Progress:P0}";
        if (state.Status == ProxyStatus.Failed) label += "  ·  failed";
        if (media is { HasAudio: true })
        {
            if (c.Muted && TimelineOps.UnlinkedSoundOf(p, c.Id).Count > 0) label += "  ·  sound unlinked";
            else if (c.Muted) label += "  ·  muted";
            else if (Math.Abs(c.Volume - 1) >= 0.005) label += $"  ·  vol {c.Volume:P0}";
        }
        TextRenderer.DrawText(g, label, Font, Rectangle.Round(new RectangleF(r.X + 6, r.Y + 4, r.Width - 12, 18)),
            Color.White, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        bool sel = _selectedClips.Contains(c.Id);
        if (sel) Theme.DrawRounded(g, Accent, 2.5f, r);
        else Theme.DrawRounded(g, Color.FromArgb(70, 255, 255, 255), 1f, r);
        Grips(g, r, TrackH, 20, sel);
    }

    /// <summary>A gap on the main track: an empty dashed slot (black in the video), selectable so Delete can close it.</summary>
    void DrawGap(Graphics g, Clip c, RectangleF r)
    {
        bool sel = _selectedClips.Contains(c.Id);
        Theme.FillRounded(g, Color.FromArgb(sel ? 40 : 24, 255, 255, 255), r);
        using (var pen = new Pen(sel ? Accent : Color.FromArgb(110, 255, 255, 255), sel ? 2.5f : 1f) { DashStyle = DashStyle.Dash })
        {
            using var shape = Theme.Rounded(r);
            g.DrawPath(pen, shape);
        }
        TextRenderer.DrawText(g, $"Gap  ·  {c.Duration.TotalSeconds:0.0}s", Font, Rectangle.Round(new RectangleF(r.X + 6, r.Y + 4, r.Width - 12, 18)),
            Color.FromArgb(170, 170, 180), TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        Grips(g, r, TrackH, 20, sel);
    }

    /// <summary>
    /// Width of the trim handle at each end: <see cref="EdgeGrab"/>, but at most a quarter of the item, so a narrow one
    /// (a short freeze frame when zoomed out) still has a middle to drag it by instead of being all trim handle.
    /// </summary>
    static float Grab(RectangleF r) => Math.Min(EdgeGrab, r.Width / 4);

    static RectangleF Gap(RectangleF r) => r.Width > 6 ? new RectangleF(r.X + 1, r.Y, r.Width - 2, r.Height) : r;

    /// <summary>Rounded trim handles at both ends.</summary>
    static void Grips(Graphics g, RectangleF r, float h, float len, bool sel)
    {
        var c = Color.FromArgb(sel ? 220 : 110, 255, 255, 255);
        Theme.FillRounded(g, c, new RectangleF(r.X + 3, r.Y + h / 2 - len / 2, 3, len), 1.5f);
        Theme.FillRounded(g, c, new RectangleF(r.Right - 6, r.Y + h / 2 - len / 2, 3, len), 1.5f);
    }

    void DrawOverlayTracks(Graphics g, Project p)
    {
        int rows = OverlayRows();
        using (var lane = new SolidBrush(Color.FromArgb(33, 33, 38)))
            for (int t = 1; t <= rows; t++) g.FillRectangle(lane, 0, OverlayRowTop(t), Width, OverlayRowH);
        if (p.Overlays.Count == 0)
        {
            TextRenderer.DrawText(g, "▣  Overlay track: right-click a file in Media → \"Add as overlay at playhead\" for picture-in-picture or a cutaway",
                Font, new Rectangle(8, (int)OverlayRowTop(1), Math.Max(10, Width - 16), OverlayRowH), Color.Gray,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            return;
        }
        foreach (var o in p.Overlays.OrderBy(o => o.Track)) DrawOverlay(g, p, o, OverlayRect(o));
    }

    void DrawOverlay(Graphics g, Project p, OverlayClip o, RectangleF r)
    {
        if (r.Right < 0 || r.Left > Width) return;
        var media = p.FindMedia(o.SourceId);
        var state = _library.GetState(o.SourceId);
        r = Gap(r);
        using var shape = Theme.Rounded(r);
        Theme.FillRounded(g, OverlayColor, r);
        if (Thumb(o.SourceId, state.ThumbnailPath) is { } img)
        {
            var region = g.Clip;
            g.SetClip(shape, CombineMode.Intersect);
            TileThumb(g, o.SourceId, img, r, (int)r.Height);
            using var shade = new SolidBrush(Color.FromArgb(120, OverlayColor));
            g.FillRectangle(shade, r);
            g.Clip = region;
        }
        var label = $"V{o.Track + 1}  ·  {media?.DisplayName ?? "(missing)"}";
        if (Math.Abs(o.Speed - 1) > 0.0005) label = $"{o.Speed:0.##}×  ·  {label}";
        if (o.Scale >= 0.99) label += "  ·  full frame";
        if (state.Status is ProxyStatus.Optimising or ProxyStatus.Queued) label += $"  ·  optimising {state.Progress:P0}";
        if (media is { HasAudio: true } && o.Muted) label += "  ·  muted";
        TextRenderer.DrawText(g, label, Font, Rectangle.Round(new RectangleF(r.X + 6, r.Y, r.Width - 12, r.Height)),
            Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        bool sel = _selOverlays.Contains(o.Id);
        if (sel) Theme.DrawRounded(g, Accent, 2.5f, r);
        else Theme.DrawRounded(g, Color.FromArgb(70, 255, 255, 255), 1f, r);
        Grips(g, r, r.Height, 14, sel);
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
        r = Gap(r);
        using var shape = Theme.Rounded(r);
        Theme.FillRounded(g, silent ? Color.FromArgb(60, 70, 66) : AudioColor, r);

        if (media is { IsAudioOnly: false })
        {
            // Sound separated from a video clip: its "thumbnail" is the poster frame, not a waveform, so name it instead.
            TextRenderer.DrawText(g, $"🔊  {media.DisplayName}", Font, Rectangle.Round(new RectangleF(r.X + 6, r.Y, r.Width - 12, r.Height)),
                silent ? Color.Gray : Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
        // Waveform of the whole file, cropped to this item's in/out.
        else if (media is { Duration.Ticks: > 0 } && Thumb(a.SourceId, _library.GetState(a.SourceId).ThumbnailPath) is { } wave)
        {
            float x0 = (float)(a.In.TotalSeconds / media.Duration.TotalSeconds * wave.Width);
            float w = (float)(a.Duration.TotalSeconds / media.Duration.TotalSeconds * wave.Width);
            var region = g.Clip;
            g.SetClip(shape, CombineMode.Intersect);
            using var attrs = new System.Drawing.Imaging.ImageAttributes();
            if (silent) attrs.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = 0.35f });
            g.DrawImage(wave, Rectangle.Round(new RectangleF(r.X, r.Y + 2, r.Width, r.Height - 4)), x0, 0, Math.Max(1, w), wave.Height, GraphicsUnit.Pixel, attrs);
            g.Clip = region;
        }

        // No text on audio items: just the waveform (greyed when muted). The name, times and volume
        // are in the hover tooltip and the Properties panel.
        bool sel = _selAudio.Contains(a.Id);
        if (sel) Theme.DrawRounded(g, Accent, 2.5f, r);
        else Theme.DrawRounded(g, Color.FromArgb(60, 255, 255, 255), 1f, r);
        Grips(g, r, AudioRowH, 14, sel);
    }

    void DrawBar(Graphics g, Annotation a, RectangleF r)
    {
        var col = a.Kind switch
        {
            AnnotationKind.Arrow => Color.FromArgb(214, 72, 72),
            AnnotationKind.Box => Color.FromArgb(200, 160, 30),
            _ => Color.FromArgb(120, 120, 135),
        };
        float radius = Math.Min(5, r.Height / 2);
        Theme.FillRounded(g, col, r, radius);
        if (IsAnnotationSelected(a)) Theme.DrawRounded(g, Accent, 2, r, radius);
        if (r.Height < LabelMinLaneH) return; // squeezed lanes: colour only (hover shows what it is)
        var text = a.Kind == AnnotationKind.Text ? $"T  {a.Text.Split('\n')[0]}" : a.Kind == AnnotationKind.Arrow ? "→  Arrow" : "▭  Box";
        TextRenderer.DrawText(g, text, Font, Rectangle.Round(new RectangleF(r.X + 4, r.Y, r.Width - 6, r.Height)),
            Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    /// <summary>Selected, or another piece of the same run across cuts is.</summary>
    bool IsAnnotationSelected(Annotation a)
    {
        if (_selAnnotations.Count == 0) return false;
        if (_selAnnotations.Contains(a.Id)) return true;
        return a.LinkId is { } l && _selAnnotations.Any(id => _session.Project.FindAnnotation(id)?.LinkId == l);
    }

    /// <summary>Thumbnails pre-scaled to a row height, so tiling them is a 1:1 copy instead of a resample per tile.</summary>
    readonly Dictionary<(Guid, int), Bitmap> _tiles = new();

    /// <summary>
    /// Tile a poster thumbnail along an item, but ONLY across the part that's on screen. Zoomed in, a long clip is
    /// hundreds of thousands of pixels wide; tiling all of it (clipped away, but still drawn) made Ctrl+wheel zoom lag.
    /// </summary>
    void TileThumb(Graphics g, Guid sourceId, Image img, RectangleF r, int height)
    {
        if (height <= 0) return;
        if (!_tiles.TryGetValue((sourceId, height), out var tile))
        {
            int w = Math.Max(1, (int)Math.Round(height * img.Width / (double)img.Height));
            tile = new Bitmap(w, height);
            tile.SetResolution(g.DpiX, g.DpiY); // so DrawImage below doesn't rescale it for DPI
            using (var tg = Graphics.FromImage(tile))
            {
                tg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                tg.DrawImage(img, 0, 0, w, height);
            }
            _tiles[(sourceId, height)] = tile;
        }
        float tw = tile.Width;
        float from = Math.Max(r.X, r.X + (float)Math.Floor((-tw - r.X) / tw) * tw); // first tile that reaches the screen
        float to = Math.Min(r.Right, Width);
        for (float x = from; x < to; x += tw) g.DrawImageUnscaled(tile, (int)x, (int)r.Y);
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
            if (e.Y < RulerH) return; // items scrolled up under the ruler can't be hit
            if (AudioHit(e.Location) is { } hitAudio)
            {
                SelectAudio(hitAudio.A.Id);
                AudioMenuRequested?.Invoke(hitAudio.A.Id, PointToScreen(e.Location));
                return;
            }
            if (OverlayHit(e.Location) is { } hitOverlay)
            {
                SelectOverlay(hitOverlay.O.Id);
                OverlayMenuRequested?.Invoke(hitOverlay.O.Id, PointToScreen(e.Location));
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

        // Chapter marker flags on the ruler
        if (MarkerAt(e.Location) is { } hitMarker)
        {
            SelectMarker(hitMarker.Marker.Id);
            _dragId = hitMarker.Marker.Id;
            _originA = hitMarker.Time;
            _drag = Drag.Marker;
            _session.BeginEdit();
            return;
        }

        // Items scrolled up under the ruler can't be hit: a click there scrubs.
        if (e.Y < RulerH) goto Scrub;

        // Annotation bars
        foreach (var (a, s, en, lane) in AnnotationBars())
        {
            var r = BarRect(s, en, lane);
            if (!r.Contains(e.Location) || !InRegion(e.Location)) continue;
            SelectAnnotation(a.Id);
            _dragId = a.Id;
            _originA = s;
            _originB = en;
            _drag = e.X <= r.Left + Grab(r) ? Drag.AnnoStart : e.X >= r.Right - Grab(r) ? Drag.AnnoEnd : Drag.AnnoMove;
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
            _originB = e.X <= hit.R.Left + Grab(hit.R) ? a.In : a.Out;
            _drag = e.X <= hit.R.Left + Grab(hit.R) ? Drag.AudioIn : e.X >= hit.R.Right - Grab(hit.R) ? Drag.AudioOut : Drag.AudioMove;
            if (_drag == Drag.AudioMove) SeekRequested?.Invoke(this, T(e.X)); // so S splits it where you clicked
            _session.BeginEdit();
            return;
        }

        // Overlays: drag to move in time and between tracks, drag ends to trim
        if (OverlayHit(e.Location) is { } ov)
        {
            var o = ov.O;
            SelectOverlay(o.Id);
            _dragId = o.Id;
            _originA = o.Start;
            _originB = e.X <= ov.R.Left + Grab(ov.R) ? o.In : o.Out;
            _drag = e.X <= ov.R.Left + Grab(ov.R) ? Drag.OverlayIn : e.X >= ov.R.Right - Grab(ov.R) ? Drag.OverlayOut : Drag.OverlayMove;
            // Like a clip: clicking its body also moves the playhead there, so S splits it right where you clicked.
            if (_drag == Drag.OverlayMove) SeekRequested?.Invoke(this, T(e.X));
            _session.BeginEdit();
            return;
        }

        // Clips
        for (int i = 0; i < p.Timeline.Count; i++)
        {
            var r = ClipRect(i);
            if (!r.Contains(e.Location)) continue;
            var c = p.Timeline[i];
            if ((ModifierKeys & (Keys.Control | Keys.Shift)) != 0)
            {
                // Ctrl+click adds/removes a clip; Shift+click selects the run from the main clip. No drag.
                ExtendSelection(i, range: (ModifierKeys & Keys.Shift) != 0);
                return;
            }
            SelectClip(c.Id);
            _dragId = c.Id;
            _originA = c.In;
            _originB = c.Out;
            _trackSnap = TimelineOps.CaptureTracks(p, c.Id);
            if (e.X <= r.Left + Grab(r)) { _drag = Drag.TrimIn; _session.BeginEdit(); }
            else if (e.X >= r.Right - Grab(r)) { _drag = Drag.TrimOut; _session.BeginEdit(); }
            else { _drag = Drag.ClipBody; _dragClipStart = TimelineOps.ClipStart(p, c.Id); SeekRequested?.Invoke(this, T(e.X)); }
            return;
        }

        // Empty space in the tracks: drag a box to select everything it touches (Shift/Ctrl adds to the selection).
        // A plain click (no drag) clears the selection and moves the playhead, as before.
        bool additive = (ModifierKeys & (Keys.Shift | Keys.Control)) != 0;
        if (!additive) ClearSelection();
        _marqueeBase = (new(_selectedClips), new(_selAudio), new(_selOverlays), new(_selAnnotations));
        _marqueeFrom = _marqueeTo = e.Location;
        _downY = e.Y;
        _drag = Drag.Marquee;
        return;

        // Ruler: scrub
        Scrub:
        ClearSelection();
        _drag = Drag.Scrub;
        SeekRequested?.Invoke(this, T(e.X));
    }

    void ClearSelection()
    {
        SelectedAudioId = null;
        if (SelectedOverlayId is not null) SelectOverlay(null);
        SelectClip(null);
        SelectAnnotation(null);
        SelectMarker(null);
    }

    RectangleF MarqueeRect() => RectangleF.FromLTRB(
        Math.Min(_marqueeFrom.X, _marqueeTo.X), Math.Min(_marqueeFrom.Y, _marqueeTo.Y),
        Math.Max(_marqueeFrom.X, _marqueeTo.X), Math.Max(_marqueeFrom.Y, _marqueeTo.Y));

    /// <summary>
    /// Select what the drag box touches, on top of what was selected when it started (Shift/Ctrl) or instead of it.
    /// The Properties panel shows one main item: a clip if any were caught, else an overlay, else an audio item.
    /// </summary>
    void ApplyMarquee()
    {
        if (_marqueeBase is not { } b) return;
        var box = MarqueeRect();
        var p = _session.Project;
        var region = RectangleF.FromLTRB(0, RegionTop, Width, RegionBottom); // scrolled-away bars can't be caught
        _selectedClips.Clear(); _selectedClips.UnionWith(b.Clips);
        _selAudio.Clear(); _selAudio.UnionWith(b.Audio);
        _selOverlays.Clear(); _selOverlays.UnionWith(b.Overlays);
        _selAnnotations.Clear(); _selAnnotations.UnionWith(b.Annotations);
        for (int i = 0; i < p.Timeline.Count; i++)
            if (ClipRect(i).IntersectsWith(box)) _selectedClips.Add(p.Timeline[i].Id);
        foreach (var (a, row) in AudioRows())
            if (AudioRect(a, row).IntersectsWith(box)) _selAudio.Add(a.Id);
        foreach (var o in p.Overlays)
        {
            var r = OverlayRect(o);
            if (r.IntersectsWith(box) && r.IntersectsWith(region)) _selOverlays.Add(o.Id);
        }
        foreach (var (a, s, en, lane) in AnnotationBars())
        {
            var r = BarRect(s, en, lane);
            if (r.IntersectsWith(box) && r.IntersectsWith(region)) _selAnnotations.Add(a.Id);
        }

        // One main item for the Properties panel, by priority; the others keep only their sets.
        _primaryClip = p.Timeline.Select(c => (Guid?)c.Id).FirstOrDefault(id => _selectedClips.Contains(id!.Value));
        bool clips = _primaryClip is not null;
        _primaryOverlay = clips ? null : _selOverlays.Select(id => (Guid?)id).FirstOrDefault();
        _primaryAudio = clips || _primaryOverlay is not null ? null : _selAudio.Select(id => (Guid?)id).FirstOrDefault();
        _primaryAnnotation = clips || _primaryOverlay is not null || _primaryAudio is not null ? null : _selAnnotations.Select(id => (Guid?)id).FirstOrDefault();
        SelectedMarkerId = null;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_session is null) return;
        if (_drag == Drag.None) { UpdateCursor(e.Location); return; }

        var dt = TimeSpan.FromSeconds((e.X - _downX) / _pps);
        if (Math.Abs(e.X - _downX) > 3) _dragMoved = true;
        if (_drag == Drag.Marquee && Math.Abs(e.Y - _downY) > 3) _dragMoved = true; // a box can be dragged straight up/down
        var id = _dragId;
        // A click is not a drag: nothing changes until the mouse has really moved (a stray 1 px move
        // used to "move" an annotation piece and break its link).
        if (!_dragMoved && _drag is not Drag.Scrub) return;

        switch (_drag)
        {
            case Drag.Scrub:
                SeekRequested?.Invoke(this, T(e.X));
                break;
            case Drag.Marquee:
                _marqueeTo = e.Location;
                ApplyMarquee();
                break;
            case Drag.ClipBody:
                if (_dragMoved)
                {
                    // Past the end of the other clips: it lands right there, with a gap before it (owner: "end of
                    // proj should not be capped"). Within them: reorder, as before.
                    var proposed = _dragClipStart + dt;
                    var restEnd = TimelineOps.EndWithout(_session.Project, id);
                    if (proposed > restEnd + SnapTolerance) { _dropTime = proposed; _dropIndex = -1; }
                    else { _dropTime = null; _dropIndex = DropIndex(e.X); }
                    Invalidate();
                }
                break;
            case Drag.TrimIn:
                // The mouse moves in timeline time; In/Out are footage time (they differ when the clip is sped up).
                _session.Preview(p => TimelineOps.TrimIn(p, id, _originA + (p.FindClip(id)?.SourceSpan(dt) ?? dt), _trackSnap));
                break;
            case Drag.TrimOut:
                _session.Preview(p => TimelineOps.TrimOut(p, id, _originB + (p.FindClip(id)?.SourceSpan(dt) ?? dt), _trackSnap));
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
                // Edges stick to other audio items and clip cuts within a few pixels, so items slide together and
                // touch exactly (same row) instead of overlapping a hair and dropping to a new row.
                _session.Preview(p => TimelineOps.MoveAudio(p, id, TimelineOps.SnapAudioStart(p, id, _originA + dt, SnapTolerance)));
                break;
            case Drag.AudioIn:
            {
                // Absolute target (origin + delta), so repeated Preview steps don't accumulate. _originA = start, _originB = in.
                var edge = _originA + dt;
                _session.Preview(p =>
                {
                    var snapped = TimelineOps.SnapAudioEdge(p, id, edge, SnapTolerance);
                    TimelineOps.TrimAudioIn(p, id, _originB + (snapped - _originA));
                });
                break;
            }
            case Drag.AudioOut:
            {
                // _originA = start, _originB = out. The right edge on the timeline is start + (out − in).
                _session.Preview(p =>
                {
                    if (p.FindAudio(id) is not { } a) return;
                    var edge = _originA + (_originB + dt - a.In);
                    var snapped = TimelineOps.SnapAudioEdge(p, id, edge, SnapTolerance);
                    TimelineOps.TrimAudioOut(p, id, _originB + dt + (snapped - edge));
                });
                break;
            }
            case Drag.Marker:
                if (_dragMoved) _session.Preview(p => MarkerOps.MoveTo(p, id, _originA + dt));
                break;
            case Drag.OverlayMove:
                int track = TrackAtY(e.Y);
                _dragTrack = track; // shows a row for a new track while hovering above the top one
                _session.Preview(p => TimelineOps.MoveOverlay(p, id, _originA + dt, track));
                break;
            case Drag.OverlayIn:
                // The mouse moves in timeline time; In/Out are footage time (they differ when the overlay is sped up).
                _session.Preview(p => TimelineOps.TrimOverlayIn(p, id, _originB + (p.FindOverlay(id)?.SourceSpan(dt) ?? dt)));
                break;
            case Drag.OverlayOut:
                _session.Preview(p => TimelineOps.TrimOverlayOut(p, id, _originB + (p.FindOverlay(id)?.SourceSpan(dt) ?? dt)));
                break;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_drag == Drag.Marquee)
        {
            _marqueeBase = null;
            if (_dragMoved) MarqueeSelected?.Invoke(this, EventArgs.Empty);
            else SeekRequested?.Invoke(this, T(e.X)); // a click on empty space: just move the playhead
        }
        else if (_drag == Drag.ClipBody && _dragMoved && _dropTime is { } dropAt)
        {
            var id = _dragId;
            _session.Execute(p => TimelineOps.MoveToTime(p, id, dropAt));
        }
        else if (_drag == Drag.ClipBody && _dragMoved && _dropIndex >= 0)
        {
            var id = _dragId;
            int from = _session.Project.Timeline.FindIndex(c => c.Id == id);
            int to = _dropIndex > from ? _dropIndex - 1 : _dropIndex; // index after removal
            if (to != from) _session.Execute(p => TimelineOps.Move(p, id, to));
        }
        else if (_drag is Drag.TrimIn or Drag.TrimOut or Drag.AnnoMove or Drag.AnnoStart or Drag.AnnoEnd
                 or Drag.AudioMove or Drag.AudioIn or Drag.AudioOut or Drag.Marker
                 or Drag.OverlayMove or Drag.OverlayIn or Drag.OverlayOut)
        {
            if (_drag == Drag.Marker && !_dragMoved) SeekRequested?.Invoke(this, _originA); // click = jump to the chapter
            _session.CommitEdit();
        }
        bool wasDragging = _drag != Drag.None;
        _dragTrack = 0;
        _drag = Drag.None;
        _dropIndex = -1;
        _dropTime = null;
        Invalidate();
        if (wasDragging) PreferredHeightChanged?.Invoke(this, EventArgs.Empty); // the form held the size during the drag
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

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (_session is not null && MarkerAt(e.Location) is { } hit) MarkerRenameRequested?.Invoke(this, hit.Marker.Id);
    }

    void UpdateCursor(Point pt)
    {
        var p = _session.Project;
        if (MarkerAt(pt) is not null) { Cursor = Cursors.SizeWE; return; }
        if (pt.Y < RulerH)
        {
            if (_tipFor is not null) { _tipFor = null; _tip.SetToolTip(this, null); }
            Cursor = Cursors.Default;
            return;
        }
        foreach (var (a, s, en, lane) in AnnotationBars())
        {
            var r = BarRect(s, en, lane);
            if (!r.Contains(pt) || !InRegion(pt)) continue;
            Cursor = pt.X <= r.Left + Grab(r) || pt.X >= r.Right - Grab(r) ? Cursors.SizeWE : Cursors.Hand;
            if (_tipFor != a.Id) { _tipFor = a.Id; _tip.SetToolTip(this, Summary(a, s, en)); }
            return;
        }
        if (AudioHit(pt) is { } hit)
        {
            Cursor = pt.X <= hit.R.Left + Grab(hit.R) || pt.X >= hit.R.Right - Grab(hit.R) ? Cursors.SizeWE : Cursors.Hand;
            if (_tipFor != hit.A.Id) { _tipFor = hit.A.Id; _tip.SetToolTip(this, AudioSummary(hit.A)); }
            return;
        }
        if (OverlayHit(pt) is { } ov)
        {
            Cursor = pt.X <= ov.R.Left + Grab(ov.R) || pt.X >= ov.R.Right - Grab(ov.R) ? Cursors.SizeWE : Cursors.Hand;
            if (_tipFor != ov.O.Id)
            {
                _tipFor = ov.O.Id;
                var name = _session.Project.FindMedia(ov.O.SourceId)?.DisplayName ?? "?";
                _tip.SetToolTip(this, $"▣ {name} on V{ov.O.Track + 1}\n{FormatTime(ov.O.Start, tenths: true)} – {FormatTime(ov.O.End, tenths: true)}  ({ov.O.Duration.TotalSeconds:0.0}s)\n" +
                                      "Drag to move (up/down changes track) · drag ends to trim · drag it in the preview to place it · right-click for more");
            }
            return;
        }
        if (_tipFor is not null) { _tipFor = null; _tip.SetToolTip(this, null); }
        for (int i = 0; i < p.Timeline.Count; i++)
        {
            var r = ClipRect(i);
            if (!r.Contains(pt)) continue;
            Cursor = pt.X <= r.Left + Grab(r) || pt.X >= r.Right - Grab(r) ? Cursors.SizeWE : Cursors.Hand;
            if (p.Timeline[i] is { IsGap: true } gap && _tipFor != gap.Id)
            {
                _tipFor = gap.Id;
                _tip.SetToolTip(this, $"Gap ({gap.Duration.TotalSeconds:0.0}s): black, with only the audio track and overlays playing\n" +
                                      "Click it and press Del to close it up · drag its edges to make it shorter or longer");
            }
            return;
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
        if (id is not null) { SelectedClipId = null; SelectedAnnotationId = null; SelectedMarkerId = null; SelectedOverlayId = null; }
        AudioSelected?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    void SelectMarker(Guid? id)
    {
        SelectedMarkerId = id;
        if (id is not null) { SelectedClipId = null; SelectedAnnotationId = null; SelectedAudioId = null; SelectedOverlayId = null; }
        MarkerSelected?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    void SelectClip(Guid? id)
    {
        SelectedClipId = id;
        if (id is not null) { SelectedAnnotationId = null; SelectedAudioId = null; SelectedMarkerId = null; SelectedOverlayId = null; }
        ClipSelected?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    void ExtendSelection(int index, bool range)
    {
        var p = _session.Project;
        var id = p.Timeline[index].Id;
        SelectedAnnotationId = null;
        SelectedAudioId = null;
        SelectedMarkerId = null;
        SelectedOverlayId = null;
        if (range && _primaryClip is { } main && p.Timeline.FindIndex(c => c.Id == main) is var from and >= 0)
        {
            int lo = Math.Min(from, index), hi = Math.Max(from, index);
            for (int k = lo; k <= hi; k++) _selectedClips.Add(p.Timeline[k].Id);
        }
        else if (_selectedClips.Contains(id) && _selectedClips.Count > 1)
        {
            _selectedClips.Remove(id);
            if (_primaryClip == id) _primaryClip = _selectedClips.First();
        }
        else
        {
            _selectedClips.Add(id);
            _primaryClip ??= id;
        }
        if (_primaryClip is null || !_selectedClips.Contains(_primaryClip.Value)) _primaryClip = id;
        ClipSelected?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    void SelectAnnotation(Guid? id)
    {
        SelectedAnnotationId = id;
        if (id is not null) { SelectedClipId = null; SelectedAudioId = null; SelectedMarkerId = null; SelectedOverlayId = null; }
        AnnotationSelected?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var img in _thumbs.Values) img.Dispose();
            foreach (var t in _tiles.Values) t.Dispose();
        }
        base.Dispose(disposing);
    }
}
