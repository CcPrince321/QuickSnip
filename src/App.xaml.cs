using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using QuickSnip.Native;
using QuickSnip.Overlay;
using QuickSnip.Services;
using QuickSnip.Views;

namespace QuickSnip;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\QuickSnip.SingleInstance";
    private const string ActivateEventName = @"Local\QuickSnip.Activate";

    private Mutex? _singleInstance;
    private EventWaitHandle? _activateSignal;
    private CancellationTokenSource? _activateCts;
    private TrayService? _tray;
    private HotKeyManager? _hotKeys;
    private AppSettings _settings = new();
    private OverlaySession? _session;
    private DateTime _lastCaptureUtc = DateTime.MinValue;

    public App()
    {
        // 托盘程序没有控制台，崩溃必须自己记下来，否则用户只看到「没反应」。
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Exception("AppDomain.UnhandledException", args.ExceptionObject as Exception
                ?? new Exception(args.ExceptionObject?.ToString() ?? "unknown"));
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Exception("TaskScheduler.UnobservedTaskException", args.Exception);
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Exception("DispatcherUnhandledException", args.Exception);
            MessageBox.Show(args.Exception.ToString(), "QuickSnip 发生未处理异常", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        // DPI 感知必须在创建任何窗口、获取任何 DC 之前落地。
        // manifest 里已经声明了 PerMonitorV2，这里只是兜底（manifest 生效时会返回失败，无害）。
        ScreenLayout.EnsurePerMonitorV2();

        base.OnStartup(e);

        _singleInstance = new Mutex(true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            // 已经有一个实例在跑（热键也在它手上）。
            // 不能静默退出——用户会以为「一打开就崩了」，所以先喊醒老实例弹个气泡。
            Log.Write("second instance: signalling first instance, then exit");
            try
            {
                if (EventWaitHandle.TryOpenExisting(ActivateEventName, out EventWaitHandle? existing))
                {
                    using (existing) existing.Set();
                }
            }
            catch (Exception ex)
            {
                Log.Exception("second instance: signal failed", ex);
            }

            Shutdown(0);
            return;
        }

        _settings = SettingsStore.Load();
        ApplyInstallerSaveDirectoryHint();
        Log.Write($"--- app start pid={Environment.ProcessId} settings={SettingsStore.FilePath} hotKey={_settings.HotKey} saveDir={SaveService.ResolveDirectory(_settings)}");

        _tray = new TrayService();
        _tray.CaptureRequested += () => Dispatcher.BeginInvoke(new Action(StartCapture));
        _tray.SettingsRequested += () => Dispatcher.BeginInvoke(new Action(ShowHotKeySettings));
        _tray.ScreenInfoRequested += () => Dispatcher.BeginInvoke(new Action(ShowScreenInfo));
        _tray.OpenSaveFolderRequested += () => Dispatcher.BeginInvoke(new Action(OpenSaveFolder));
        _tray.ExitRequested += () => Dispatcher.BeginInvoke(new Action(ExitApp));

        _hotKeys = new HotKeyManager();
        _hotKeys.Pressed += StartCapture;

        HotKeySpec spec = _settings.GetHotKeySpec();
        if (!_hotKeys.TryRegister(spec, out string? error))
        {
            Log.Write($"hotkey register FAILED: {spec.ToDisplayString()} :: {error}");
            _tray.ShowBalloon("热键注册失败", error ?? "未知错误", isError: true);

            // 回落到默认组合再试一次，尽量让用户还能用。
            if (!spec.SameAs(HotKeySpec.Default) && _hotKeys.TryRegister(HotKeySpec.Default, out _))
            {
                _settings.HotKey = HotKeySpec.Default.ToDisplayString();
                SettingsStore.Save(_settings);
                _tray.ShowBalloon("已回落到默认热键", HotKeySpec.Default.ToDisplayString());
            }
        }
        else
        {
            Log.Write($"hotkey registered OK: {spec.ToDisplayString()}");
        }

        _tray.SetTooltip(_hotKeys.Current?.ToDisplayString() ?? spec.ToDisplayString());

        StartActivationListener();

        // 托盘程序起来之后屏幕上「什么都不会发生」，用户没法区分「在跑」和「双击后崩了」。
        // 所以启动时给一次可见反馈，同时把它当作「启动完成」的日志锚点。
        if (_settings.ShowBalloonOnStartup)
        {
            _tray.ShowBalloon(
                "QuickSnip 已就绪",
                $"按 {_hotKeys.Current?.ToDisplayString() ?? spec.ToDisplayString()} 截图；双击托盘图标同样可以。");
        }

        Log.Write($"--- startup complete pid={Environment.ProcessId}");

        // 冒烟自检：QuickSnip.exe --selftest
        // 走一遍完整的抓帧 + 建遮罩 + 关闭流程，1.5 秒后自动取消并退出，结果看日志。
        if (Array.IndexOf(e.Args, "--selftest") >= 0)
        {
            Log.Write("selftest: begin");
            Dispatcher.BeginInvoke(new Action(() =>
            {
                StartCapture();
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    Log.Write("selftest: auto-cancel");
                    _session?.Cancel();
                    Log.Write("selftest: end");
                    Shutdown(0);
                };
                timer.Start();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// 第二个实例启动时会给我们这个具名事件置位。
    /// 在后台线程等它，收到就弹气泡——让「重复双击 exe」有个明确反馈，而不是静默退出。
    /// </summary>
    private void StartActivationListener()
    {
        try
        {
            _activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        }
        catch (Exception ex)
        {
            Log.Exception("activation listener create failed", ex);
            return;
        }

        EventWaitHandle signal = _activateSignal;
        _activateCts = new CancellationTokenSource();
        CancellationToken token = _activateCts.Token;

        var thread = new Thread(() =>
        {
            while (!token.IsCancellationRequested)
            {
                bool signaled;
                try
                {
                    signaled = signal.WaitOne(500);
                }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested) Log.Exception("activation wait failed", ex);
                    return;
                }

                if (token.IsCancellationRequested) return;
                if (!signaled) continue;

                try
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        Log.Write("activation signal received (another instance was launched)");
                        _tray?.ShowBalloon(
                            "QuickSnip 已经在运行",
                            "看屏幕右下角托盘区的红色图标。按热键截图，右键图标可以退出。");
                    }));
                }
                catch
                {
                    return; // 正在退出，Dispatcher 已关闭。
                }
            }
        })
        {
            IsBackground = true,
            Name = "QuickSnip.ActivateListener",
        };

        thread.Start();
        Log.Write("activation listener started");
    }

    private void StopActivationListener()
    {
        try { _activateCts?.Cancel(); } catch { }
        _activateCts?.Dispose();
        _activateCts = null;

        _activateSignal?.Dispose();
        _activateSignal = null;
    }

    /// <summary>
    /// 安装程序里用户选的「截图保存位置」通过一个 marker 文件传过来。
    /// 启动时采纳一次（写进 settings.json）然后删掉 marker——之后用户改配置就不会被覆盖。
    /// </summary>
    private void ApplyInstallerSaveDirectoryHint()
    {
        try
        {
            string marker = Path.Combine(SettingsStore.Directory, "save-directory.txt");
            if (!File.Exists(marker)) return;

            string dir = File.ReadAllText(marker).Trim().Trim('"');
            File.Delete(marker);

            if (string.IsNullOrWhiteSpace(dir)) return;
            if (dir == _settings.SaveDirectory) return;

            _settings.SaveDirectory = dir;
            SettingsStore.Save(_settings);
            Log.Write($"save directory adopted from installer: {dir}");
        }
        catch (Exception ex)
        {
            Log.Exception("apply installer save-directory hint failed", ex);
        }
    }

    private void StartCapture()
    {
        Log.Write($"StartCapture: session={( _session == null ? "null" : "active")}");

        if (_session != null) return;

        // 热键连发保护（MOD_NOREPEAT 之外再兜一层）。
        if ((DateTime.UtcNow - _lastCaptureUtc).TotalMilliseconds < 200) return;
        _lastCaptureUtc = DateTime.UtcNow;

        try
        {
            _session = OverlaySession.Begin(_settings, OnCaptureFinished);
        }
        catch (Exception ex)
        {
            _session = null;
            Log.Exception("StartCapture", ex);
            _tray?.ShowBalloon("截图失败", ex.Message, isError: true);
            MessageBox.Show(ex.ToString(), "QuickSnip 截图失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnCaptureFinished(BitmapSource? image)
    {
        _session = null;

        if (image == null) return; // 用户取消（Esc / 右键 / 单击）

        bool copied = ClipboardService.TrySetImage(image, out string? clipError);

        string? savedPath = null;
        string? saveError = null;
        if (_settings.AutoSavePng)
        {
            try
            {
                savedPath = SaveService.SavePng(image, SaveService.ResolveDirectory(_settings));
                Log.Write($"saved png: {savedPath} ({image.PixelWidth}x{image.PixelHeight})");
            }
            catch (Exception ex)
            {
                saveError = ex.Message;
                Log.Exception("auto save png failed", ex);
            }
        }

        if (!_settings.ShowBalloonAfterCapture) return;

        string size = $"{image.PixelWidth} × {image.PixelHeight} 像素";

        if (savedPath != null && copied)
        {
            _tray?.ShowBalloon("已复制并保存", $"{size}\n{savedPath}");
        }
        else if (savedPath != null && !copied)
        {
            _tray?.ShowBalloon("已保存到文件，但剪贴板复制失败",
                $"{savedPath}\n剪贴板原因：{clipError ?? "被其他程序占用，请重试。"}",
                isError: true);
        }
        else if (savedPath == null && copied && _settings.AutoSavePng)
        {
            _tray?.ShowBalloon("已复制到剪贴板，但保存文件失败", $"{size}\n原因：{saveError}", isError: true);
        }
        else if (savedPath == null && copied)
        {
            _tray?.ShowBalloon("已复制到剪贴板", size);
        }
        else
        {
            _tray?.ShowBalloon("截图失败", $"剪贴板：{clipError ?? "被占用"}\n保存：{saveError ?? "未启用"}",
                isError: true);
        }
    }

    /// <summary>托盘菜单「打开截图文件夹」。</summary>
    private void OpenSaveFolder()
    {
        try
        {
            string dir = SaveService.ResolveDirectory(_settings);
            Log.Write($"open save folder: {dir}");
            System.Diagnostics.Process.Start("explorer.exe", dir);
        }
        catch (Exception ex)
        {
            Log.Exception("open save folder failed", ex);
            _tray?.ShowBalloon("打开文件夹失败", ex.Message, isError: true);
        }
    }

    /// <summary>
    /// 排查多屏混合 DPI 问题时先看这个：确认 Windows 认为的每块屏物理矩形和 DPI。
    /// </summary>
    private void ShowScreenInfo()
    {
        PixelRect virtualBounds = ScreenLayout.GetVirtualBounds();
        NativeMethods.POINT cursor = NativeMethods.GetCursorPosition();

        var sb = new StringBuilder();
        sb.AppendLine("虚拟桌面（物理像素 / VSP）：");
        sb.AppendLine($"  ({virtualBounds.X}, {virtualBounds.Y})  {virtualBounds.Width} × {virtualBounds.Height}");
        sb.AppendLine($"  光标 VSP 坐标：({cursor.X}, {cursor.Y})");
        sb.AppendLine();

        int index = 1;
        foreach (MonitorInfo monitor in ScreenLayout.Enumerate())
        {
            sb.AppendLine($"显示器 {index++}{(monitor.IsPrimary ? "（主屏）" : string.Empty)}");
            sb.AppendLine($"  矩形：({monitor.Bounds.X}, {monitor.Bounds.Y})  {monitor.Bounds.Width} × {monitor.Bounds.Height}");
            sb.AppendLine($"  DPI：{monitor.Dpi}  缩放：{monitor.Scale * 100:0}%");
            sb.AppendLine();
        }

        MessageBox.Show(sb.ToString().TrimEnd(), "QuickSnip 屏幕信息", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ShowHotKeySettings()
    {
        HotKeySpec current = _hotKeys?.Current ?? _settings.GetHotKeySpec();

        var window = new HotKeySettingsWindow(current, spec =>
        {
            if (_hotKeys == null) return "热键模块未初始化。";

            HotKeySpec previous = _hotKeys.Current ?? HotKeySpec.Default;

            if (_hotKeys.TryRegister(spec, out string? error))
            {
                _settings.HotKey = spec.ToDisplayString();
                SettingsStore.Save(_settings);
                _tray?.SetTooltip(spec.ToDisplayString());
                return null;
            }

            // 注册失败：把旧热键还回去，别让用户既没新的也没旧的。
            _hotKeys.TryRegister(previous, out _);
            _tray?.SetTooltip(previous.ToDisplayString());
            return error;
        })
        {
            Topmost = true,
        };

        window.ShowDialog();
    }

    private void ExitApp()
    {
        _session?.Cancel();
        _session = null;

        _hotKeys?.Dispose();   // 这里做 UnregisterHotKey
        _tray?.Dispose();
        StopActivationListener();

        try { _singleInstance?.ReleaseMutex(); } catch { }
        _singleInstance?.Dispose();
        _singleInstance = null;

        Shutdown(0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 兜底：无论从哪条路径退出，都不能把热键留在系统里。
        _hotKeys?.Dispose();
        _tray?.Dispose();
        StopActivationListener();
        base.OnExit(e);
    }
}
