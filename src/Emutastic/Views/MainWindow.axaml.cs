using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Emutastic.Emulator;

namespace Emutastic.Views;

/// <summary>
/// Throwaway M2 launcher: picks a core + ROM and opens an <see cref="EmulatorWindow"/>.
/// Replaced by the real 1:1 library-browser shell in M4.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        var corePath = this.FindControl<TextBox>("CorePath")!;
        var romPath = this.FindControl<TextBox>("RomPath")!;
        var status = this.FindControl<TextBlock>("Status")!;

        // Convenience defaults: the spike's downloaded core/ROM if present.
        string spike = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Projects/emutastic-linux/spike");
        string defCore = Path.Combine(spike, "cores/nestopia_libretro.so");
        string defRom = Path.Combine(spike, "roms/full_palette.nes");
        if (File.Exists(defCore)) corePath.Text = defCore;
        if (File.Exists(defRom)) romPath.Text = defRom;

        this.FindControl<Button>("BrowseCore")!.Click += async (_, _) =>
            corePath.Text = await PickFile("Select libretro core", "*.so") ?? corePath.Text;
        this.FindControl<Button>("BrowseRom")!.Click += async (_, _) =>
            romPath.Text = await PickFile("Select ROM", "*") ?? romPath.Text;

        this.FindControl<Button>("Launch")!.Click += (_, _) =>
        {
            status.Text = "";
            string core = corePath.Text ?? "", rom = romPath.Text ?? "";
            if (!File.Exists(core)) { status.Text = "Core not found: " + core; return; }
            if (!File.Exists(rom)) { status.Text = "ROM not found: " + rom; return; }
            new EmulatorWindow(new EmulatorSession(core, rom)).Show();
        };
    }

    private async Task<string?> PickFile(string title, string pattern)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType(pattern) { Patterns = new[] { pattern } } },
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
