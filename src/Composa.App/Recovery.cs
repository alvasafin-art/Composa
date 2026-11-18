using System.Diagnostics;
using System.Text.Json;
using Composa.Editing;
using Composa.IO;
using Composa.Model;

namespace Composa.App;

/// <summary>
/// Crash protection: modified documents are written to the cache directory every few minutes, from a snapshot and
/// off the UI thread (committed bitmaps are immutable, so a snapshot can be read safely while editing continues).
/// A clean close removes them; whatever is still there at the next launch is offered for recovery.
/// </summary>
public sealed class Recovery
{
    public sealed record Entry(string ProjectPath, string InfoPath, string Title, string? OriginalPath, DateTime SavedAt);

    private sealed record Info(string Title, string? OriginalPath, DateTime SavedAt, int ProcessId);

    private readonly string directory;
    private sealed class Copy(Guid id)
    {
        public readonly Guid Id = id;
        public volatile int Revision = -1;
        public DateTime? LastSuccess;
    }

    private readonly Dictionary<EditorSession, Copy> saved = [];
    public string? Warning { get; private set; }

    public void Adopt(EditorSession session, Entry entry)
    {
        if (Guid.TryParse(Path.GetFileNameWithoutExtension(entry.ProjectPath), out var id))
            saved[session] = new Copy(id) { LastSuccess = entry.SavedAt };
    }
    private Task running = Task.CompletedTask;

    public Recovery(string? directory = null) => this.directory = directory ?? Path.Combine(AppPaths.Cache, "recovery");

    /// <summary>Writes every modified session that changed since its last autosave. Returns the task doing the writing.</summary>
    public Task Save(IEnumerable<EditorSession> sessions)
    {
        if (!running.IsCompleted) return running; // A large document may still be encoding; try again next time.
        var work = new List<(Copy State, int Revision, Document Snapshot, string Path, Info Info)>();
        foreach (var session in sessions)
        {
            if (!session.IsModified || session.IsInteracting) continue;
            if (!saved.TryGetValue(session, out var state)) saved[session] = state = new Copy(Guid.NewGuid());
            if (state.Revision == session.Revision) continue;
            work.Add((state, session.Revision, session.Document.Clone(), Path.Combine(directory, state.Id + ProjectFile.Extension),
                new Info(session.Title, session.FilePath, DateTime.Now, Environment.ProcessId)));
        }
        if (work.Count == 0) return Task.CompletedTask;
        return running = Task.Run(() =>
        {
            Warning = null;
            foreach (var (state, revision, snapshot, path, info) in work)
            {
                try
                {
                    Directory.CreateDirectory(directory);
                    ProjectFile.Save(snapshot, path);
                    var metadata = Path.ChangeExtension(path, ".json");
                    File.WriteAllText(metadata + ".tmp", JsonSerializer.Serialize(info));
                    File.Move(metadata + ".tmp", metadata, true);
                    state.LastSuccess = info.SavedAt;
                    state.Revision = revision;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // Recovery copies are best effort: a full or read-only cache must not interrupt editing.
                    Warning = state.LastSuccess is { } last
                        ? $"Autosave failed for {info.Title}. Last copy {last:t}. Will retry."
                        : $"Autosave failed for {info.Title}. No recovery copy yet. Will retry.";
                    Console.Error.WriteLine($"{Warning} {error.Message}");
                }
            }
        });
    }

    /// <summary>Forgets a session that was saved or closed on purpose, deleting its recovery copy.</summary>
    public void Forget(EditorSession session)
    {
        if (!saved.Remove(session, out var state)) return;
        var path = Path.Combine(directory, state.Id + ProjectFile.Extension);
        running = running.ContinueWith(_ => Delete(path));
    }

    /// <summary>Recovery copies left behind by an instance that is no longer running.</summary>
    public List<Entry> FindAbandoned()
    {
        var found = new List<Entry>();
        if (!Directory.Exists(directory)) return found;
        foreach (var infoPath in Directory.GetFiles(directory, "*.json"))
        {
            var projectPath = Path.ChangeExtension(infoPath, ProjectFile.Extension);
            try
            {
                var info = JsonSerializer.Deserialize<Info>(File.ReadAllText(infoPath));
                if (info == null || !File.Exists(projectPath) || IsAlive(info.ProcessId)) continue;
                found.Add(new Entry(projectPath, infoPath, info.Title, info.OriginalPath, info.SavedAt));
            }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { /* An unreadable entry cannot be recovered; leave it alone. */ }
        }
        return found.OrderBy(e => e.SavedAt).ToList();
    }

    public void Discard(Entry entry) => Delete(entry.ProjectPath);

    private static void Delete(string projectPath)
    {
        try
        {
            File.Delete(projectPath);
            File.Delete(Path.ChangeExtension(projectPath, ".json"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* It will be offered again next launch. */ }
    }

    private static bool IsAlive(int processId)
    {
        if (processId == Environment.ProcessId) return true;
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited && process.ProcessName.Contains("composa", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
    }
}
