using System.Collections.ObjectModel;
using System.Text;
using Clipt.Models;
using Clipt.Services;

namespace Clipt.UI;

public enum LauncherMode { Browse, AiActions, AiResult }

public sealed class FilterOption : ObservableObject
{
    private readonly Action<FilterOption> _onChecked;
    public string Name { get; }
    public ClipKind[]? Kinds { get; }
    public bool PinnedOnly { get; }

    public FilterOption(string name, ClipKind[]? kinds, bool pinnedOnly, Action<FilterOption> onChecked)
    {
        Name = name; Kinds = kinds; PinnedOnly = pinnedOnly; _onChecked = onChecked;
    }

    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set { if (Set(ref _isChecked, value) && value) _onChecked(this); }
    }
}

public sealed record Hint(string Keys, string Label);

public sealed class LauncherViewModel : ObservableObject
{
    private readonly App _app;
    private CancellationTokenSource? _aiCts;
    private string _browseQuery = "";
    private FilterOption _filter;

    public ObservableCollection<ClipItem> Items { get; } = [];
    public ObservableCollection<AiAction> Actions { get; } = [];
    public ObservableCollection<Hint> Hints { get; } = [];
    public IReadOnlyList<FilterOption> Filters { get; }

    public LauncherViewModel(App app)
    {
        _app = app;
        Filters =
        [
            new("All", null, false, OnFilter),
            new("Text", [ClipKind.Text, ClipKind.Code], false, OnFilter),
            new("Images", [ClipKind.Image], false, OnFilter),
            new("Links", [ClipKind.Link], false, OnFilter),
            new("Files", [ClipKind.Files], false, OnFilter),
            new("Colors", [ClipKind.Color], false, OnFilter),
            new("Pinned", null, true, OnFilter),
        ];
        _filter = Filters[0];
        _filter.IsChecked = true;
        UpdateHints();
    }

    // ---------- state ----------

    private string _query = "";
    public string Query
    {
        get => _query;
        set
        {
            if (!Set(ref _query, value ?? "")) return;
            if (Mode == LauncherMode.Browse) Reload();
            else
            {
                if (Mode == LauncherMode.AiResult) Mode = LauncherMode.AiActions;
                RefreshActions();
            }
        }
    }

    private ClipItem? _selected;
    public ClipItem? Selected
    {
        get => _selected;
        set { if (Set(ref _selected, value)) OnPropertyChanged(nameof(ShowClipPreview)); }
    }

    private AiAction? _selectedAction;
    public AiAction? SelectedAction { get => _selectedAction; set => Set(ref _selectedAction, value); }

    private LauncherMode _mode;
    public LauncherMode Mode
    {
        get => _mode;
        private set
        {
            if (!Set(ref _mode, value)) return;
            OnPropertyChanged(nameof(IsBrowse));
            OnPropertyChanged(nameof(IsAi));
            OnPropertyChanged(nameof(IsAiResult));
            OnPropertyChanged(nameof(ShowClipPreview));
            OnPropertyChanged(nameof(Placeholder));
            OnPropertyChanged(nameof(ShowEmpty));
            UpdateHints();
        }
    }

    public bool IsBrowse => Mode == LauncherMode.Browse;
    public bool IsAi => Mode != LauncherMode.Browse;
    public bool IsAiResult => Mode == LauncherMode.AiResult;
    public bool ShowClipPreview => Mode != LauncherMode.AiResult && Selected is not null;

    public string Placeholder => IsAi ? "Ask anything about this clip, or pick an action…" : "Search your clipboard";

    private string _statusText = "";
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    public bool ShowEmpty => IsBrowse && Items.Count == 0;

    public string EmptyText =>
        Query.Length > 0 ? $"No matches for “{Query}”"
        : _filter.PinnedOnly ? "Nothing pinned yet — press Ctrl+P on a clip to keep it forever"
        : _filter.Kinds is not null ? $"No {_filter.Name.ToLowerInvariant()} in your history yet"
        : "Copy something — it will show up here";

    // ---------- AI state ----------

    public string AiProvider => _app.Ai.ProviderLabel;

    private string _aiTitle = "";
    public string AiTitle { get => _aiTitle; private set => Set(ref _aiTitle, value); }

    private string _aiOutput = "";
    public string AiOutput { get => _aiOutput; private set => Set(ref _aiOutput, value); }

    private bool _aiBusy;
    public bool AiBusy { get => _aiBusy; private set => Set(ref _aiBusy, value); }

    private string? _aiError;
    public string? AiError { get => _aiError; private set => Set(ref _aiError, value); }

    public bool HasAiResult => IsAiResult && !AiBusy && AiOutput.Length > 0;

    // ---------- browse ----------

    public void OnShow()
    {
        CancelAi();
        Mode = LauncherMode.Browse;
        _browseQuery = "";
        _query = "";
        OnPropertyChanged(nameof(Query));
        if (!_filter.Equals(Filters[0])) Filters[0].IsChecked = true; // triggers reload
        Reload();
    }

    private void OnFilter(FilterOption f)
    {
        _filter = f;
        foreach (var other in Filters) if (!ReferenceEquals(other, f)) other.IsChecked = false;
        Reload();
    }

    public void SetFilter(int index)
    {
        if (index >= 0 && index < Filters.Count) Filters[index].IsChecked = true;
    }

    public void CycleFilter(int delta)
    {
        int i = Filters.ToList().IndexOf(_filter);
        SetFilter((i + delta + Filters.Count) % Filters.Count);
    }

    public void Reload(bool keepSelection = false)
    {
        var keepId = keepSelection ? Selected?.Id : null;
        var results = _app.Store.Search(new SearchRequest
        {
            Query = Query,
            Kinds = _filter.Kinds,
            PinnedOnly = _filter.PinnedOnly,
        });

        Items.Clear();
        foreach (var r in results) Items.Add(r);
        Selected = (keepId is not null ? Items.FirstOrDefault(i => i.Id == keepId) : null) ?? Items.FirstOrDefault();

        int total = _app.Store.Count();
        StatusText = Query.Length > 0 || _filter.Kinds is not null || _filter.PinnedOnly
            ? $"{Items.Count:N0} of {total:N0}"
            : $"{total:N0} clips";
        if (_app.Monitor.Paused) StatusText = "Paused · " + StatusText;
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(EmptyText));
    }

    public void Move(int delta)
    {
        if (IsAi)
        {
            if (Mode == LauncherMode.AiResult) Mode = LauncherMode.AiActions;
            if (Actions.Count == 0) return;
            int i = SelectedAction is null ? -1 : Actions.IndexOf(SelectedAction);
            SelectedAction = Actions[Math.Clamp(i + delta, 0, Actions.Count - 1)];
            return;
        }
        if (Items.Count == 0) return;
        int idx = Selected is null ? -1 : Items.IndexOf(Selected);
        Selected = Items[Math.Clamp(idx + delta, 0, Items.Count - 1)];
    }

    public void MoveToEdge(bool end)
    {
        if (IsAi) { if (Actions.Count > 0) SelectedAction = end ? Actions[^1] : Actions[0]; return; }
        if (Items.Count > 0) Selected = end ? Items[^1] : Items[0];
    }

    public void TogglePin()
    {
        if (Selected is null) return;
        Selected.Pinned = !Selected.Pinned;
        _app.Store.SetPinned(Selected.Id, Selected.Pinned);
        if (_filter.PinnedOnly && !Selected.Pinned) Reload();
    }

    public void DeleteSelected()
    {
        if (Selected is null) return;
        int idx = Items.IndexOf(Selected);
        _app.Store.Delete(Selected.Id);
        Items.RemoveAt(idx);
        Selected = Items.Count == 0 ? null : Items[Math.Min(idx, Items.Count - 1)];
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(EmptyText));
    }

    // ---------- privacy ----------

    public void ToggleReveal()
    {
        if (Selected is { Sensitive: true } s) s.IsRevealed = !s.IsRevealed;
    }

    /// <summary>Re-masks every revealed clip (called whenever the launcher hides).</summary>
    public void ConcealAll()
    {
        foreach (var i in Items) if (i.IsRevealed) i.IsRevealed = false;
    }

    public void ToggleSensitive()
    {
        if (Selected is not { } s) return;
        bool make = !s.Sensitive;
        _app.Store.SetSensitive(s.Id, make, make ? "Marked as sensitive" : null);
        _app.Store.Reindex(s);
        StatusText = make
            ? $"Hidden · auto-deletes in {_app.Settings.SensitiveTtlMinutes} min (Ctrl+P keeps it)"
            : "No longer marked as sensitive";
    }

    public void ExcludeSelectedApp()
    {
        if (Selected is not { SourcePath: { Length: > 0 } path } s) return;
        var name = _app.ExcludeApp(path, s.SourceApp);
        StatusText = $"Copies from {name} won't be recorded anymore";
    }

    // ---------- AI ----------

    public bool EnterAi()
    {
        if (Selected is null) return false;
        if (!_app.Ai.Enabled) { StatusText = "Turn on AI in Settings (Ctrl+,)"; return false; }
        if (Selected.Sensitive) { StatusText = "AI is off for sensitive clips — they never leave your PC"; return false; }
        _browseQuery = Query;
        Mode = LauncherMode.AiActions;
        _query = "";
        OnPropertyChanged(nameof(Query));
        OnPropertyChanged(nameof(AiProvider));
        RefreshActions();
        return true;
    }

    public void ExitAi()
    {
        CancelAi();
        Mode = LauncherMode.Browse;
        _query = _browseQuery;
        OnPropertyChanged(nameof(Query));
    }

    public void BackToActions()
    {
        CancelAi();
        Mode = LauncherMode.AiActions;
    }

    private void RefreshActions()
    {
        if (Selected is null) return;
        var q = Query.Trim();
        var matches = _app.Ai.ActionsFor(Selected)
            .Where(a => q.Length == 0 || a.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase))
            .ToList();

        Actions.Clear();
        var custom = q.Length > 0 ? new AiAction($"Ask: “{q}”", "", q) { IsCustom = true } : null;
        if (custom is not null && matches.Count == 0) Actions.Add(custom);
        foreach (var a in matches) Actions.Add(a);
        if (custom is not null && matches.Count > 0) Actions.Add(custom);
        SelectedAction = Actions.FirstOrDefault();
    }

    public async Task RunAiAsync(AiAction action)
    {
        if (Selected is null) return;
        CancelAi();
        var cts = _aiCts = new CancellationTokenSource();

        AiTitle = action.IsCustom ? action.Instruction : action.Name;
        AiOutput = "";
        AiError = null;
        AiBusy = true;
        Mode = LauncherMode.AiResult;
        OnPropertyChanged(nameof(HasAiResult));

        var sb = new StringBuilder();
        try
        {
            await foreach (var chunk in _app.Ai.RunAsync(Selected, action.Instruction, cts.Token))
            {
                sb.Append(chunk);
                AiOutput = sb.ToString().TrimStart();
            }
            if (sb.Length == 0) AiError = "The model returned an empty response.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AiError = ex.Message; }
        finally
        {
            if (ReferenceEquals(_aiCts, cts))
            {
                AiBusy = false;
                OnPropertyChanged(nameof(HasAiResult));
                UpdateHints();
            }
        }
    }

    public void CancelAi()
    {
        _aiCts?.Cancel();
        _aiCts = null;
        AiBusy = false;
    }

    /// <summary>Stores the AI result as a new clip so it is pasteable and stays in history.</summary>
    public ClipItem? CreateClipFromAiResult()
    {
        var text = AiOutput.Trim();
        if (text.Length == 0) return null;
        return _app.Store.Upsert(new ClipItem
        {
            Kind = ClipboardMonitor.Classify(text),
            Text = text,
            Hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("text:" + text))),
            SourceApp = "Clipt AI",
        });
    }

    // ---------- footer ----------

    private void UpdateHints()
    {
        Hints.Clear();
        switch (Mode)
        {
            case LauncherMode.Browse:
                Hints.Add(new("↵", "Paste"));
                Hints.Add(new("⇧↵", "Plain text"));
                Hints.Add(new("Ctrl ↵", "Copy"));
                Hints.Add(new("Ctrl P", "Pin"));
                Hints.Add(new("Ctrl H", "Hide"));
                Hints.Add(new("⇧Del", "Delete"));
                if (_app.Ai.Enabled) Hints.Add(new("Tab", "AI actions"));
                break;
            case LauncherMode.AiActions:
                Hints.Add(new("↵", "Run"));
                Hints.Add(new("↑↓", "Choose"));
                Hints.Add(new("Esc", "Back"));
                break;
            case LauncherMode.AiResult:
                Hints.Add(new("↵", "Paste result"));
                Hints.Add(new("Ctrl ↵", "Copy"));
                Hints.Add(new("↑↓", "Other action"));
                Hints.Add(new("Esc", "Back"));
                break;
        }
    }
}
