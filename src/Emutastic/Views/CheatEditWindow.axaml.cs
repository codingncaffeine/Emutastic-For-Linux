using Avalonia.Controls;
using Emutastic.Models;
using Emutastic.Services;

namespace Emutastic.Views;

/// <summary>Add/edit a single cheat. ShowDialog&lt;bool&gt; returns true on Save or Delete
/// (check DeleteRequested); Result holds the edited cheat.</summary>
public partial class CheatEditWindow : Window
{
    public Cheat Result { get; private set; } = new();
    public bool DeleteRequested { get; private set; }

    public CheatEditWindow() : this(null, "") { }

    public CheatEditWindow(Cheat? existing, string corePath)
    {
        InitializeComponent();

        var title = this.FindControl<TextBox>("TitleBox")!;
        var code = this.FindControl<TextBox>("CodeBox")!;
        var enabled = this.FindControl<CheckBox>("EnabledCheck")!;
        var header = this.FindControl<TextBlock>("HeaderTitle")!;
        var saveBtn = this.FindControl<Button>("SaveBtn")!;
        var deleteBtn = this.FindControl<Button>("DeleteBtn")!;

        var info = CheatSupport.Lookup(corePath);
        if (!string.IsNullOrEmpty(info.FormatHint))
            this.FindControl<TextBlock>("FormatHint")!.Text = $"Format: {info.FormatHint}"
                + (string.IsNullOrEmpty(info.Example) ? "" : $"   e.g. {info.Example}");
        if (!string.IsNullOrEmpty(info.Example)) code.PlaceholderText = info.Example;

        if (existing != null)
        {
            Title = "Edit Cheat"; header.Text = "Edit Cheat";
            title.Text = existing.Title; code.Text = existing.Code; enabled.IsChecked = existing.Enabled;
            saveBtn.Content = "Save"; deleteBtn.IsVisible = true;
        }
        else
        {
            Title = "Add Cheat"; header.Text = "Add Cheat"; enabled.IsChecked = true;
        }

        saveBtn.Click += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(title.Text) || string.IsNullOrWhiteSpace(code.Text))
            {
                await new ConfirmDialog("Cheat", "Title and Code are both required.", "OK", infoOnly: true).ShowDialog<bool>(this);
                return;
            }
            Result = new Cheat { Title = title.Text!.Trim(), Code = code.Text!.Trim(), Enabled = enabled.IsChecked == true };
            Close(true);
        };
        this.FindControl<Button>("CancelBtn")!.Click += (_, _) => Close(false);
        deleteBtn.Click += (_, _) => { DeleteRequested = true; Close(true); };

        Opened += (_, _) => title.Focus();
    }
}
