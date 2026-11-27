using Flyweight.Core.Media;
using Flyweight.Core.Model;

namespace Flyweight.Core.Editing;

/// <summary>
/// Owns the open project, its file path, dirty flag and undo/redo history.
/// Undo is snapshot-based: the project is small JSON, so copying it whole is cheap and
/// impossible to get subtly wrong (unlike hand-written inverse commands).
/// </summary>
public sealed class ProjectSession
{
    const int MaxUndo = 200;

    readonly LinkedList<string> _undo = new();
    readonly Stack<string> _redo = new();
    string? _pendingSnapshot;

    public Project Project { get; private set; } = new();
    public string? FilePath { get; set; }
    public bool IsDirty { get; private set; }
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Raised after any change to <see cref="Project"/> (edit, undo, redo, load).</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Apply a discrete edit as one undo step. If the edit throws, the project is put back exactly as it was
    /// (a half-applied edit would otherwise stay on screen with no undo step), and the exception is rethrown.
    /// </summary>
    public void Execute(Action<Project> edit)
    {
        var before = ProjectSerializer.Serialize(Project);
        try { edit(Project); }
        catch
        {
            Project = ProjectSerializer.Deserialize(before);
            Changed?.Invoke(this, EventArgs.Empty);
            throw;
        }
        if (ProjectSerializer.Serialize(Project) != before) PushUndo(before);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Start a continuous edit (e.g. a mouse drag). Call <see cref="Preview"/> as it moves and
    /// <see cref="CommitEdit"/> on release; the whole drag becomes a single undo step.
    /// </summary>
    public void BeginEdit() => _pendingSnapshot ??= ProjectSerializer.Serialize(Project);

    /// <summary>
    /// One step of a continuous edit. Opens the edit itself if none is open: Undo/Redo commit an open edit, so a drag
    /// that carries on after Ctrl+Z would otherwise change the project with no undo step and without marking it dirty
    /// (no autosave, no save prompt).
    /// </summary>
    public void Preview(Action<Project> edit)
    {
        BeginEdit();
        edit(Project);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void CommitEdit()
    {
        if (_pendingSnapshot is null) return;
        if (ProjectSerializer.Serialize(Project) != _pendingSnapshot) PushUndo(_pendingSnapshot);
        _pendingSnapshot = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Undo()
    {
        // An edit still open (e.g. a wheel-zoom that commits on a timer) becomes its own step first, so this undoes
        // it instead of leaving a stale snapshot to be pushed later (which would wipe redo).
        if (_pendingSnapshot is not null) CommitEdit();
        if (_undo.Count == 0) return;
        _redo.Push(ProjectSerializer.Serialize(Project));
        Project = ProjectSerializer.Deserialize(_undo.Last!.Value);
        _undo.RemoveLast();
        IsDirty = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Redo()
    {
        if (_pendingSnapshot is not null) CommitEdit();
        if (_redo.Count == 0) return;
        _undo.AddLast(ProjectSerializer.Serialize(Project));
        Project = ProjectSerializer.Deserialize(_redo.Pop());
        IsDirty = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A fresh, empty project. New projects blur the bars around portrait clips by default (D40).</summary>
    public void New(ExportResolution outputResolution = ExportResolution.P1080, BackgroundFill background = BackgroundFill.Blur,
                    CanvasShape shape = CanvasShape.Landscape)
    {
        Load(new Project { OutputResolution = outputResolution, Background = background, Shape = shape }, null);
    }

    public void Load(Project project, string? path)
    {
        // Old projects: fix images saved as zero-length videos. Marks the project dirty so it gets saved.
        var upgraded = TimelineOps.UpgradeLegacyStills(project);
        Project = project;
        FilePath = path;
        _undo.Clear();
        _redo.Clear();
        _pendingSnapshot = null;
        IsDirty = upgraded;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Save(string path)
    {
        ProjectSerializer.Save(Project, path);
        FilePath = path;
        IsDirty = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Write a crash-recovery copy without touching the user's file or dirty flag.</summary>
    public void Autosave(string path) => ProjectSerializer.Save(Project, path);

    void PushUndo(string snapshot)
    {
        _undo.AddLast(snapshot);
        if (_undo.Count > MaxUndo) _undo.RemoveFirst();
        _redo.Clear();
        IsDirty = true;
    }
}
