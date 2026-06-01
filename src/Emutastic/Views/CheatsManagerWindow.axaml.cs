using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Emutastic.Models;
using Emutastic.Services;

namespace Emutastic.Views;

/// <summary>Per-game cheat manager (port of upstream CheatsManagerWindow). Edits the same
/// per-game cheats JSON the in-game overlay uses; changes apply on the next launch.</summary>
public partial class CheatsManagerWindow : Window
{
    private readonly Game _game;
    private readonly string _formatHintCorePath;
    private List<Cheat> _cheats;

    public CheatsManagerWindow() : this(new Game { Title = "Game", Console = "NES" }) { }

    public CheatsManagerWindow(Game game)
    {
        InitializeComponent();
        _game = game;
        _cheats = CheatService.Load(game);

        // No core loaded here — use the first preferred core for this console for the format hint.
        _formatHintCorePath = "";
        if (CoreManager.ConsoleCoreMap.TryGetValue(game.Console ?? "", out var cores) && cores.Length > 0)
            _formatHintCorePath = cores[0];

        this.FindControl<TextBlock>("HeaderTitle")!.Text = $"Cheats — {game.Title}";
        this.FindControl<TextBlock>("HeaderSubtitle")!.Text = "Cheats apply the next time you launch this game.";

        this.FindControl<Button>("AddBtn")!.Click += (_, _) => _ = OpenEditor(-1);
        this.FindControl<Button>("ImportBtn")!.Click += (_, _) => _ = ImportAsync();
        this.FindControl<Button>("CloseBtn")!.Click += (_, _) => Close();

        Refresh();
    }

    private void Refresh()
    {
        var list = this.FindControl<StackPanel>("CheatList")!;
        list.Children.Clear();
        this.FindControl<TextBlock>("EmptyHint")!.IsVisible = _cheats.Count == 0;

        for (int i = 0; i < _cheats.Count; i++)
        {
            var cheat = _cheats[i];
            int captured = i;

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("46,*,Auto") };

            // Pill toggle: knob right + accent when on, left + muted when off.
            var knob = new Border
            {
                Width = 14, Height = 14, CornerRadius = new CornerRadius(7), Background = Brushes.White,
                Margin = new Thickness(2, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = cheat.Enabled ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            };
            var toggle = new Border
            {
                Background = cheat.Enabled ? Brush("AccentBrush") : Brush("BgTertiaryBrush"),
                BorderBrush = Brush("BorderNormalBrush"), BorderThickness = new Thickness(1),
                Width = 34, Height = 18, CornerRadius = new CornerRadius(9), Cursor = new Cursor(StandardCursorType.Hand),
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Child = knob,
            };
            ToolTip.SetTip(toggle, cheat.Enabled ? "Click to disable" : "Click to enable");
            toggle.PointerPressed += (_, e) => { e.Handled = true; ToggleCheat(captured); };

            var label = new TextBlock
            {
                Text = cheat.Title, FontFamily = Font("PrimaryFont"),
                Foreground = Brush(cheat.Enabled ? "TextPrimaryBrush" : "TextSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var code = new TextBlock
            {
                Text = cheat.Code, FontFamily = "monospace", FontSize = 11, Foreground = Brush("TextMutedBrush"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 140,
            };
            Grid.SetColumn(toggle, 0); Grid.SetColumn(label, 1); Grid.SetColumn(code, 2);
            grid.Children.Add(toggle); grid.Children.Add(label); grid.Children.Add(code);

            var btn = new Button { Content = grid };
            btn.Classes.Add("cheatRow");
            btn.Click += (_, _) => _ = OpenEditor(captured);
            list.Children.Add(btn);
        }
    }

    private void ToggleCheat(int index)
    {
        if (index < 0 || index >= _cheats.Count) return;
        _cheats[index].Enabled = !_cheats[index].Enabled;
        CheatService.Save(_game, _cheats);
        Refresh();
    }

    private async System.Threading.Tasks.Task OpenEditor(int existingIndex)
    {
        Cheat? existing = (existingIndex >= 0 && existingIndex < _cheats.Count) ? _cheats[existingIndex] : null;
        var dlg = new CheatEditWindow(existing, _formatHintCorePath);
        bool ok = await dlg.ShowDialog<bool>(this);
        if (!ok) return;
        if (dlg.DeleteRequested && existingIndex >= 0) _cheats.RemoveAt(existingIndex);
        else if (existingIndex >= 0) _cheats[existingIndex] = dlg.Result;
        else _cheats.Add(dlg.Result);
        CheatService.Save(_game, _cheats);
        Refresh();
    }

    private async System.Threading.Tasks.Task ImportAsync()
    {
        if (!CheatDatabaseService.IsInstalled())
        {
            await new ConfirmDialog("Cheats Database",
                "The cheats database isn't installed yet.\n\nOpen Preferences → Cores / Extras and download it first.",
                "OK", infoOnly: true).ShowDialog<bool>(this);
            return;
        }
        var result = CheatDatabaseService.LookupForGame(_game);
        if (result == null || result.Cheats.Count == 0)
        {
            await new ConfirmDialog("Import Cheats", "No matching cheats found for this game in the database.", "OK", infoOnly: true).ShowDialog<bool>(this);
            return;
        }
        var existingCodes = new HashSet<string>(_cheats.Select(c => c.Code), System.StringComparer.OrdinalIgnoreCase);
        int added = 0;
        foreach (var c in result.Cheats)
        {
            if (existingCodes.Contains(c.Code)) continue;
            _cheats.Add(c); existingCodes.Add(c.Code); added++;
        }
        CheatService.Save(_game, _cheats);
        Refresh();
        await new ConfirmDialog("Import Cheats",
            added > 0 ? $"Imported {added} cheat{(added == 1 ? "" : "s")} — all disabled by default. Toggle the ones you want."
                      : "All matching cheats were already in your list.",
            "OK", infoOnly: true).ShowDialog<bool>(this);
    }

    private IBrush? Brush(string key) => this.TryFindResource(key, out var v) ? v as IBrush : null;
    private FontFamily Font(string key) => this.TryFindResource(key, out var v) && v is FontFamily f ? f : FontFamily.Default;
}
