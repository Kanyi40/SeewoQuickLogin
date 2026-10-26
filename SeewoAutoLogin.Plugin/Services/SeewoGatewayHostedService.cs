using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SeewoAutoLogin.Plugin.Services;

/// <summary>
/// 应用启动时自动拉起 SSO 网关（若插件已启用）。
/// 启停的运行时切换由设置页 ViewModel 直接操作网关完成。
///
/// 现场健壮性：班级大屏上希沃套件（EasiAgent）开机自启，常在 ClassIsland 之前抢走 24300。
/// 网关首次启动失败后不再放弃——后台每 30 秒重试（含 AutoTakeOverPort 接管逻辑），
/// 直到拿到端口或用户关闭开关，避免"开机慢半拍就永久失效"。
/// </summary>
public class SeewoGatewayHostedService : IHostedService
{
    private readonly SeewoSsoGateway _gateway;
    private readonly SettingsStore _store;
    private readonly HostsFileService _hosts;
    private readonly ILogger<SeewoGatewayHostedService>? _logger;
    private CancellationTokenSource? _retryCts;
    private DateTime _lastUpgradeAttemptUtc = DateTime.MinValue;

    /// <summary>网关启动失败后的自动重试间隔。</summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    /// <summary>运行在备用端口时，尝试升级回首选端口的间隔。</summary>
    private static readonly TimeSpan UpgradeInterval = TimeSpan.FromMinutes(10);

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

        // 后台守护：网关被外部抢端/启动失败时周期性重试
        _retryCts = new CancellationTokenSource();
        _ = Task.Run(() => RetryLoopAsync(_retryCts.Token));

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _retryCts?.Cancel();
        _gateway.Stop();
        return Task.CompletedTask;
    }

    private async Task RetryLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(RetryInterval, token).ConfigureAwait(false);
                _store.LoadSettings();
                if (!_store.Settings.Enabled)
                    continue;

                if (!_gateway.IsRunning)
                {
                    // 网关未运行：自动重试拉起（Start 内部含接管与详细诊断日志）
                    if (_gateway.TryStart(_store.Settings.GatewayPort))
                    {
                        _logger?.LogInformation("SSO 网关重试启动成功：端口 {Port}", _gateway.Port);
                    }
                    else
                    {
                        _logger?.LogWarning("SSO 网关自动重试仍失败：{Error}", _gateway.LastStartError);
                    }
                    continue;
                }

                // 运行在备用端口：旧版白板不会访问备用端口，等于失效。
                // 定期尝试升级回首选端口（Stop 后重 Start，Start 内部会接管希沃服务）。
                if (_gateway.Port != _store.Settings.GatewayPort
                    && DateTime.UtcNow - _lastUpgradeAttemptUtc >= UpgradeInterval)
                {
                    _lastUpgradeAttemptUtc = DateTime.UtcNow;
                    _logger?.LogInformation(
                        "网关当前在备用端口 {Port}，尝试升级回首选端口 {Preferred}",
                        _gateway.Port, _store.Settings.GatewayPort);
                    _gateway.Stop();
                    TryStartGateway();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "SSO 网关重试循环异常");
            }
        }
    }

    internal void TryStartGateway()
    {
        try
        {
            _gateway.Start(_store.Settings.GatewayPort);
        }
        catch (Exception ex)
        {
            // 24300 被占用（常见为希沃自带 EasiAgent）等情况：只告警，交由重试循环接管
            _logger?.LogWarning(ex,
                "SSO 网关启动失败（端口 {Port}，可能被 EasiAgent 或其它程序占用），将于 {Interval} 后自动重试",
                _store.Settings.GatewayPort, RetryInterval);
        }
    }
}
