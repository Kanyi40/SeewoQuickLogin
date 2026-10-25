namespace SeewoAutoLogin.Plugin.Models;

/// <summary>
/// 登录 / 续期失败的类型。
/// 区分「凭据真的失效（要用户处理）」与「临时故障（自动重试）」——
/// 后者绝不能提示"密码错误"，否则一次网络抖动就会误报。
/// </summary>
public enum SeewoFailureKind
{
    None = 0,
    /// <summary>账号 / 密码 / 令牌无效，重试无意义，需要用户处理。</summary>
    Credential,
    /// <summary>网络、超时等临时传输问题，稍后重试可能成功。</summary>
    Network,
    /// <summary>服务端 5xx / 限流等临时问题，稍后重试可能成功。</summary>
    Server,
    /// <summary>希沃要求图片验证码（error_code 4906）。风控已介入，自动重试只会加重。</summary>
    Captcha,
    /// <summary>无法归类的异常，按"不误报密码问题"处理。</summary>
    Unknown
}

/// <summary>登录 / 续期结果。</summary>
public class SeewoLoginResult
{
    public bool Success { get; set; }
    public string Token { get; set; } = "";
    public string? ErrorMessage { get; set; }
    public SeewoFailureKind FailureKind { get; set; } = SeewoFailureKind.None;
    public SeewoUserInfo? UserInfo { get; set; }

    public static SeewoLoginResult Fail(SeewoFailureKind kind, string message) =>
        new() { Success = false, FailureKind = kind, ErrorMessage = message };
}
