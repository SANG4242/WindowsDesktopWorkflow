using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DesktopWorkflow.App;

internal static class NativeMethods
{
    internal const uint SwShowNormal = 1;
    internal const int SwRestore = 9;
    internal static readonly nint HwndTopmost = new(-1);
    internal static readonly nint HwndNoTopmost = new(-2);
    internal const uint SwpNoSize = 0x0001;
    internal const uint SwpNoMove = 0x0002;
    internal const uint SwpNoZOrder = 0x0004;
    internal const uint SwpNoActivate = 0x0010;
    internal const uint SwpShowWindow = 0x0040;
    internal const uint ModAlt = 0x0001;
    internal const uint ModControl = 0x0002;
    internal const uint ModShift = 0x0004;
    internal const uint ModWin = 0x0008;
    internal const int WmHotkey = 0x0312;
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ProcessCommandLineInformation = 60;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    [DllImport("user32.dll")]
    internal static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    internal delegate bool EnumWindowsProc(nint window, nint parameter);

    [DllImport("user32.dll")]
    internal static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    internal static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint window, StringBuilder text, int capacity);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(nint window);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern nint SetFocus(nint window);

    [DllImport("user32.dll")]
    internal static extern nint SetActiveWindow(nint window);

    [DllImport("user32.dll")]
    internal static extern void SwitchToThisWindow(nint window, bool altTab);

    [DllImport("user32.dll")]
    internal static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        nint processHandle,
        int processInformationClass,
        nint processInformation,
        int processInformationLength,
        out int returnLength);

    [DllImport("user32.dll")]
    internal static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    internal static extern bool PrintWindow(nint hwnd, nint hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    internal static extern nint GetDC(nint hwnd);

    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(nint hwnd, nint hdc);

    [DllImport("gdi32.dll")]
    internal static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll")]
    internal static extern nint CreateCompatibleBitmap(nint hdc, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    internal static extern nint SelectObject(nint hdc, nint hgdiobj);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteDC(nint hdc);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteObject(nint hObject);

    [DllImport("user32.dll")]
    internal static extern bool BringWindowToTop(nint window);

    [DllImport("user32.dll")]
    internal static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll")]
    internal static extern bool GetWindowRect(nint window, out Rect rect);

    [DllImport("user32.dll")]
    internal static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(nint window, ref WindowPlacement placement);

    [DllImport("user32.dll")]
    internal static extern bool SetWindowPlacement(nint window, ref WindowPlacement placement);

    [DllImport("user32.dll")]
    internal static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    internal static extern bool UnregisterHotKey(nint window, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);

    internal static bool TryGetWindowPlacement(nint window, out WindowPlacement placement)
    {
        placement = new WindowPlacement { Length = (uint)Marshal.SizeOf<WindowPlacement>() };
        return GetWindowPlacement(window, ref placement);
    }

    internal static string GetWindowTitle(nint window)
    {
        var length = GetWindowTextLength(window);
        if (length <= 0)
        {
            return string.Empty;
        }
        var builder = new StringBuilder(length + 1);
        _ = GetWindowText(window, builder, builder.Capacity);
        return builder.ToString();
    }

    internal static uint GetProcessId(nint window)
    {
        _ = GetWindowThreadProcessId(window, out var processId);
        return processId;
    }

    internal static string GetProcessName(nint window)
    {
        var processId = GetProcessId(window);
        try
        {
            return Process.GetProcessById((int)processId).ProcessName + ".exe";
        }
        catch
        {
            return string.Empty;
        }
    }

    internal static string GetProcessCommandLine(nint window)
    {
        var processId = GetProcessId(window);
        var processHandle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (processHandle == 0)
        {
            return string.Empty;
        }
        try
        {
            var status = NtQueryInformationProcess(
                processHandle,
                ProcessCommandLineInformation,
                0,
                0,
                out var requiredLength);
            if (status != StatusInfoLengthMismatch || requiredLength <= Marshal.SizeOf<UnicodeString>())
            {
                return string.Empty;
            }

            var buffer = Marshal.AllocHGlobal(requiredLength);
            try
            {
                status = NtQueryInformationProcess(
                    processHandle,
                    ProcessCommandLineInformation,
                    buffer,
                    requiredLength,
                    out _);
                if (status != 0)
                {
                    return string.Empty;
                }
                var commandLine = Marshal.PtrToStructure<UnicodeString>(buffer);
                return commandLine.Buffer == 0 || commandLine.Length == 0
                    ? string.Empty
                    : Marshal.PtrToStringUni(commandLine.Buffer, commandLine.Length / sizeof(char)) ?? string.Empty;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            _ = CloseHandle(processHandle);
        }
    }

    internal static void SendChord(byte modifier, byte key) => SendKeyboardInputs(
        KeyboardInput(modifier, false),
        KeyboardInput(key, false),
        KeyboardInput(key, true),
        KeyboardInput(modifier, true));

    internal static void SendKey(byte key) => SendKeyboardInputs(
        KeyboardInput(key, false),
        KeyboardInput(key, true));

    private static Input KeyboardInput(byte key, bool keyUp) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInputData
            {
                VirtualKey = key,
                Flags = keyUp ? KeyEventKeyUp : 0
            }
        }
    };

    private static void SendKeyboardInputs(params Input[] inputs)
    {
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent != inputs.Length)
        {
            throw new InvalidOperationException($"系统只接受了 {sent}/{inputs.Length} 个键盘输入。");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public nint Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowPlacement
    {
        public uint Length;
        public uint Flags;
        public uint ShowCommand;
        public Point MinPosition;
        public Point MaxPosition;
        public Rect NormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInputData Mouse;

        [FieldOffset(0)]
        public KeyboardInputData Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInputData
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInputData
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }
}
