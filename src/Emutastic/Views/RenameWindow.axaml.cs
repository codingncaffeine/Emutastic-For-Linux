using Avalonia.Controls;
using Avalonia.Input;

namespace Emutastic.Views;

/// <summary>Rename dialog. ShowDialog&lt;string?&gt; returns the new title, or null on cancel.</summary>
public partial class RenameWindow : Window
{
    public RenameWindow() : this("") { }

    public RenameWindow(string currentTitle)
    {
        InitializeComponent();
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
