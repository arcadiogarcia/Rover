using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Rover.FullTrust.McpServer;

/// <summary>
/// Provides real input injection via Win32 SendInput API.
/// This bypasses the UWP InputInjector which fails on many configurations.
/// Runs in the FullTrust process which has full Win32 access.
/// </summary>
internal sealed class Win32InputInjector
{
    #region P/Invoke

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern short VkKeyScan(char ch);

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const int INPUT_MOUSE = 0;
    private const int INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_HWHEEL = 0x1000;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT
    {
        [FieldOffset(0)] public int type;
        [FieldOffset(8)] public MOUSEINPUT mi;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    #endregion

    private readonly string _windowTitle;

    public Win32InputInjector(string windowTitle = "Rover Sample")
    {
        _windowTitle = windowTitle;
    }

    /// <summary>
    /// Brings the target UWP window to the foreground via SetForegroundWindow.
    /// Returns true if the window was found and the call succeeded.
    /// </summary>
    public bool BringToForeground()
    {
        IntPtr hwnd = FindWindow("ApplicationFrameWindow", _windowTitle);
        if (hwnd == IntPtr.Zero)
            hwnd = FindWindow(null, _windowTitle);
        if (hwnd == IntPtr.Zero)
            return false;
        return SetForegroundWindow(hwnd);
    }

    /// <summary>
    /// Finds the target UWP window and returns its screen-pixel bounds.
    /// Returns null if the window is not found.
    /// </summary>
    private RECT? GetWindowBounds()
    {
        // UWP apps use "ApplicationFrameWindow" as their window class
        IntPtr hwnd = FindWindow("ApplicationFrameWindow", _windowTitle);
        if (hwnd == IntPtr.Zero)
        {
            Console.Error.WriteLine("[Win32Input] Window not found, trying without class name...");
            hwnd = FindWindow(null, _windowTitle);
        }

        if (hwnd == IntPtr.Zero)
        {
            Console.Error.WriteLine($"[Win32Input] Could not find window '{_windowTitle}'");
            return null;
        }

        if (!GetWindowRect(hwnd, out RECT rect))
        {
            Console.Error.WriteLine("[Win32Input] GetWindowRect failed");
            return null;
        }

        return rect;
    }

    /// <summary>
    /// Converts normalized coordinates (0-1) to absolute screen coordinates
    /// suitable for SendInput (0-65535 range).
    /// </summary>
    private (int absX, int absY)? NormalizedToAbsolute(double normX, double normY, string coordinateSpace)
    {
        if (coordinateSpace == "absolute")
        {
            int screenW = GetSystemMetrics(SM_CXSCREEN);
            int screenH = GetSystemMetrics(SM_CYSCREEN);
            int absX = (int)(normX / screenW * 65535);
            int absY = (int)(normY / screenH * 65535);
            return (absX, absY);
        }

        var bounds = GetWindowBounds();
        if (bounds == null) return null;
        var rect = bounds.Value;

        int winW = rect.Right - rect.Left;
        int winH = rect.Bottom - rect.Top;

        double screenX, screenY;
        if (coordinateSpace == "client")
        {
            screenX = rect.Left + normX;
            screenY = rect.Top + normY;
        }
        else // normalized (default)
        {
            screenX = rect.Left + normX * winW;
            screenY = rect.Top + normY * winH;
        }

        int sw = GetSystemMetrics(SM_CXSCREEN);
        int sh = GetSystemMetrics(SM_CYSCREEN);

        return ((int)(screenX / sw * 65535), (int)(screenY / sh * 65535));
    }

    /// <summary>Injects a tap (mouse click) at the specified position.</summary>
    public bool InjectTap(double x, double y, string coordinateSpace = "normalized")
    {
        var abs = NormalizedToAbsolute(x, y, coordinateSpace);
        if (abs == null) return false;

        var (absX, absY) = abs.Value;

        var inputs = new INPUT[]
        {
            new INPUT
            {
                type = INPUT_MOUSE,
                mi = new MOUSEINPUT
                {
                    dx = absX,
                    dy = absY,
                    dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE
                }
            },
            new INPUT
            {
                type = INPUT_MOUSE,
                mi = new MOUSEINPUT
                {
                    dx = absX,
                    dy = absY,
                    dwFlags = MOUSEEVENTF_LEFTDOWN | MOUSEEVENTF_ABSOLUTE
                }
            },
            new INPUT
            {
                type = INPUT_MOUSE,
                mi = new MOUSEINPUT
                {
                    dx = absX,
                    dy = absY,
                    dwFlags = MOUSEEVENTF_LEFTUP | MOUSEEVENTF_ABSOLUTE
                }
            }
        };

        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        Console.Error.WriteLine($"[Win32Input] Tap at ({x:F3},{y:F3}) → screen abs ({absX},{absY}), sent={sent}");
        return sent == (uint)inputs.Length;
    }

    /// <summary>Injects a drag gesture along a path of points.</summary>
    public async Task<bool> InjectDragPath(
        List<(double x, double y)> points,
        int durationMs = 300,
        string coordinateSpace = "normalized")
    {
        if (points.Count < 2) return false;

        // Convert all points to absolute coordinates
        var absPoints = new List<(int absX, int absY)>();
        foreach (var (px, py) in points)
        {
            var abs = NormalizedToAbsolute(px, py, coordinateSpace);
            if (abs == null) return false;
            absPoints.Add(abs.Value);
        }

        // Interpolate between waypoints for smoother drag
        int totalSteps = Math.Max(20, durationMs / 10);
        var interpolated = InterpolatePoints(absPoints, totalSteps);
        int delayMs = Math.Max(1, durationMs / interpolated.Count);

        // Move to start position
        var moveToStart = new INPUT[]
        {
            new INPUT
            {
                type = INPUT_MOUSE,
                mi = new MOUSEINPUT
                {
                    dx = interpolated[0].absX,
                    dy = interpolated[0].absY,
                    dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE
                }
            }
        };
        SendInput(1, moveToStart, Marshal.SizeOf<INPUT>());
        await Task.Delay(10);

        // Press down at start
        var downInput = new INPUT[]
        {
            new INPUT
            {
                type = INPUT_MOUSE,
                mi = new MOUSEINPUT
                {
                    dx = interpolated[0].absX,
                    dy = interpolated[0].absY,
                    dwFlags = MOUSEEVENTF_LEFTDOWN | MOUSEEVENTF_ABSOLUTE
                }
            }
        };
        SendInput(1, downInput, Marshal.SizeOf<INPUT>());

        // Move through intermediate points
        for (int i = 1; i < interpolated.Count - 1; i++)
        {
            var moveInput = new INPUT[]
            {
                new INPUT
                {
                    type = INPUT_MOUSE,
                    mi = new MOUSEINPUT
                    {
                        dx = interpolated[i].absX,
                        dy = interpolated[i].absY,
                        dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE
                    }
                }
            };
            SendInput(1, moveInput, Marshal.SizeOf<INPUT>());
            await Task.Delay(delayMs);
        }

        // Release at end point
        var last = interpolated[interpolated.Count - 1];
        var upInput = new INPUT[]
        {
            new INPUT
            {
                type = INPUT_MOUSE,
                mi = new MOUSEINPUT
                {
                    dx = last.absX,
                    dy = last.absY,
                    dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE
                }
            },
            new INPUT
            {
                type = INPUT_MOUSE,
                mi = new MOUSEINPUT
                {
                    dx = last.absX,
                    dy = last.absY,
                    dwFlags = MOUSEEVENTF_LEFTUP | MOUSEEVENTF_ABSOLUTE
                }
            }
        };
        SendInput((uint)upInput.Length, upInput, Marshal.SizeOf<INPUT>());

        Console.Error.WriteLine($"[Win32Input] Drag {points.Count} waypoints, {interpolated.Count} steps, {durationMs}ms");
        return true;
    }

    private static List<(int absX, int absY)> InterpolatePoints(
        List<(int absX, int absY)> waypoints, int totalSteps)
    {
        if (waypoints.Count == 2)
        {
            // Simple linear interpolation between two points
            var result = new List<(int, int)>();
            var (x0, y0) = waypoints[0];
            var (x1, y1) = waypoints[1];
            for (int i = 0; i <= totalSteps; i++)
            {
                double t = (double)i / totalSteps;
                result.Add(((int)(x0 + t * (x1 - x0)), (int)(y0 + t * (y1 - y0))));
            }
            return result;
        }

        // Multi-waypoint: distribute steps proportionally across segments
        var allPoints = new List<(int, int)>();
        double totalDist = 0;
        for (int i = 1; i < waypoints.Count; i++)
        {
            double dx = waypoints[i].absX - waypoints[i - 1].absX;
            double dy = waypoints[i].absY - waypoints[i - 1].absY;
            totalDist += Math.Sqrt(dx * dx + dy * dy);
        }

        double accumulated = 0;
        for (int seg = 0; seg < waypoints.Count - 1; seg++)
        {
            double dx = waypoints[seg + 1].absX - waypoints[seg].absX;
            double dy = waypoints[seg + 1].absY - waypoints[seg].absY;
            double segDist = Math.Sqrt(dx * dx + dy * dy);
            int segSteps = Math.Max(1, (int)(totalSteps * segDist / totalDist));

            int startI = seg == 0 ? 0 : 1; // avoid duplicating junction points
            for (int i = startI; i <= segSteps; i++)
            {
                double t = (double)i / segSteps;
                allPoints.Add((
                    (int)(waypoints[seg].absX + t * (waypoints[seg + 1].absX - waypoints[seg].absX)),
                    (int)(waypoints[seg].absY + t * (waypoints[seg + 1].absY - waypoints[seg].absY))));
            }
            accumulated += segDist;
        }

        return allPoints;
    }

    // --- Keyboard support ---

    private static readonly Dictionary<string, ushort> VirtualKeyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Back"] = 0x08, ["Backspace"] = 0x08,
        ["Tab"] = 0x09, ["Enter"] = 0x0D, ["Return"] = 0x0D,
        ["Shift"] = 0x10, ["Control"] = 0x11, ["Ctrl"] = 0x11,
        ["Menu"] = 0x12, ["Alt"] = 0x12,
        ["Pause"] = 0x13, ["CapsLock"] = 0x14, ["Escape"] = 0x1B, ["Esc"] = 0x1B,
        ["Space"] = 0x20,
        ["PageUp"] = 0x21, ["PageDown"] = 0x22,
        ["End"] = 0x23, ["Home"] = 0x24,
        ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28,
        ["Insert"] = 0x2D, ["Delete"] = 0x2E,
        ["Windows"] = 0x5B,
        ["F1"] = 0x70, ["F2"] = 0x71, ["F3"] = 0x72, ["F4"] = 0x73,
        ["F5"] = 0x74, ["F6"] = 0x75, ["F7"] = 0x76, ["F8"] = 0x77,
        ["F9"] = 0x78, ["F10"] = 0x79, ["F11"] = 0x7A, ["F12"] = 0x7B,
        ["A"] = 0x41, ["B"] = 0x42, ["C"] = 0x43, ["D"] = 0x44,
        ["E"] = 0x45, ["F"] = 0x46, ["G"] = 0x47, ["H"] = 0x48,
        ["I"] = 0x49, ["J"] = 0x4A, ["K"] = 0x4B, ["L"] = 0x4C,
        ["M"] = 0x4D, ["N"] = 0x4E, ["O"] = 0x4F, ["P"] = 0x50,
        ["Q"] = 0x51, ["R"] = 0x52, ["S"] = 0x53, ["T"] = 0x54,
        ["U"] = 0x55, ["V"] = 0x56, ["W"] = 0x57, ["X"] = 0x58,
        ["Y"] = 0x59, ["Z"] = 0x5A,
        ["0"] = 0x30, ["1"] = 0x31, ["2"] = 0x32, ["3"] = 0x33,
        ["4"] = 0x34, ["5"] = 0x35, ["6"] = 0x36, ["7"] = 0x37,
        ["8"] = 0x38, ["9"] = 0x39,
    };

    /// <summary>Injects a key press with optional modifiers.</summary>
    public async Task<bool> InjectKeyPress(string key, string[] modifiers, int holdDurationMs = 0)
    {
        if (!VirtualKeyMap.TryGetValue(key, out ushort vk))
        {
            Console.Error.WriteLine($"[Win32Input] Unknown key: {key}");
            return false;
        }

        var inputs = new List<INPUT>();

        // Press modifiers
        foreach (var mod in modifiers)
        {
            if (VirtualKeyMap.TryGetValue(mod, out ushort modVk))
                inputs.Add(new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = modVk } });
        }

        // Press key
        inputs.Add(new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = vk } });

        uint sent = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());

        if (holdDurationMs > 0)
            await Task.Delay(holdDurationMs);

        // Release key
        var releases = new List<INPUT>();
        releases.Add(new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYUP } });

        // Release modifiers in reverse
        for (int i = modifiers.Length - 1; i >= 0; i--)
        {
            if (VirtualKeyMap.TryGetValue(modifiers[i], out ushort modVk))
                releases.Add(new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = modVk, dwFlags = KEYEVENTF_KEYUP } });
        }

        sent += SendInput((uint)releases.Count, releases.ToArray(), Marshal.SizeOf<INPUT>());
        Console.Error.WriteLine($"[Win32Input] Key press: {key} modifiers=[{string.Join(",", modifiers)}] hold={holdDurationMs}ms sent={sent}");
        return true;
    }

    /// <summary>Types text by sending Unicode character events.</summary>
    public async Task<bool> InjectText(string text, int delayBetweenKeysMs = 30)
    {
        foreach (char ch in text)
        {
            var inputs = new INPUT[]
            {
                new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE } },
                new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } }
            };
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

            if (delayBetweenKeysMs > 0)
                await Task.Delay(delayBetweenKeysMs);
        }
        Console.Error.WriteLine($"[Win32Input] Text injected: {text.Length} chars");
        return true;
    }

    // --- Mouse move/scroll ---

    /// <summary>Moves the mouse to the specified coordinates without clicking.</summary>
    public bool InjectMouseMove(double x, double y, string coordinateSpace = "normalized")
    {
        var abs = NormalizedToAbsolute(x, y, coordinateSpace);
        if (abs == null) return false;

        var (absX, absY) = abs.Value;
        var inputs = new INPUT[]
        {
            new INPUT
            {
                type = INPUT_MOUSE,
                mi = new MOUSEINPUT { dx = absX, dy = absY, dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE }
            }
        };
        uint sent = SendInput(1, inputs, Marshal.SizeOf<INPUT>());
        return sent == 1;
    }

    /// <summary>Scrolls the mouse wheel at the specified coordinates.</summary>
    public bool InjectMouseScroll(double x, double y, int deltaY, int deltaX, string coordinateSpace = "normalized")
    {
        var abs = NormalizedToAbsolute(x, y, coordinateSpace);
        if (abs == null) return false;

        var (absX, absY) = abs.Value;

        // Move to position first
        var moveInput = new INPUT[]
        {
            new INPUT
            {
                type = INPUT_MOUSE,
                mi = new MOUSEINPUT { dx = absX, dy = absY, dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE }
            }
        };
        SendInput(1, moveInput, Marshal.SizeOf<INPUT>());

        // Vertical scroll
        if (deltaY != 0)
        {
            var vScroll = new INPUT[]
            {
                new INPUT
                {
                    type = INPUT_MOUSE,
                    mi = new MOUSEINPUT { mouseData = unchecked((uint)deltaY), dwFlags = MOUSEEVENTF_WHEEL }
                }
            };
            SendInput(1, vScroll, Marshal.SizeOf<INPUT>());
        }

        // Horizontal scroll
        if (deltaX != 0)
        {
            var hScroll = new INPUT[]
            {
                new INPUT
                {
                    type = INPUT_MOUSE,
                    mi = new MOUSEINPUT { mouseData = unchecked((uint)deltaX), dwFlags = MOUSEEVENTF_HWHEEL }
                }
            };
            SendInput(1, hScroll, Marshal.SizeOf<INPUT>());
        }

        Console.Error.WriteLine($"[Win32Input] Scroll at ({x:F3},{y:F3}), deltaY={deltaY}, deltaX={deltaX}");
        return true;
    }
}
