using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using EasyAI.Desktop.Infrastructure;
using EasyLabel.Core.Projects;

namespace EasyAI.Desktop.Services;

/// <summary>One row of the recent-projects list. <see cref="Exists"/> is true until <see cref="RecentProjectsService.Refresh"/> finds the path missing.</summary>
public sealed class RecentProjectItem : ObservableObject
{
    private bool _exists = true;

    public RecentProjectItem(RecentProject entry)
    {
        Entry = entry;
    }

    public RecentProject Entry { get; }

    public string Path => Entry.Path;

    public string Name => Entry.Name;

    public RecentProjectKind Kind => Entry.Kind;

    public DateTime LastOpened => Entry.LastOpenedUtc.ToLocalTime();

    /// <summary>False when the manifest file or folder is gone; the UI greys the row but the entry stays until the user removes it.</summary>
    public bool Exists
    {
        get => _exists;
        set => SetProperty(ref _exists, value);
    }

    /// <summary>Second line of a row: the folder of the project file (named) or the image folder (legacy).</summary>
    public string SecondaryText => RecentProjectDisplay.SecondaryText(Entry);

    /// <summary>Name and full path on one line.</summary>
    public string DisplayText => Name + " - " + Path;
}

/// <summary>
/// The recent-projects list of the start screen and File menu: keeps <see cref="Items"/>, persists every change through the settings
/// store and checks off the UI thread whether each path still exists. Create and use it on the UI thread.
/// </summary>
public sealed class RecentProjectsService
{
    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private readonly Dispatcher _dispatcher;
    private readonly Func<string, bool> _exists;
    private IReadOnlyList<RecentProject> _entries;
    private int _refreshVersion;

    /// <param name="settings">The shared settings object (its <see cref="AppSettings.RecentProjects"/> is read now and rewritten on every change).</param>
    /// <param name="exists">Existence test, run on a worker thread; defaults to "a file or folder is there".</param>
    public RecentProjectsService(AppSettings settings, SettingsStore store, Dispatcher dispatcher, Func<string, bool>? exists = null)
    {
        _settings = settings;
        _store = store;
        _dispatcher = dispatcher;
        _exists = exists ?? DefaultExists;
        _entries = RecentProjectSetting.ToEntries(settings.RecentProjects);
        Items = new ObservableCollection<RecentProjectItem>();
        Rebuild();
    }

    public ObservableCollection<RecentProjectItem> Items { get; }

    /// <summary>Puts a named project that was just created, opened or saved as at the top.</summary>
    public void Record(ProjectOpenResult result)
    {
        Record(result.Manifest.ManifestPath, result.Manifest.Name, RecentProjectKind.Named);
    }

    /// <summary>Puts a legacy image folder at the top.</summary>
    public void RecordFolder(string folder)
    {
        Record(folder, string.Empty, RecentProjectKind.Folder);
    }

    /// <summary>Puts the entry at the top (moving an existing entry for the same path), saves the settings and updates <see cref="Items"/>.</summary>
    public void Record(string path, string name, RecentProjectKind kind)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        _entries = RecentProjectList.Add(_entries, new RecentProject(path, name, DateTime.UtcNow, kind));
        Commit();
    }

    public void Remove(string path)
    {
        _entries = RecentProjectList.Remove(_entries, path);
        Commit();
    }

    public void Clear()
    {
        _entries = RecentProjectList.Clear();
        Commit();
    }

    /// <summary>Re-checks every path on a worker thread (a slow or offline network path never blocks the UI) and updates <see cref="RecentProjectItem.Exists"/>.</summary>
    public void Refresh()
    {
        IReadOnlyList<RecentProject> snapshot = _entries;
        int version = ++_refreshVersion;
        Task.Run(() => RecentProjectList.MarkMissing(snapshot, _exists)).ContinueWith(task =>
        {
            if (task.IsFaulted || task.IsCanceled)
            {
                return;
            }

            IReadOnlyList<RecentProjectStatus> statuses = task.Result;
            _dispatcher.BeginInvoke(new Action(() => Apply(statuses, version)));
        }, TaskScheduler.Default);
    }

    private void Apply(IReadOnlyList<RecentProjectStatus> statuses, int version)
    {
        if (version != _refreshVersion)
        {
            return;
        }

        foreach (RecentProjectStatus status in statuses)
        {
            foreach (RecentProjectItem item in Items)
            {
                if (RecentProjectList.SamePath(item.Path, status.Project.Path))
                {
                    item.Exists = status.Exists;
                    break;
                }
            }
        }
    }

    private void Commit()
    {
        _settings.RecentProjects = RecentProjectSetting.FromEntries(_entries);
        _store.SaveSettings(_settings);
        Rebuild();
    }

    private void Rebuild()
    {
        var known = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (RecentProjectItem old in Items)
        {
            known[old.Path] = old.Exists;
        }

        Items.Clear();
        foreach (RecentProject entry in _entries)
        {
            var item = new RecentProjectItem(entry);
            if (known.TryGetValue(entry.Path, out bool exists))
            {
                item.Exists = exists;
            }

            Items.Add(item);
        }
    }

    private static bool DefaultExists(string path)
    {
        return File.Exists(path) || Directory.Exists(path);
    }
}
