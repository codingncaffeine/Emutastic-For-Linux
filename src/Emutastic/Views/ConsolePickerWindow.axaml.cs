using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Styling;

namespace Emutastic.Views;

/// <summary>
/// Ambiguous-import disambiguation dialog: when a ROM (.bin/.iso/.chd) can't be matched against a DAT,
/// asks which system it's for. ShowDialog&lt;string?&gt; returns the chosen console tag, or null on cancel.
/// </summary>
public partial class ConsolePickerWindow : Window
{
    private static readonly Dictionary<string, string> Labels = new()
    {
        { "SegaCD", "Sega CD" }, { "Saturn", "Sega Saturn" }, { "Dreamcast", "Sega Dreamcast" },
        { "PS1", "PlayStation" }, { "PSP", "PlayStation Portable" }, { "TGCD", "TurboGrafx-CD" },
        { "GameCube", "Nintendo GameCube" }, { "3DO", "3DO" },
    };

    // Parameterless ctor for the XAML designer.
    public ConsolePickerWindow() : this("game", new[] { "PS1", "SegaCD" }) { }

    public ConsolePickerWindow(string fileName, string[] candidates)
    {
        InitializeComponent();

        this.FindControl<TextBlock>("Msg")!.Text =
            $"\"{fileName}\" wasn't found in any known game database. Which system is it for?";

        var panel = this.FindControl<StackPanel>("ButtonsPanel")!;
        foreach (string tag in candidates)
        {
            var btn = new Button
            {
                Content = Labels.TryGetValue(tag, out var lbl) ? lbl : tag,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Tag = tag,
            };
            if (this.TryFindResource("PrimaryButtonStyle", out var theme) && theme is ControlTheme ct)
                btn.Theme = ct;
            btn.Click += (_, _) => Close((string?)btn.Tag);
            panel.Children.Add(btn);
        }

        this.FindControl<Button>("CancelButton")!.Click += (_, _) => Close((string?)null);
    }
}
