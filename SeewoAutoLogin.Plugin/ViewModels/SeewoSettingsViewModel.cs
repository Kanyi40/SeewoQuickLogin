using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SeewoAutoLogin.Plugin.Models;
using SeewoAutoLogin.Plugin.Services;

namespace SeewoAutoLogin.Plugin.ViewModels;

/// <summary>设置页 ViewModel。</summary>
public partial class SeewoSettingsViewModel : ObservableObject
{
    private readonly SettingsStore _store;
    private readonly SeewoSsoGateway _gateway;
    private readonly SeewoAuthService _auth;
    private readonly HostsFileService _hosts;

    private List<SeewoAccount> _accounts = new();

    [ObservableProperty]
    private bool _enabled = true;

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private bool _isAddFormOpen;

    [ObservableProperty]
    private string _addUsername = "";

    [ObservableProperty]
    private string _addPassword = "";

    [ObservableProperty]
    private string _addDisplayName = "";

    /// <summary>正在编辑的账号 Id；null = 新增模式。</summary>
    [ObservableProperty]
    private string? _editingAccountId;

    public string FormTitle => EditingAccountId != null ? "编辑账号" : "添加账号";
    public bool IsEditMode => EditingAccountId != null;

    partial void OnEditingAccountIdChanged(string? value)
    {
        OnPropertyChanged(nameof(FormTitle));
        OnPropertyChanged(nameof(IsEditMode));
    }

    [ObservableProperty]
    private bool _isGatewayRunning;

    [ObservableProperty]
    private string _gatewayStatusText = "";

    [ObservableProperty]
    private bool _hostsMapped;

    [ObservableProperty]
    private string _hostsStatusText = "检测中…";

    /// <summary>网关运行在备用端口（旧版白板可能不认，等于对白板失效）。</summary>
    [ObservableProperty]
    private bool _isGatewayFallback;

    public bool IsGatewayPortOk => IsGatewayRunning && !IsGatewayFallback;

    [ObservableProperty]
    private string _lastSsoRequestText = "从未";

    [ObservableProperty]
    private string _hostsHintVisibility = "Collapsed"; // 简化：由页面用 bool 转换器处理

    public bool ShowHostsHint => IsGatewayRunning && !HostsMapped;

    public ObservableCollection<AccountVm> Accounts { get; } = new();

    public SeewoSettingsViewModel(
        SettingsStore store, SeewoSsoGateway gateway,
        SeewoAuthService auth, HostsFileService hosts)
    {
        _store = store;
        _gateway = gateway;
        _auth = auth;
        _hosts = hosts;

        _store.LoadSettings();
        _enabled = _store.Settings.Enabled;
        LoadAccounts();
        RefreshStatus();
        RefreshMissingProfilesInBackground();
    }

    /// <summary>
    /// 后台自愈：对缺少用户资料（昵称/头像）的账号补一次登录拉取，完成后回 UI 线程刷新列表。
    /// 覆盖旧版本遗留数据（UserInfo 为空）的场景。
    /// </summary>
    private async void RefreshMissingProfilesInBackground()
    {
        try
        {
            var missing = _accounts
                .Where(a => !string.IsNullOrEmpty(a.EncryptedPassword) &&
                            string.IsNullOrEmpty(a.UserInfo?.PhotoUrl) &&
                            !_auth.HasSession(a.Id))
                .ToList();
            if (missing.Count == 0) return;

            foreach (var account in missing)
            {
                try
                {
                    var password = SecureStore.Unprotect(account.EncryptedPassword);
                    if (string.IsNullOrEmpty(password)) continue;
                    await _auth.LoginAsync(account, password);
                }
                catch
                {
                    // 单个账号失败不影响其它账号
                }
            }
            SaveAccounts();
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var vm in Accounts)
                    vm.Refresh(vm.Account);
            });
        }
        catch
        {
            // 后台自愈失败不能影响应用（async void 的异常会终止进程）
        }
    }

    // ── 数据装载 ─────────────────────────────────────────────

    private void LoadAccounts()
    {
        _accounts = _store.LoadAccounts();
        Accounts.Clear();
        foreach (var a in _accounts)
            Accounts.Add(new AccountVm(a));
    }

    private void SaveAccounts() => _store.SaveAccounts(_accounts);

    /// <summary>刷新运行状态卡（页面定时器与操作后调用）。</summary>
    public void RefreshStatus()
    {
        IsGatewayRunning = _gateway.IsRunning;
        IsGatewayFallback = _gateway.IsRunning && _gateway.Port != _store.Settings.GatewayPort;
        GatewayStatusText = _gateway.IsRunning
            ? (IsGatewayFallback
                ? $"127.0.0.1:{_gateway.Port} · 备用端口（旧版白板可能不识别，请检查希沃服务占用）"
                : $"127.0.0.1:{_gateway.Port} · 运行中")
            : (_gateway.LastStartError ?? "未运行");
        HostsMapped = _hosts.HasMapping();
        HostsStatusText = HostsMapped ? "已生效" : "未生效";
        LastSsoRequestText = _gateway.LastSsoRequestAtUtc is { } t
            ? $"{(long)(DateTime.UtcNow - t).TotalSeconds} 秒前"
            : "从未";
        OnPropertyChanged(nameof(ShowHostsHint));
        OnPropertyChanged(nameof(IsGatewayPortOk));
    }

    public void RefreshAccount(AccountVm vm) => vm.Refresh(vm.Account);

    // ── 总开关 ───────────────────────────────────────────────

    partial void OnEnabledChanged(bool value)
    {
        _store.Settings.Enabled = value;
        _store.SaveSettings();
        if (value)
        {
            // 启动可能在端口被占用时阻塞数秒（接管希沃服务），必须放到后台线程，避免卡住 UI
            StatusMessage = "正在启动 SSO 网关…";
            var port = _store.Settings.GatewayPort;
            _ = Task.Run(() =>
            {
                var ok = _gateway.TryStart(port);
                Dispatcher.UIThread.Post(() =>
                {
                    StatusMessage = ok
                        ? $"SSO 网关已启动：127.0.0.1:{_gateway.Port}"
                        : $"网关启动失败：{_gateway.LastStartError}";
                    RefreshStatus();
                });
            });
        }
        else
        {
            _gateway.Stop();
            StatusMessage = "SSO 网关已停止";
        }
        RefreshStatus();
    }

    // ── 添加账号 ─────────────────────────────────────────────

    [RelayCommand]
    private void OpenAddForm()
    {
        EditingAccountId = null;
        AddUsername = "";
        AddPassword = "";
        AddDisplayName = "";
        IsAddFormOpen = true;
    }

    [RelayCommand]
    private void EditAccount(AccountVm? vm)
    {
        if (vm == null) return;
        EditingAccountId = vm.Account.Id;
        AddUsername = vm.Account.Username;
        AddPassword = ""; // 留空 = 不修改密码
        AddDisplayName = vm.Account.DisplayName;
        IsAddFormOpen = true;
    }

    [RelayCommand]
    private void CancelAddForm()
    {
        IsAddFormOpen = false;
        EditingAccountId = null;
    }

    /// <summary>
    /// 保存并测试登录：先用输入的凭据真实登录一次，
    /// 成功才保存（并带回落取到的昵称/头像）；失败则提示原因、不落盘。
    /// </summary>
    [RelayCommand]
    private async Task SaveAccountAsync()
    {
        var username = AddUsername.Trim();
        var isEdit = EditingAccountId != null;
        var existing = isEdit ? _accounts.FirstOrDefault(a => a.Id == EditingAccountId) : null;
        if (isEdit && existing == null)
        {
            StatusMessage = "要编辑的账号不存在，可能已被删除";
            IsAddFormOpen = false;
            EditingAccountId = null;
            return;
        }

        if (string.IsNullOrWhiteSpace(username) || (existing == null && string.IsNullOrWhiteSpace(AddPassword)))
        {
            StatusMessage = "账号和密码为必填项";
            return;
        }
        // 判重：新增时不能与任何账号重复；编辑时不能与"其它"账号重复
        if (_accounts.Any(a => a.Id != existing?.Id && a.Username == username))
        {
            StatusMessage = "该账号已存在";
            return;
        }

        // 编辑时密码留空 = 沿用已保存密码
        string password;
        if (!string.IsNullOrWhiteSpace(AddPassword))
        {
            password = AddPassword;
        }
        else
        {
            var stored = SecureStore.Unprotect(existing!.EncryptedPassword);
            if (string.IsNullOrEmpty(stored))
            {
                StatusMessage = "本地保存的密码无法解密，请输入新密码";
                return;
            }
            password = stored;
        }

        // ① 先测试登录（用临时探针对象，不污染列表数据）
        StatusMessage = $"正在测试登录：{username}…";
        var probe = new SeewoAccount { Id = existing?.Id ?? Guid.NewGuid().ToString("N"), Username = username };
        var result = await _auth.LoginAsync(probe, password);
        if (!result.Success)
        {
            StatusMessage = $"登录失败，未保存：{result.ErrorMessage}";
            return;
        }

        // ② 登录成功才落盘
        var encrypted = SecureStore.Protect(password);
        if (encrypted == null)
        {
            StatusMessage = "密码加密失败（DPAPI 不可用），未保存";
            return;
        }

        if (existing != null)
        {
            existing.Username = username;
            existing.DisplayName = AddDisplayName.Trim(); // 名称留空时展示层自动回退到希沃昵称
            existing.EncryptedPassword = encrypted;
            existing.UserInfo = probe.UserInfo ?? existing.UserInfo;
            existing.LastError = null;
            existing.LastFailureKind = SeewoFailureKind.None;
            SaveAccounts();
            Accounts.First(x => x.Account.Id == existing.Id).Refresh(existing);
        }
        else
        {
            // 直接复用探针对象（会话已按其 Id 缓存，令牌可继续复用）
            probe.DisplayName = AddDisplayName.Trim();
            probe.EncryptedPassword = encrypted;
            _accounts.Add(probe);
            SaveAccounts();
            Accounts.Add(new AccountVm(probe));
        }

        IsAddFormOpen = false;
        EditingAccountId = null;
        StatusMessage = $"登录成功，账号已保存：{(existing?.EffectiveName ?? probe.EffectiveName)}"
                        + (string.IsNullOrWhiteSpace(AddDisplayName) && existing == null ? "（名称取自希沃昵称）" : "");
    }

    // ── 账号操作 ─────────────────────────────────────────────

    [RelayCommand]
    private async Task TestLoginAsync(AccountVm? vm)
    {
        if (vm == null) return;
        var account = vm.Account;
        var password = SecureStore.Unprotect(account.EncryptedPassword);
        if (string.IsNullOrEmpty(password))
        {
            account.LastError = "本地保存的密码已无法解密（可能更换了 Windows 账户），请删除后重新添加";
            account.LastFailureKind = SeewoFailureKind.Credential;
            vm.Refresh(account);
            return;
        }

        // 已有会话时直接回报有效，避免每次点击都打希沃登录接口（降低风控风险）
        if (_auth.HasSession(account.Id))
        {
            StatusMessage = "会话仍然有效（未重复请求希沃登录接口）";
            vm.Refresh(account);
            return;
        }

        StatusMessage = $"正在测试登录：{account.EffectiveName}…";
        var result = await _auth.LoginAsync(account, password);        if (result.Success)
        {
            StatusMessage = $"登录成功：{result.UserInfo?.NickName ?? account.EffectiveName}";
        }
        else
        {
            StatusMessage = $"登录失败：{result.ErrorMessage}";
        }
        SaveAccounts();
        vm.Refresh(account);
    }

    [RelayCommand]
    private void DeleteAccount(AccountVm? vm)
    {
        if (vm == null) return;
        var account = vm.Account;
        _accounts.Remove(account);
        SaveAccounts();
        Accounts.Remove(vm);
        // 清理运行时缓存，避免残留会话/冷却记录
        _auth.RemoveSession(account.Id);
        _gateway.ClearFailure(account.Id);
        StatusMessage = $"已删除账号：{account.EffectiveName}";
    }

    // ── 辅助动作 ─────────────────────────────────────────────

    [RelayCommand]
    private void OpenDataFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _store.Folder,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"打开目录失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenHostsFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _hosts.HostsFolder,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"打开目录失败：{ex.Message}";
        }
    }

    /// <summary>复制 hosts 映射行到剪贴板（剪贴板访问需要 TopLevel，由页面代码后台实现）。</summary>
    public event Action<string>? CopyToClipboardRequested;

    [RelayCommand]
    private void CopyHostsLine() =>
        CopyToClipboardRequested?.Invoke(HostsFileService.MappingLine);
}
