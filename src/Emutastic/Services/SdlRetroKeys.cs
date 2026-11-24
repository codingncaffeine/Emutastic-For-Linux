namespace Emutastic.Services
{
    /// <summary>
    /// SDL scancodes (key positions, USB HID usage numbers) to libretro RETROK_* codes, as a US keyboard labels
    /// those positions. A core that reads the keyboard as a computer's keyboard (Kernal64) maps keys by position,
    /// so the position is what is passed on, whatever the player's layout prints on the key.
    /// </summary>
    public static class SdlRetroKeys
    {
        public const int Count = 512;   // RETROK_* codes stay below RETROK_LAST (324)

        /// <summary>RETROK code for an SDL scancode, or 0 when the key has none.</summary>
        public static uint ToRetro(int scancode)
        {
            if (scancode >= 4 && scancode <= 29) return (uint)('a' + scancode - 4);    // A..Z
            if (scancode >= 30 && scancode <= 38) return (uint)('1' + scancode - 30);  // 1..9
            if (scancode >= 58 && scancode <= 69) return 282u + (uint)(scancode - 58); // F1..F12
            return scancode switch
            {
                39 => '0', 40 => 13, 41 => 27, 42 => 8, 43 => 9, 44 => 32,   // 0 RETURN ESCAPE BACKSPACE TAB SPACE
                45 => '-', 46 => '=', 47 => '[', 48 => ']', 49 => '\\',
                51 => ';', 52 => '\'', 53 => '`', 54 => ',', 55 => '.', 56 => '/',
                57 => 301,                                                     // CAPSLOCK
                70 => 316, 71 => 302, 72 => 19,                                // PRINT SCROLLOCK PAUSE
                73 => 277, 74 => 278, 75 => 280, 76 => 127, 77 => 279, 78 => 281,   // INSERT HOME PAGEUP DELETE END PAGEDOWN
                79 => 275, 80 => 276, 81 => 274, 82 => 273,                    // RIGHT LEFT DOWN UP
                88 => 271,                                                     // KP_ENTER
                224 => 306, 225 => 304, 226 => 308, 227 => 311,                // LCTRL LSHIFT LALT LSUPER
                228 => 305, 229 => 303, 230 => 307, 231 => 312,                // RCTRL RSHIFT RALT RSUPER
                _ => 0,
            };
        }
    }
}
