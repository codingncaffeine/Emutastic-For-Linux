using Avalonia.Input;

namespace Emutastic.Services
{
    // Extracted from DatabaseService.cs during the Linux port: InputMapping/InputType
    // are plain DTOs that several layers (Configuration, input UI) depend on, so they
    // live in their own file here rather than buried in the SQLite service. When
    // DatabaseService.cs is ported (M3), its inline copies of these two types are removed.
    // Note: the Key property is Avalonia.Input.Key (was System.Windows.Input.Key upstream).
    public class InputMapping
    {
        public string ConsoleName { get; set; } = "";
        public string ButtonName { get; set; } = "";
        public InputType InputType { get; set; }
        public Key Key { get; set; }
        public uint ControllerButtonId { get; set; }
        public string DisplayText { get; set; } = "";
        public bool IsSelected { get; set; }
        // For chord mappings (e.g. "Disk Swap" = L3 + Start). Format: "A+B" where A
        // and B are either two key names (keyboard) or two controller-button ids.
        // Null/empty for single-input mappings.
        public string? ChordIdentifier { get; set; }
    }

    public enum InputType { Keyboard, Controller }
}
