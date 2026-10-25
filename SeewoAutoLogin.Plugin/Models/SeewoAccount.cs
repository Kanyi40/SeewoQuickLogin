namespace SeewoAutoLogin.Plugin.Models;

/// <summary>
/// 一个希沃账号。密码以 DPAPI 密文（base64）持久化，明文只在内存中短暂存在。
/// </summary>
public class SeewoAccount
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>登录账号（手机号）。</summary>
    public string Username { get; set; } = "";

    /// <summary>显示名称（选填，为空时用用户信息昵称 / 用户名）。</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>DPAPI 加密后的密码（base64），null = 未录入。</summary>
    public string? EncryptedPassword { get; set; }

    public SeewoUserInfo? UserInfo { get; set; }

    public string? LastError { get; set; }
    public SeewoFailureKind LastFailureKind { get; set; } = SeewoFailureKind.None;
    public DateTime? LastLoginAtUtc { get; set; }

    /// <summary>今日被希沃请求登录的次数（按日期滚动清零）。</summary>
    public int RequestCountToday { get; set; }
    public string RequestCountDate { get; set; } = "";

    /// <summary>显示名称的回退顺序：备注 → 昵称 → 用户名。</summary>
    public string EffectiveName =>
        string.IsNullOrWhiteSpace(DisplayName)
            ? (string.IsNullOrWhiteSpace(UserInfo?.NickName) ? Username : UserInfo!.NickName)
            : DisplayName;

    public void CountRequest()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        if (RequestCountDate != today)
        {
            RequestCountDate = today;
            RequestCountToday = 0;
        }
        RequestCountToday++;
    }
}
