namespace SeewoAutoLogin.Plugin.Models;

/// <summary>插件全局设置（settings.json）。</summary>
public class PluginSettings
{
    /// <summary>启用快捷登录（控制 SSO 网关启停）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>SSO 网关端口。希沃硬编码请求 24300，正常情况不要改。</summary>
    public int GatewayPort { get; set; } = 24300;

    /// <summary>登录失败后的冷却秒数（冷却期内不重试，防风控）。</summary>
    public int FailureCooldownSeconds { get; set; } = 60;

    /// <summary>希沃快捷登录列表最多展示的账号数（希沃侧上限 6）。</summary>
    public int MaxVisibleAccounts { get; set; } = 6;

    /// <summary>
    /// 端口被希沃自家服务（EasiAgent 等）占用时自动结束它并接管。
    /// 希沃会自行重新拉起 EasiAgent，届时端口已被本插件占用，白板将走本插件网关。
    /// 仅对希沃进程名生效，其它进程占用不处理。
    /// </summary>
    public bool AutoTakeOverPort { get; set; } = true;
}
