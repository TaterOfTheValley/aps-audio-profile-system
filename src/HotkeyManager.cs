using System.Runtime.InteropServices;

namespace APS;

public class HotkeyManager : IDisposable
{
    private class HotkeyWindow : NativeWindow, IDisposable
    {
        private const int WM_HOTKEY = 0x0312;
        private readonly Action<int> _callback;

        public HotkeyWindow(Action<int> callback)
        {
            _callback = callback;
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                _callback?.Invoke(id);
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            DestroyHandle();
        }
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    private readonly HotkeyWindow _window;
    private readonly Dictionary<int, Action> _hotkeyActions = new();
    private int _currentId = 1;

    public HotkeyManager()
    {
        _window = new HotkeyWindow(OnHotkeyPressed);
    }

    private void OnHotkeyPressed(int id)
    {
        if (_hotkeyActions.TryGetValue(id, out var action))
        {
            action?.Invoke();
        }
    }

    public bool Register(string hotkeyString, Action action)
    {
        if (string.IsNullOrWhiteSpace(hotkeyString)) return false;

        uint modifiers = MOD_NOREPEAT;
        Keys key = Keys.None;

        var tokens = hotkeyString.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens)
        {
            if (token.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || token.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= MOD_CONTROL;
            }
            else if (token.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= MOD_ALT;
            }
            else if (token.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= MOD_SHIFT;
            }
            else if (token.Equals("Win", StringComparison.OrdinalIgnoreCase) || token.Equals("Windows", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= MOD_WIN;
            }
            else
            {
                if (Enum.TryParse<Keys>(token, true, out var parsedKey))
                {
                    key = parsedKey;
                }
                else if (token.Length == 1 && char.IsDigit(token[0]))
                {
                    key = (Keys)((int)Keys.D0 + (token[0] - '0'));
                }
                else if (token.Length == 1 && char.IsLetter(token[0]))
                {
                    key = (Keys)((int)Keys.A + (char.ToUpperInvariant(token[0]) - 'A'));
                }
            }
        }

        if (key == Keys.None) return false;

        int id = _currentId++;
        bool success = RegisterHotKey(_window.Handle, id, modifiers, (uint)key);
        if (success)
        {
            _hotkeyActions[id] = action;
        }

        return success;
    }

    public void UnregisterAll()
    {
        foreach (var id in _hotkeyActions.Keys)
        {
            UnregisterHotKey(_window.Handle, id);
        }
        _hotkeyActions.Clear();
    }

    public void Dispose()
    {
        UnregisterAll();
        _window.Dispose();
    }
}
