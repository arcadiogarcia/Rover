using System;

namespace zRover.Core.Tools.InputInjection
{
    /// <summary>
    /// Platform-neutral virtual key name resolver. Returns the ushort VK_ code that can be
    /// cast directly to <c>Windows.System.VirtualKey</c> on both UWP and WinUI 3.
    /// Kept in Core so neither platform project needs to duplicate the mapping table.
    /// </summary>
    public static class VirtualKeyHelper
    {
        /// <summary>
        /// Converts a key name string to its Windows VK_ code.
        /// Accepts Windows VirtualKey enum names (case-insensitive) as well as common aliases.
        /// </summary>
        /// <exception cref="ArgumentException">Thrown when the key name is unrecognised.</exception>
        public static ushort ParseVirtualKey(string keyName)
        {
            if (string.IsNullOrEmpty(keyName))
                throw new ArgumentException("Key name must not be empty.", nameof(keyName));

            // Common aliases that differ from or supplement the VirtualKey enum names
            switch (keyName.ToLowerInvariant())
            {
                case "ctrl":       return 17;  // VK_CONTROL
                case "alt":        return 18;  // VK_MENU
                case "win":        return 91;  // VK_LWIN
                case "backspace":  return 8;   // VK_BACK
                case "return":     return 13;  // VK_RETURN
                case "esc":        return 27;  // VK_ESCAPE
                case "del":        return 46;  // VK_DELETE
                case "ins":        return 45;  // VK_INSERT
                case "pgup":       return 33;  // VK_PRIOR
                case "pgdn":
                case "pgdown":     return 34;  // VK_NEXT
                case "up":         return 38;  // VK_UP
                case "down":       return 40;  // VK_DOWN
                case "left":       return 37;  // VK_LEFT
                case "right":      return 39;  // VK_RIGHT
                case "space":      return 32;  // VK_SPACE

                // Named keys matching Windows.System.VirtualKey members
                case "enter":      return 13;
                case "tab":        return 9;
                case "escape":     return 27;
                case "back":       return 8;
                case "delete":     return 46;
                case "insert":     return 45;
                case "home":       return 36;
                case "end":        return 35;
                case "pageup":     return 33;
                case "pagedown":   return 34;
                case "control":    return 17;
                case "menu":       return 18;
                case "shift":      return 16;
                case "leftshift":  return 160;
                case "rightshift": return 161;
                case "leftcontrol":  return 162;
                case "rightcontrol": return 163;
                case "leftmenu":   return 164;
                case "rightmenu":  return 165;
                case "leftwindows":  return 91;
                case "rightwindows": return 92;
                case "capital":    return 20;
                case "numlock":    return 144;
                case "scroll":     return 145;
                case "snapshot":   return 44;
                case "pause":      return 19;
                case "print":      return 42;
                case "f1":  return 112; case "f2":  return 113; case "f3":  return 114;
                case "f4":  return 115; case "f5":  return 116; case "f6":  return 117;
                case "f7":  return 118; case "f8":  return 119; case "f9":  return 120;
                case "f10": return 121; case "f11": return 122; case "f12": return 123;
                case "f13": return 124; case "f14": return 125; case "f15": return 126;
                case "f16": return 127; case "f17": return 128; case "f18": return 129;
                case "f19": return 130; case "f20": return 131; case "f21": return 132;
                case "f22": return 133; case "f23": return 134; case "f24": return 135;
                case "multiply":   return 106;
                case "add":        return 107;
                case "separator":  return 108;
                case "subtract":   return 109;
                case "decimal":    return 110;
                case "divide":     return 111;
                case "number0": case "numpad0": return 96;
                case "number1": case "numpad1": return 97;
                case "number2": case "numpad2": return 98;
                case "number3": case "numpad3": return 99;
                case "number4": case "numpad4": return 100;
                case "number5": case "numpad5": return 101;
                case "number6": case "numpad6": return 102;
                case "number7": case "numpad7": return 103;
                case "number8": case "numpad8": return 104;
                case "number9": case "numpad9": return 105;
            }

            // Single letter (A-Z) → VK_A = 65, VK_Z = 90
            if (keyName.Length == 1)
            {
                char ch = char.ToUpperInvariant(keyName[0]);
                if (ch >= 'A' && ch <= 'Z')
                    return (ushort)ch;
                // Digit 0-9 → VK_0 = 48 … VK_9 = 57
                if (ch >= '0' && ch <= '9')
                    return (ushort)ch;
            }

            throw new ArgumentException($"Unknown virtual key: '{keyName}'");
        }
    }
}
