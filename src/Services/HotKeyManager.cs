using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using QuickSnip.Native;

namespace QuickSnip.Services;

/// <summary>
/// 全局热键。用一个 message-only 窗口收 WM_HOTKEY：
/// RegisterHotKey 把消息投递到注册线程的消息队列，message-only 窗口同样能收到。
/// 退出时必须 UnregisterHotKey，否则快捷键会被这个（已死的）进程占着。
/// </summary>
public sealed class HotKeyManager : IDisposable
{
    private const int HotKeyId = 0x51A7;

    private HwndSource? _source;
    private bool _registered;

    public event Action? Pressed;

    public HotKeySpec? Current { get; private set; }

    /// <summary>
    /// 注册热键。会先注销旧热键；返回 false 时 error 里是给用户看的原因。
    /// </summary>
    public bool TryRegister(HotKeySpec spec, out string? error)
    {
        error = null;
        EnsureSink();

        Unregister();

        // MOD_NOREPEAT：按住不放不要连发，否则会瞬间开出几十个遮罩。
        if (!NativeMethods.RegisterHotKey(_source!.Handle, HotKeyId, spec.Modifiers | NativeMethods.MOD_NOREPEAT, spec.VirtualKey))
        {
            int code = Marshal.GetLastWin32Error();
            error = code == 1409 // ERROR_HOTKEY_ALREADY_REGISTERED
                ? $"热键 {spec.ToDisplayString()} 已被其他程序占用，请换一个组合。"
                : $"注册热键 {spec.ToDisplayString()} 失败：{new Win32Exception(code).Message}";
            Current = null;
            return false;
        }

        _registered = true;
        Current = spec;
        Log.Write($"RegisterHotKey OK hwnd=0x{_source.Handle.ToInt64():X} {spec.ToDisplayString()} mods=0x{spec.Modifiers:X} vk=0x{spec.VirtualKey:X}");
        return true;
    }

    private void EnsureSink()
    {
        if (_source != null) return;

        // 0×0 的隐藏 WS_POPUP 窗口，永远不 Show。
        // RegisterHotKey 只要求一个属于本线程的窗口句柄来接收 WM_HOTKEY，
        // 用普通隐藏窗口比 HWND_MESSAGE 的兼容性更稳。
        var parameters = new HwndSourceParameters("QuickSnip.HotKeySink")
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotKeyId)
        {
            handled = true;
            Log.Write("WM_HOTKEY received");
            Pressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    private void Unregister()
    {
        if (!_registered || _source == null) return;
        NativeMethods.UnregisterHotKey(_source.Handle, HotKeyId);
        _registered = false;
    }

    public void Dispose()
    {
        Unregister();
        if (_source != null)
        {
            _source.RemoveHook(WndProc);
            _source.Dispose();
            _source = null;
        }
        Current = null;
    }
}
