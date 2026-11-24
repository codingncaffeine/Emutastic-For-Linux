using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Emutastic.Models;
using Emutastic.Services;

namespace Emutastic.Views;

/// <summary>
/// Makes a Commodore 64 hard drive (a CMD HD) for one game: the user picks a size, the core prepares the drive
/// (its own DOS installed and partitions formatted, by the drive's own programs) off the UI thread with progress
/// shown here. ShowDialog&lt;string?&gt; returns a line for the status bar, or null if nothing was made.
/// </summary>
public partial class HardDriveWindow : Window
{
    private readonly CancellationTokenSource _cancel = new();
    private bool _busy;

    public HardDriveWindow() : this(null!, "") { }

    public HardDriveWindow(Game game, string corePath)
    {
        InitializeComponent();
        var info = this.FindControl<TextBlock>("InfoText")!;
        var sizeBox = this.FindControl<ComboBox>("SizeBox")!;
        var note = this.FindControl<TextBlock>("SizeNote")!;
        var warn = this.FindControl<TextBlock>("WarnText")!;
        var ok = this.FindControl<Button>("OkButton")!;
        var progress = this.FindControl<ProgressBar>("Progress")!;
        var status = this.FindControl<TextBlock>("StatusText")!;
        if (game == null) return;

        string path = C64HardDrive.PathFor(game);
        info.Text = $"A CMD HD hard drive for \"{game.Title}\": device 12, ready to use (SAVE\"NAME\",12 and " +
                    "LOAD\"NAME\",12 from BASIC; LOAD\"$\",12 lists it). It is set up with the drive's own programs, " +
                    "in 16 MB partitions (the largest the drive has). Hard drives are not part of the GitHub backup.";
        foreach (var (label, _) in C64HardDrive.Sizes) sizeBox.Items.Add(label);
        sizeBox.SelectedIndex = 0;
        void ShowNote()
        {
            long bytes = C64HardDrive.Sizes[Math.Max(0, sizeBox.SelectedIndex)].Bytes;
            long usable = Math.Min(bytes, 1L << 30);
            int parts = (int)Math.Ceiling((usable - 528 * 512.0) / (16_711_680.0));
            note.Text = bytes > (1L << 30)
                ? $"{parts} partitions. The drive's DOS reaches the first 1 GB of a disk; the rest stays unused."
                : $"{parts} partition{(parts == 1 ? "" : "s")}.";
        }
        sizeBox.SelectionChanged += (_, _) => ShowNote();
        ShowNote();

        string? missing = File.Exists(corePath) ? C64HardDrive.Missing(corePath) : null;
        if (missing == null) { warn.Text = "This version of the Commodore 64 core cannot make hard drives."; warn.IsVisible = true; ok.IsEnabled = false; }
        else if (missing.Length > 0)
        {
            warn.Text = $"Missing in System Files: {missing}. Add them in Preferences → System Files.";
            warn.IsVisible = true;
            ok.IsEnabled = false;
        }
        else if (File.Exists(path))
        {
            warn.Text = "This game has a hard drive already: making a new one replaces it and everything on it.";
            warn.IsVisible = true;
        }

        ok.Click += async (_, _) =>
        {
            if (_busy) return;
            _busy = true;
            ok.IsEnabled = false;
            sizeBox.IsEnabled = false;
            progress.IsVisible = status.IsVisible = true;
            status.Text = "Preparing the drive: installing its DOS, then making and formatting the partitions…";
            long bytes = C64HardDrive.Sizes[Math.Max(0, sizeBox.SelectedIndex)].Bytes;
            string tmp = path + ".new";
            try
            {
                int parts = await Task.Run(() =>
                {
                    try { File.Delete(tmp); } catch { }
                    int n = C64HardDrive.Make(corePath, tmp, bytes,
                        p => Dispatcher.UIThread.Post(() => progress.Value = p), _cancel.Token);
                    File.Move(tmp, path, overwrite: true);
                    return n;
                });
                Close($"Made a {C64HardDrive.Sizes[sizeBox.SelectedIndex].Label} hard drive for \"{game.Title}\" " +
                      $"({parts} partition{(parts == 1 ? "" : "s")}, device 12).");
            }
            catch (Exception ex)
            {
                try { File.Delete(tmp); } catch { }
                if (_cancel.IsCancellationRequested) { Close((string?)null); return; }
                status.Text = "The drive was not made: " + ex.Message;
                progress.IsVisible = false;
                _busy = false;
                ok.IsEnabled = sizeBox.IsEnabled = true;
            }
        };
        this.FindControl<Button>("CancelButton")!.Click += (_, _) =>
        {
            if (_busy) { _cancel.Cancel(); status.Text = "Cancelling…"; }
            else Close((string?)null);
        };
        Closing += (_, e) => { if (_busy) { _cancel.Cancel(); } };
    }
}
