using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;

namespace Emutastic.Configuration
{
    public static class ConfigurationExtensions
    {
        // Legacy support - convert old InputMapping to new ButtonMapping
        public static ButtonMapping ToButtonMapping(this Services.InputMapping oldMapping)
        {
            return new ButtonMapping
            {
                ButtonName = oldMapping.ButtonName,
                InputIdentifier = oldMapping.InputType == Services.InputType.Keyboard 
                    ? ((Key)oldMapping.Key).ToString() 
                    : oldMapping.ControllerButtonId.ToString(),
                InputType = oldMapping.InputType == Services.InputType.Keyboard 
                    ? InputType.Keyboard 
                    : InputType.Controller,
                DisplayName = oldMapping.DisplayText,
                ModifierKeys = 0 // TODO: Extract modifier keys if needed
            };
        }

        // Convert new ButtonMapping back to legacy InputMapping
        public static Services.InputMapping ToLegacyInputMapping(this ButtonMapping newMapping)
        {
            return new Services.InputMapping
            {
                ButtonName = newMapping.ButtonName,
                InputType = newMapping.InputType == InputType.Keyboard 
                    ? Services.InputType.Keyboard 
                    : Services.InputType.Controller,
                Key = newMapping.InputType == InputType.Keyboard && Enum.TryParse<Key>(newMapping.InputIdentifier, out var key)
                    ? key 
                    : Key.None,
                ControllerButtonId = newMapping.InputType == InputType.Controller && uint.TryParse(newMapping.InputIdentifier, out var buttonId)
                    ? buttonId 
                    : 0,
                DisplayText = newMapping.DisplayName
            };
        }

        // Get default keyboard mappings for a console
        public static List<ButtonMapping> GetDefaultKeyboardMappings(string consoleName)
        {
            return consoleName switch
            {
                "NES" => new List<ButtonMapping>
                {
                    new() { ButtonName = "Up", InputIdentifier = "Up", InputType = InputType.Keyboard, DisplayName = "↑" },
                    new() { ButtonName = "Down", InputIdentifier = "Down", InputType = InputType.Keyboard, DisplayName = "↓" },
                    new() { ButtonName = "Left", InputIdentifier = "Left", InputType = InputType.Keyboard, DisplayName = "←" },
                    new() { ButtonName = "Right", InputIdentifier = "Right", InputType = InputType.Keyboard, DisplayName = "→" },
                    new() { ButtonName = "Select", InputIdentifier = "RightShift", InputType = InputType.Keyboard, DisplayName = "Right Shift" },
                    new() { ButtonName = "Start", InputIdentifier = "Return", InputType = InputType.Keyboard, DisplayName = "Enter" },
                    new() { ButtonName = "B", InputIdentifier = "Z", InputType = InputType.Keyboard, DisplayName = "Z" },
                    new() { ButtonName = "A", InputIdentifier = "X", InputType = InputType.Keyboard, DisplayName = "X" },
                },
                "SNES" => new List<ButtonMapping>
                {
                    new() { ButtonName = "Up", InputIdentifier = "Up", InputType = InputType.Keyboard, DisplayName = "↑" },
                    new() { ButtonName = "Down", InputIdentifier = "Down", InputType = InputType.Keyboard, DisplayName = "↓" },
                    new() { ButtonName = "Left", InputIdentifier = "Left", InputType = InputType.Keyboard, DisplayName = "←" },
                    new() { ButtonName = "Right", InputIdentifier = "Right", InputType = InputType.Keyboard, DisplayName = "→" },
                    new() { ButtonName = "Select", InputIdentifier = "RightShift", InputType = InputType.Keyboard, DisplayName = "Right Shift" },
                    new() { ButtonName = "Start", InputIdentifier = "Return", InputType = InputType.Keyboard, DisplayName = "Enter" },
                    new() { ButtonName = "Y", InputIdentifier = "A", InputType = InputType.Keyboard, DisplayName = "A" },
                    new() { ButtonName = "X", InputIdentifier = "S", InputType = InputType.Keyboard, DisplayName = "S" },
                    new() { ButtonName = "B", InputIdentifier = "Z", InputType = InputType.Keyboard, DisplayName = "Z" },
                    new() { ButtonName = "A", InputIdentifier = "X", InputType = InputType.Keyboard, DisplayName = "X" },
                    new() { ButtonName = "L", InputIdentifier = "Q", InputType = InputType.Keyboard, DisplayName = "Q" },
                    new() { ButtonName = "R", InputIdentifier = "W", InputType = InputType.Keyboard, DisplayName = "W" },
                },
                "2600" => new List<ButtonMapping>
                {
                    new() { ButtonName = "Up", InputIdentifier = "Up", InputType = InputType.Keyboard, DisplayName = "↑" },
                    new() { ButtonName = "Down", InputIdentifier = "Down", InputType = InputType.Keyboard, DisplayName = "↓" },
                    new() { ButtonName = "Left", InputIdentifier = "Left", InputType = InputType.Keyboard, DisplayName = "←" },
                    new() { ButtonName = "Right", InputIdentifier = "Right", InputType = InputType.Keyboard, DisplayName = "→" },
                    new() { ButtonName = "Fire", InputIdentifier = "Z", InputType = InputType.Keyboard, DisplayName = "Z" },
                    new() { ButtonName = "Select", InputIdentifier = "RightShift", InputType = InputType.Keyboard, DisplayName = "Right Shift" },
                    new() { ButtonName = "Reset", InputIdentifier = "Return", InputType = InputType.Keyboard, DisplayName = "Enter" },
                },
                "Genesis" => new List<ButtonMapping>
                {
                    new() { ButtonName = "Up", InputIdentifier = "Up", InputType = InputType.Keyboard, DisplayName = "↑" },
                    new() { ButtonName = "Down", InputIdentifier = "Down", InputType = InputType.Keyboard, DisplayName = "↓" },
                    new() { ButtonName = "Left", InputIdentifier = "Left", InputType = InputType.Keyboard, DisplayName = "←" },
                    new() { ButtonName = "Right", InputIdentifier = "Right", InputType = InputType.Keyboard, DisplayName = "→" },
                    new() { ButtonName = "Select", InputIdentifier = "RightShift", InputType = InputType.Keyboard, DisplayName = "Right Shift" },
                    new() { ButtonName = "Start", InputIdentifier = "Return", InputType = InputType.Keyboard, DisplayName = "Enter" },
                    new() { ButtonName = "A", InputIdentifier = "Z", InputType = InputType.Keyboard, DisplayName = "Z" },
                    new() { ButtonName = "B", InputIdentifier = "X", InputType = InputType.Keyboard, DisplayName = "X" },
                    new() { ButtonName = "C", InputIdentifier = "C", InputType = InputType.Keyboard, DisplayName = "C" },
                },
                "N64" => new List<ButtonMapping>
                {
                    new() { ButtonName = "Up", InputIdentifier = "Up", InputType = InputType.Keyboard, DisplayName = "↑" },
                    new() { ButtonName = "Down", InputIdentifier = "Down", InputType = InputType.Keyboard, DisplayName = "↓" },
                    new() { ButtonName = "Left", InputIdentifier = "Left", InputType = InputType.Keyboard, DisplayName = "←" },
                    new() { ButtonName = "Right", InputIdentifier = "Right", InputType = InputType.Keyboard, DisplayName = "→" },
                    new() { ButtonName = "Select", InputIdentifier = "RightShift", InputType = InputType.Keyboard, DisplayName = "Right Shift" },
                    new() { ButtonName = "Start", InputIdentifier = "Return", InputType = InputType.Keyboard, DisplayName = "Enter" },
                    new() { ButtonName = "A", InputIdentifier = "Z", InputType = InputType.Keyboard, DisplayName = "Z" },
                    new() { ButtonName = "B", InputIdentifier = "X", InputType = InputType.Keyboard, DisplayName = "X" },
                    new() { ButtonName = "Z", InputIdentifier = "C", InputType = InputType.Keyboard, DisplayName = "C" },
                    new() { ButtonName = "L", InputIdentifier = "Q", InputType = InputType.Keyboard, DisplayName = "Q" },
                    new() { ButtonName = "R", InputIdentifier = "W", InputType = InputType.Keyboard, DisplayName = "W" },
                    new() { ButtonName = "C Up", InputIdentifier = "I", InputType = InputType.Keyboard, DisplayName = "I" },
                    new() { ButtonName = "C Down", InputIdentifier = "K", InputType = InputType.Keyboard, DisplayName = "K" },
                    new() { ButtonName = "C Left", InputIdentifier = "J", InputType = InputType.Keyboard, DisplayName = "J" },
                    new() { ButtonName = "C Right", InputIdentifier = "L", InputType = InputType.Keyboard, DisplayName = "L" },
                },
                "Saturn" or "SegaCD" or "Sega32X" => new List<ButtonMapping>
                {
                    new() { ButtonName = "Up",     InputIdentifier = "Up",         InputType = InputType.Keyboard, DisplayName = "↑" },
                    new() { ButtonName = "Down",   InputIdentifier = "Down",       InputType = InputType.Keyboard, DisplayName = "↓" },
                    new() { ButtonName = "Left",   InputIdentifier = "Left",       InputType = InputType.Keyboard, DisplayName = "←" },
                    new() { ButtonName = "Right",  InputIdentifier = "Right",      InputType = InputType.Keyboard, DisplayName = "→" },
                    new() { ButtonName = "Start",  InputIdentifier = "Return",     InputType = InputType.Keyboard, DisplayName = "Enter" },
                    new() { ButtonName = "A",      InputIdentifier = "Z",          InputType = InputType.Keyboard, DisplayName = "Z" },
                    new() { ButtonName = "B",      InputIdentifier = "X",          InputType = InputType.Keyboard, DisplayName = "X" },
                    new() { ButtonName = "C",      InputIdentifier = "C",          InputType = InputType.Keyboard, DisplayName = "C" },
                    new() { ButtonName = "X",      InputIdentifier = "A",          InputType = InputType.Keyboard, DisplayName = "A" },
                    new() { ButtonName = "Y",      InputIdentifier = "S",          InputType = InputType.Keyboard, DisplayName = "S" },
                    new() { ButtonName = "Z",      InputIdentifier = "D",          InputType = InputType.Keyboard, DisplayName = "D" },
                    new() { ButtonName = "L",      InputIdentifier = "Q",          InputType = InputType.Keyboard, DisplayName = "Q" },
                    new() { ButtonName = "R",      InputIdentifier = "W",          InputType = InputType.Keyboard, DisplayName = "W" },
                },
                // Add more console defaults as needed
                _ => GetDefaultKeyboardMappings("NES") // Default to NES layout
            };
        }

        // Get default controller mappings for a console
        public static List<ButtonMapping> GetDefaultControllerMappings(string consoleName)
        {
            // Derive defaults from the console's button list + the canonical libretro→SDL raw-id
            // table, so the saved ids live in the SAME space SdlInput.ReadRawControl reads (and that
            // the Controls panel's live capture produces). Hand-maintained per-console literals had
            // drifted into the legacy XInput index space and mis-mapped every pad on save.
            var result = new List<ButtonMapping>();
            if (!ControllerDefinitions.AllControllers.TryGetValue(consoleName, out var def))
                ControllerDefinitions.AllControllers.TryGetValue("NES", out def);
            if (def == null) return result;

            foreach (var b in def.Buttons)
            {
                uint lib = Services.LibretroInput.GetButtonId(b.Name, consoleName);
                int raw = LibretroIdToSdlRaw(lib);
                if (raw < 0) continue;   // unknown / unmapped button name
                result.Add(new ButtonMapping
                {
                    ButtonName = b.Name, InputIdentifier = raw.ToString(),
                    InputType = InputType.Controller, DisplayName = SdlRawLabel(raw),
                });
            }
            return result;
        }

        // libretro RETRO_DEVICE_ID_JOYPAD/ANALOG id → SDL raw control id (SdlInput / ControllerManager
        // space): 0..20 SDL_GamepadButton, 100/101 L2/R2 triggers, 110..117 L/R stick directions.
        private static int LibretroIdToSdlRaw(uint id) => id switch
        {
            0  => 0,    // B      → SDL South (Xbox A)
            1  => 2,    // Y      → SDL West  (Xbox X)
            2  => 4,    // Select → Back
            3  => 6,    // Start
            4  => 11,   // Up
            5  => 12,   // Down
            6  => 13,   // Left
            7  => 14,   // Right
            8  => 1,    // A      → SDL East  (Xbox B)
            9  => 3,    // X      → SDL North (Xbox Y)
            10 => 9,    // L      → Left shoulder
            11 => 10,   // R      → Right shoulder
            12 => 100,  // L2     → Left trigger
            13 => 101,  // R2     → Right trigger
            14 => 7,    // L3     → Left stick click
            15 => 8,    // R3     → Right stick click
            16 => 112, 17 => 113, 18 => 110, 19 => 111,   // analog left  (U,D,L,R)
            20 => 116, 21 => 117, 22 => 114, 23 => 115,   // analog right (U,D,L,R) — N64 C-buttons
            _  => -1,
        };

        private static string SdlRawLabel(int raw) => raw switch
        {
            0 => "A", 1 => "B", 2 => "X", 3 => "Y", 4 => "Back", 6 => "Start",
            7 => "L3", 8 => "R3", 9 => "LB", 10 => "RB",
            11 => "D-Pad Up", 12 => "D-Pad Down", 13 => "D-Pad Left", 14 => "D-Pad Right",
            100 => "LT", 101 => "RT",
            110 => "L-Stick ←", 111 => "L-Stick →", 112 => "L-Stick ↑", 113 => "L-Stick ↓",
            114 => "R-Stick ←", 115 => "R-Stick →", 116 => "R-Stick ↑", 117 => "R-Stick ↓",
            _ => $"Button {raw}",
        };

        // Validate and fix button mappings
        public static void ValidateMappings(this InputConfiguration config)
        {
            var controllerDef = ControllerDefinitions.GetControllerDefinition(config.ConsoleName);
            if (controllerDef == null) return;

            // Remove mappings for buttons that don't exist
            config.KeyboardMappings.RemoveAll(m => !controllerDef.Buttons.Any(b => b.Name == m.ButtonName));
            config.ControllerMappings.RemoveAll(m => !controllerDef.Buttons.Any(b => b.Name == m.ButtonName));

            // Add missing mappings with defaults
            foreach (var button in controllerDef.Buttons)
            {
                if (!config.KeyboardMappings.Any(m => m.ButtonName == button.Name))
                {
                    var defaultMappings = GetDefaultKeyboardMappings(config.ConsoleName);
                    var defaultMapping = defaultMappings.FirstOrDefault(m => m.ButtonName == button.Name);
                    if (defaultMapping != null)
                    {
                        config.KeyboardMappings.Add(defaultMapping);
                    }
                }

                if (!config.ControllerMappings.Any(m => m.ButtonName == button.Name))
                {
                    var defaultMappings = GetDefaultControllerMappings(config.ConsoleName);
                    var defaultMapping = defaultMappings.FirstOrDefault(m => m.ButtonName == button.Name);
                    if (defaultMapping != null)
                    {
                        config.ControllerMappings.Add(defaultMapping);
                    }
                }
            }
        }
    }
}
