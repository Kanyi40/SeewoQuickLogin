using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SeewoAutoLogin.Plugin.Services;

/// <summary>
/// 应用启动时自动拉起 SSO 网关（若插件已启用）。
/// 启停的运行时切换由设置页 ViewModel 直接操作网关完成。
/// </summary>
public class SeewoGatewayHostedService : IHostedService
{
    private readonly SeewoSsoGateway _gateway;
    private readonly SettingsStore _store;
    private readonly HostsFileService _hosts;
    private readonly ILogger<SeewoGatewayHostedService>? _logger;

    public SeewoGatewayHostedService(
        SeewoSsoGateway gateway, SettingsStore store, HostsFileService hosts,
        ILogger<SeewoGatewayHostedService>? logger = null)
    {
        _gateway = gateway;
        _store = store;
        _hosts = hosts;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _store.LoadSettings();
        if (_store.Settings.Enabled)
        {
            TryStartGateway();
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _gateway.Stop();
        return Task.CompletedTask;
    }

    internal void TryStartGateway()
    {
        try
        {
            _gateway.Start(_store.Settings.GatewayPort);
        }
        catch (Exception ex)
        {
            // 24300 被占用（常见为希沃自带 EasiAgent）等情况：只告警，不自动杀进程
            _logger?.LogWarning(ex,
                "SSO 网关启动失败（端口 {Port}，可能被 EasiAgent 或其它程序占用）",
                _store.Settings.GatewayPort);
        }
    }
}
