using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using SeewoAutoLogin.Plugin.Services;
using SeewoAutoLogin.Plugin.ViewModels;

namespace SeewoAutoLogin.Plugin.Views.SettingsPages;

/// <summary>
/// 希沃快捷登录 插件设置页面。
/// </summary>
[SettingsPageInfo("kanyi.easiquick.settings", "希沃快捷登录", "\uEDB3", "\uEDB2")]
public partial class SeewoSettingsPage : SettingsPageBase
{
    private readonly SeewoSettingsViewModel _viewModel;
    private readonly DispatcherTimer _refreshTimer;

    public SeewoSettingsPage(SeewoSettingsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        // 剪贴板访问需要 TopLevel，经事件转交页面处理
        viewModel.CopyToClipboardRequested += OnCopyToClipboardRequested;

        // 定时刷新运行状态（网关状态 / hosts / 希沃最近请求时间）
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _refreshTimer.Tick += (_, _) => viewModel.RefreshStatus();
        _refreshTimer.Start();

        // 页面离开可视树时释放：停表 + 退订（否则每次进出设置页都会泄漏一个定时器与页面实例）
        DetachedFromVisualTree += (_, _) =>
        {
            _refreshTimer.Stop();
            _viewModel.CopyToClipboardRequested -= OnCopyToClipboardRequested;
        };
        AttachedToVisualTree += (_, _) =>
        {
            _viewModel.CopyToClipboardRequested -= OnCopyToClipboardRequested;
            _viewModel.CopyToClipboardRequested += OnCopyToClipboardRequested;
            if (!_refreshTimer.IsEnabled) _refreshTimer.Start();
        };
    }

    private void OnCopyToClipboardRequested(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null)
        {
            _ = clipboard.SetTextAsync(text);
            _viewModel.StatusMessage = $"已复制：{text}";
        }
    }

    private void OnCopyHostsLine(object? sender, RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard != null)
        {
            _ = clipboard.SetTextAsync(HostsFileService.MappingLine);
            _viewModel.StatusMessage = $"已复制映射行：{HostsFileService.MappingLine}";
        }
    }
}
