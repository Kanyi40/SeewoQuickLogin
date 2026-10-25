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
    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        // 插件数据目录（ClassIsland 提供的插件专属目录）
        var store = new SettingsStore(PluginConfigFolder);
        store.LoadSettings();

        services.AddSingleton(store);
        services.AddSingleton(sp =>
        {
            var authSvc = new SeewoAuthService();
            authSvc.Diagnostic = msg =>
            {
                try
                {
                    File.AppendAllText(Path.Combine(PluginConfigFolder, "debug.log"),
                        $"[{DateTime.Now:HH:mm:ss}] {msg}\n");
                }
                catch { }
            };
            return authSvc;
        });
        services.AddSingleton<HostsFileService>();
        services.AddSingleton(sp =>
            new SeewoSsoGateway(sp.GetRequiredService<SeewoAuthService>(), store, msg =>
            {
                Console.WriteLine($"[SeewoAutoLogin] {msg}");
                try
                {
                    File.AppendAllText(Path.Combine(PluginConfigFolder, "gateway.log"),
                        $"[{DateTime.Now:HH:mm:ss}] {msg}\n");
                }
                catch { }
            }));
        services.AddSingleton<SeewoGatewayHostedService>();
        services.AddHostedService(sp => sp.GetRequiredService<SeewoGatewayHostedService>());

        services.AddSingleton<SeewoSettingsViewModel>();
        services.AddSettingsPage<SeewoSettingsPage>();
    }
}
