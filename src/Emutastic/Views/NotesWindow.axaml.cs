using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Emutastic.Models;
using Emutastic.Services;

namespace Emutastic.Views;

/// <summary>
/// Per-game notes editor (port of upstream NotesWindow). Autosaves with an 800ms
/// debounce to the Games.Notes column (which rides the library backup). One window
/// per game id, app-wide — reopening re-focuses. Simplified from upstream's AvalonEdit
/// + floating roll-up/pin chrome to a themed window + multiline editor.
/// </summary>
public partial class NotesWindow : Window
{
    private readonly Game _game;
    private readonly DatabaseService _db = new();
    private readonly DispatcherTimer _saveTimer;
    private bool _suppressAutoSave;

    private static readonly Dictionary<int, NotesWindow> _open = new();

    public NotesWindow() : this(new Game { Title = "Game" }) { }

    /// <summary>Opens (or re-focuses) the notes window for a game.</summary>
    public static void ShowFor(Game game, Window? owner)
    {
        if (_open.TryGetValue(game.Id, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }
        var win = new NotesWindow(game);
        _open[game.Id] = win;
        win.Closed += (_, _) => _open.Remove(game.Id);
        if (owner != null) win.Show(owner); else win.Show();
        win.Activate();
    }

    public NotesWindow(Game game)
    {
        InitializeComponent();
        _game = game;
        this.FindControl<TextBlock>("TitleText")!.Text = $"Notes — {game.Title}";

        this.FindControl<Grid>("TitleBar")!.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        };
        this.FindControl<Button>("CloseBtn")!.Click += (_, _) => Close();

        var editor = this.FindControl<TextBox>("Editor")!;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); PersistNotes(); };

        // Load WITHOUT tripping autosave (the assignment raises TextChanged).
        _suppressAutoSave = true;
        editor.Text = game.Notes ?? "";
        _suppressAutoSave = false;

        editor.TextChanged += (_, _) =>
        {
            if (_suppressAutoSave) return;
            this.FindControl<TextBlock>("SaveHint")!.Text = "Saving…";
            _saveTimer.Stop();
            _saveTimer.Start();
        };

        Opened += (_, _) => editor.Focus();
    }

    private void PersistNotes()
    {
        string text = this.FindControl<TextBox>("Editor")!.Text ?? "";
        _game.Notes = text;          // live-updates the detail-card preview (UI thread)
        int id = _game.Id;
        _ = Task.Run(() =>
        {
            try { _db.UpdateNotes(id, text); } catch { }
            Dispatcher.UIThread.Post(() => { if (this.FindControl<TextBlock>("SaveHint") is { } h) h.Text = "Saved"; });
        });
    }

    protected override void OnClosed(EventArgs e) { _saveTimer.Stop(); PersistNotes(); base.OnClosed(e); }
}
