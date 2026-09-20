using System.Windows.Input;
using System.Windows.Interop;

namespace DesktopWorkflow.App;

public sealed class GlobalHotkeyService : IDisposable
{
    private readonly Dictionary<int, Action> _actions = [];
    private List<(string Hotkey, Action Action)> _registrations = [];
    private HwndSource? _source;
    private int _nextId = 100;

    public void Attach(System.Windows.Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(handle) ?? throw new InvalidOperationException("无法取得窗口消息源。");
        _source.AddHook(WndProc);
    }

    public void Register(IEnumerable<(string Hotkey, Action Action)> registrations)
    {
        if (_source is null)
        {
            throw new InvalidOperationException("快捷键服务尚未连接窗口。");
        }
        var next = registrations.Where(item => !string.IsNullOrWhiteSpace(item.Hotkey)).ToList();
        var previous = _registrations.ToList();
        ClearActions();
        try
        {
            RegisterCore(next);
            _registrations = next;
        }
        catch (Exception registrationException)
        {
            ClearActions();
            try
            {
                RegisterCore(previous);
                _registrations = previous;
            }
            catch (Exception restoreException)
            {
                _registrations = [];
                throw new InvalidOperationException(
                    $"快捷键更新失败，旧快捷键恢复也失败：{restoreException.Message}",
                    new AggregateException(registrationException, restoreException));
            }
            throw;
        }
    }

    private void RegisterCore(IEnumerable<(string Hotkey, Action Action)> registrations)
    {
        foreach (var registration in registrations)
        {
            var (modifiers, virtualKey) = Parse(registration.Hotkey);
            var id = _nextId++;
            if (!NativeMethods.RegisterHotKey(_source!.Handle, id, modifiers, virtualKey))
            {
                throw new InvalidOperationException($"快捷键无效或被占用：{registration.Hotkey}");
            }
            _actions[id] = registration.Action;
        }
    }

    public static string FormatForDisplay(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }
        var parts = new List<string>();
        if (value.Contains('#')) parts.Add("Win");
        if (value.Contains('^')) parts.Add("Ctrl");
        if (value.Contains('!')) parts.Add("Alt");
        if (value.Contains('+')) parts.Add("Shift");
        var key = new string(value.Where(character => character is not '#' and not '^' and not '!' and not '+').ToArray());
        if (key.Length > 0) parts.Add(key.ToUpperInvariant());
        return string.Join(" + ", parts);
    }

    public static bool HasPrimaryModifier(string value) =>
        value.Contains('#') || value.Contains('^') || value.Contains('!');

    public static string Compose(Key key, ModifierKeys modifiers, bool includeWin)
    {
        if (!includeWin && !modifiers.HasFlag(ModifierKeys.Control) && !modifiers.HasFlag(ModifierKeys.Alt))
        {
            return string.Empty;
        }
        var prefix = string.Empty;
        if (includeWin) prefix += "#";
        if (modifiers.HasFlag(ModifierKeys.Control)) prefix += "^";
        if (modifiers.HasFlag(ModifierKeys.Alt)) prefix += "!";
        if (modifiers.HasFlag(ModifierKeys.Shift)) prefix += "+";
        var name = KeyToToken(key);
        return name.Length == 0 ? string.Empty : prefix + name;
    }

    private static (uint Modifiers, uint VirtualKey) Parse(string value)
    {
        uint modifiers = 0;
        if (value.Contains('#')) modifiers |= NativeMethods.ModWin;
        if (value.Contains('^')) modifiers |= NativeMethods.ModControl;
        if (value.Contains('!')) modifiers |= NativeMethods.ModAlt;
        if (value.Contains('+')) modifiers |= NativeMethods.ModShift;
        var token = new string(value.Where(character => character is not '#' and not '^' and not '!' and not '+').ToArray());
        if (token.Length == 0)
        {
            throw new InvalidDataException($"快捷键缺少按键：{value}");
        }
        if (!HasPrimaryModifier(value))
        {
            throw new InvalidDataException($"全局快捷键必须包含 Ctrl、Alt 或 Win：{value}");
        }
        var key = TokenToKey(token);
        return (modifiers, (uint)KeyInterop.VirtualKeyFromKey(key));
    }

    private static string KeyToToken(Key key)
    {
        if (key is >= Key.A and <= Key.Z) return key.ToString().ToLowerInvariant();
        if (key is >= Key.D0 and <= Key.D9) return ((int)(key - Key.D0)).ToString();
        if (key is >= Key.F1 and <= Key.F24) return key.ToString();
        return key switch
        {
            Key.Space => "Space",
            Key.Tab => "Tab",
            Key.Enter => "Enter",
            Key.Escape => "Esc",
            Key.Left => "Left",
            Key.Right => "Right",
            Key.Up => "Up",
            Key.Down => "Down",
            _ => string.Empty
        };
    }

    private static Key TokenToKey(string token)
    {
        if (token.Length == 1 && char.IsLetter(token[0]))
        {
            return Key.A + (char.ToUpperInvariant(token[0]) - 'A');
        }
        if (token.Length == 1 && char.IsDigit(token[0]))
        {
            return Key.D0 + (token[0] - '0');
        }
        if (Enum.TryParse<Key>(token, true, out var key))
        {
            return key;
        }
        throw new InvalidDataException($"不支持的快捷键按键：{token}");
    }

    private nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == NativeMethods.WmHotkey && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            action();
            handled = true;
        }
        return 0;
    }

    private void ClearActions()
    {
        if (_source is not null)
        {
            foreach (var id in _actions.Keys)
            {
                _ = NativeMethods.UnregisterHotKey(_source.Handle, id);
            }
        }
        _actions.Clear();
    }

    public void Dispose()
    {
        ClearActions();
        _registrations = [];
        _source?.RemoveHook(WndProc);
        _source = null;
    }
}
