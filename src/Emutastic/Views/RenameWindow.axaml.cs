using Avalonia.Controls;
using Avalonia.Input;

namespace Emutastic.Views;

/// <summary>Rename dialog. ShowDialog&lt;string?&gt; returns the new title, or null on cancel.</summary>
public partial class RenameWindow : Window
{
    public RenameWindow() : this("") { }

    /// <param name="heading">Replaces the window's "Rename game" title and heading.</param>
    /// <param name="okLabel">Replaces the "Rename" button's text.</param>
    public RenameWindow(string currentTitle, string? heading = null, string? okLabel = null)
    {
        InitializeComponent();
        if (heading != null) { Title = heading; this.FindControl<TextBlock>("HeadingText")!.Text = heading; }
        if (okLabel != null) this.FindControl<Button>("OkButton")!.Content = okLabel;
        var box = this.FindControl<TextBox>("TitleBox")!;
        box.Text = currentTitle;
        box.SelectAll();

        void Accept()
        {
            var t = box.Text?.Trim();
            Close(string.IsNullOrEmpty(t) ? null : t);
        }
        this.FindControl<Button>("OkButton")!.Click += (_, _) => Accept();
        this.FindControl<Button>("CancelButton")!.Click += (_, _) => Close((string?)null);
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Accept(); else if (e.Key == Key.Escape) Close((string?)null); };
    }
}
