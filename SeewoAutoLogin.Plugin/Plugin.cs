using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Extensions.Registry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.IO;
using SeewoAutoLogin.Plugin.Services;
using SeewoAutoLogin.Plugin.ViewModels;
using SeewoAutoLogin.Plugin.Views.SettingsPages;

namespace SeewoAutoLogin.Plugin;

[PluginEntrance]
public class Plugin : PluginBase
{
    /// <summary>单个日志文件上限，超过后滚动为 .1（长时间运行的班级大屏不会把磁盘写满）。</summary>
    private const long MaxLogBytes = 2 * 1024 * 1024;
    private static readonly object LogLock = new();

    private static void AppendLog(string path, string message)
    {
        lock (LogLock)
        {
            try
            {
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > MaxLogBytes)
                {
                    var old = path + ".1";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(path, old);
                }
                File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss}] {message}\n");
            }
            catch
            {
                // 日志失败不影响主流程
            }
        }
    }

    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        // 插件数据目录（ClassIsland 提供的插件专属目录）
        var debugLogPath = Path.Combine(PluginConfigFolder, "debug.log");
        var gatewayLogPath = Path.Combine(PluginConfigFolder, "gateway.log");

        var store = new SettingsStore(PluginConfigFolder);
        store.Log = msg => AppendLog(debugLogPath, msg);
        store.LoadSettings();

        services.AddSingleton(store);
        services.AddSingleton(sp =>
        {
            var authSvc = new SeewoAuthService();
            authSvc.Diagnostic = msg => AppendLog(debugLogPath, msg);
            return authSvc;
        });
        services.AddSingleton<HostsFileService>();
        services.AddSingleton(sp =>
            new SeewoSsoGateway(sp.GetRequiredService<SeewoAuthService>(), store,
                msg => AppendLog(gatewayLogPath, msg)));
        services.AddSingleton<SeewoGatewayHostedService>();
        services.AddHostedService(sp => sp.GetRequiredService<SeewoGatewayHostedService>());

        services.AddSingleton<SeewoSettingsViewModel>();
        services.AddSettingsPage<SeewoSettingsPage>();
    }
}
