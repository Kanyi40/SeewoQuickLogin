using System.IO;
using System.Net.Http;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using SeewoAutoLogin.Plugin.Models;

namespace SeewoAutoLogin.Plugin.ViewModels;

/// <summary>账号列表条目的展示状态。</summary>
public enum AccountStatusKind
{
    Neutral,
    Ok,
    Warn,
    Error
}

/// <summary>账号列表条目 ViewModel（包装 SeewoAccount 的展示状态）。</summary>
public partial class AccountVm : ObservableObject
{
    private static readonly HttpClient AvatarHttp = new() { Timeout = TimeSpan.FromSeconds(15) };
    private string _loadedPhotoUrl = "";

    public SeewoAccount Account { get; }

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _subLine = "";

    [ObservableProperty]
    private string _statusText = "未测试";

    [ObservableProperty]
    private AccountStatusKind _statusKind = AccountStatusKind.Neutral;

    /// <summary>供 AXAML Classes 绑定切换徽章配色。</summary>
    public bool IsOk => StatusKind == AccountStatusKind.Ok;
    public bool IsWarn => StatusKind == AccountStatusKind.Warn;
    public bool IsError => StatusKind == AccountStatusKind.Error;
    public bool IsNeutral => StatusKind == AccountStatusKind.Neutral;

    [ObservableProperty]
    private string _avatarText = "";

    [ObservableProperty]
    private Bitmap? _avatarImage;

    public bool HasAvatarImage => AvatarImage != null;

    partial void OnAvatarImageChanged(Bitmap? value) => OnPropertyChanged(nameof(HasAvatarImage));

    partial void OnStatusKindChanged(AccountStatusKind value)
    {
        OnPropertyChanged(nameof(IsOk));
        OnPropertyChanged(nameof(IsWarn));
        OnPropertyChanged(nameof(IsError));
        OnPropertyChanged(nameof(IsNeutral));
    }

    public AccountVm(SeewoAccount account)
    {
        Account = account;
        Refresh(account);
    }

    /// <summary>从数据模型刷新展示状态（登录成功 / 失败 / 希沃请求后调用）。</summary>
    public void Refresh(SeewoAccount account)
    {
        Name = account.EffectiveName;
        AvatarText = string.IsNullOrEmpty(Name) ? "?" : Name[..1].ToUpperInvariant();

        var info = account.UserInfo;
        var infoParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(account.Username))
            infoParts.Add(account.Username);
        if (!string.IsNullOrWhiteSpace(info?.UnitName))
            infoParts.Add(info!.UnitName);
        if (!string.IsNullOrWhiteSpace(info?.SubjectName))
            infoParts.Add(info!.SubjectName);
        if (account.RequestCountToday > 0)
            infoParts.Add($"今日已服务 {account.RequestCountToday} 次登录");
        SubLine = string.Join(" · ", infoParts);

        if (string.IsNullOrEmpty(account.EncryptedPassword))
        {
            StatusText = "未录入密码";
            StatusKind = AccountStatusKind.Neutral;
            return;
        }

        switch (account.LastFailureKind)
        {
            case SeewoFailureKind.None:
                StatusText = "✓ 会话有效";
                StatusKind = AccountStatusKind.Ok;
                break;
            case SeewoFailureKind.Credential:
                StatusText = $"✗ {account.LastError ?? "密码已失效"}";
                StatusKind = AccountStatusKind.Error;
                break;
            case SeewoFailureKind.Captcha:
                StatusText = $"⚠ 触发风控（{account.LastError}）";
                StatusKind = AccountStatusKind.Warn;
                break;
            case SeewoFailureKind.Network:
            case SeewoFailureKind.Server:
                StatusText = $"⚠ 临时失败：{account.LastError}";
                StatusKind = AccountStatusKind.Warn;
                break;
            default:
                StatusText = $"⚠ {account.LastError ?? "未知错误"}";
                StatusKind = AccountStatusKind.Warn;
                break;
        }

        LoadAvatar(info?.PhotoUrl);
    }

    /// <summary>异步加载希沃头像；失败时保留字母头像。</summary>
    private async void LoadAvatar(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            _loadedPhotoUrl = "";
            AvatarImage = null;
            return;
        }
        if (url == _loadedPhotoUrl && AvatarImage != null) return;
        try
        {
            var bytes = await AvatarHttp.GetByteArrayAsync(url);
            using var ms = new MemoryStream(bytes);
            var bmp = new Bitmap(ms);
            _loadedPhotoUrl = url;
            AvatarImage = bmp;
        }
        catch
        {
            // 拉取失败：保持字母头像，不打断界面
        }
    }
}
