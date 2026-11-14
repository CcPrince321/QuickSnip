using System;
using System.Windows;
using System.Windows.Input;
using QuickSnip.Services;

namespace QuickSnip.Views;

/// <summary>
/// 热键录入窗口。回调 tryApply 负责「注册 + 持久化」，
/// 返回 null 表示成功，返回字符串表示失败原因（就地显示，不关窗）。
/// </summary>
public partial class HotKeySettingsWindow : Window
{
    private readonly Func<HotKeySpec, string?> _tryApply;
    private HotKeySpec _spec;

    public HotKeySettingsWindow(HotKeySpec current, Func<HotKeySpec, string?> tryApply)
    {
        InitializeComponent();

        _tryApply = tryApply;
        _spec = current;
        UpdateDisplay();

        Loaded += (_, _) => KeyBox.Focus();
    }

    private void UpdateDisplay()
    {
        KeyText.Text = _spec.ToDisplayString();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // 一切按键都由这里吃掉，不让 Enter / 空格去触发按钮。
        e.Handled = true;

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            Close();
            return;
        }

        if (key is Key.Back or Key.Delete)
        {
            _spec = HotKeySpec.Default;
            ErrorText.Visibility = Visibility.Collapsed;
            UpdateDisplay();
            return;
        }

        // 只按下了修饰键本身，等用户继续按主键。
        if (key is Key.LeftCtrl or Key.RightCtrl
                 or Key.LeftAlt or Key.RightAlt
                 or Key.LeftShift or Key.RightShift
                 or Key.LWin or Key.RWin
                 or Key.None)
        {
            return;
        }

        int virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey <= 0)
        {
            return;
        }

        ModifierKeys modifiers = Keyboard.Modifiers;
        bool isFunctionKey = key >= Key.F1 && key <= Key.F24;

        if (modifiers == ModifierKeys.None && !isFunctionKey)
        {
            ErrorText.Text = "请至少带一个修饰键（Ctrl / Alt / Shift / Win），F1–F24 除外。";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        _spec = new HotKeySpec(HotKeySpec.ToNativeModifiers(modifiers), (uint)virtualKey);
        ErrorText.Visibility = Visibility.Collapsed;
        UpdateDisplay();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        string? error = _tryApply(_spec);
        if (error == null)
        {
            Close();
            return;
        }

        ErrorText.Text = error;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();
}
