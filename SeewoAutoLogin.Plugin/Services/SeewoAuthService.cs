using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SeewoAutoLogin.Plugin.Models;

namespace SeewoAutoLogin.Plugin.Services;

/// <summary>某个账号的内存会话（token + 用户信息）。</summary>
public class SeewoSession
{
    public string AccountId { get; init; } = "";
    public string Token { get; set; } = "";
    public SeewoUserInfo? UserInfo { get; set; }
}

/// <summary>
/// 希沃登录协议实现（逆向自 Pro-Qin/SeewoAutoLogin）：
/// - 密码登录：POST edu.seewo.com/api/v1/auth/login（密码 MD5，伪装 okhttp UA）
/// - 用户信息：GET  edu.seewo.com/api/v2/user/info
/// - Token 换发：GET account.seewo.com/seewo-account/api/v1/auth/{token}/exchange
/// 会话按账号隔离存储；同账号请求串行化，避免并发登录互相覆盖。
/// </summary>
public class SeewoAuthService
{
    private const string LoginUrl = "https://edu.seewo.com/api/v1/auth/login";
    private const string UserInfoUrl = "https://edu.seewo.com/api/v2/user/info";
    private const string TokenExchangeUrl = "https://account.seewo.com/seewo-account/api/v1/auth/";
    private const string UserAgent = "okhttp/3.12.12";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http = new() { Timeout = RequestTimeout };

    /// <summary>诊断日志（宿主可选注入；不输出任何敏感信息）。</summary>
    public Action<string>? Diagnostic { get; set; }

    private readonly ConcurrentDictionary<string, SeewoSession> _sessions = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();

    public bool HasSession(string accountId) =>
        !string.IsNullOrEmpty(accountId) && _sessions.ContainsKey(accountId);

    public SeewoSession? GetSession(string accountId) =>
        accountId != null && _sessions.TryGetValue(accountId, out var s) ? s : null;

    /// <summary>用密码登录并缓存会话。同账号串行化。</summary>
    public async Task<SeewoLoginResult> LoginAsync(SeewoAccount account, string password)
    {
        var gate = _gates.GetOrAdd(account.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            var result = await LoginCoreAsync(account.Username, password);
            if (result.Success)
            {
                _sessions[account.Id] = new SeewoSession
                {
                    AccountId = account.Id,
                    Token = result.Token,
                    UserInfo = result.UserInfo
                };
                // 同步用户资料到账号（昵称/头像供界面与希沃快捷登录列表展示）
                account.UserInfo = result.UserInfo ?? account.UserInfo;
                account.LastError = null;
                account.LastFailureKind = SeewoFailureKind.None;
            }
            else
            {
                account.LastError = result.ErrorMessage;
                account.LastFailureKind = result.FailureKind;
            }
            account.LastLoginAtUtc = DateTime.UtcNow;
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>换发（续期）某账号的当前 token。失败时清除会话。</summary>
    public async Task<SeewoLoginResult> ExchangeAsync(SeewoAccount account)
    {
        var gate = _gates.GetOrAdd(account.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (!_sessions.TryGetValue(account.Id, out var session) ||
                string.IsNullOrWhiteSpace(session.Token))
                return SeewoLoginResult.Fail(SeewoFailureKind.Credential, "没有可用的登录令牌");

            var result = await ExchangeCoreAsync(session.Token);
            if (result.Success)
            {
                session.Token = result.Token;
                session.UserInfo = result.UserInfo ?? session.UserInfo;
                account.LastError = null;
                account.LastFailureKind = SeewoFailureKind.None;
            }
            else
            {
                _sessions.TryRemove(account.Id, out _);
                account.LastError = result.ErrorMessage;
                account.LastFailureKind = result.FailureKind;
            }
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    // ── 协议实现 ─────────────────────────────────────────────

    private async Task<SeewoLoginResult> LoginCoreAsync(string username, string password)
    {
        try
        {
            var traceId = Guid.NewGuid().ToString("N")[..32];
            var json = JsonSerializer.Serialize(new
            {
                username,
                password = ComputeMd5(password),
                captcha = (string?)null,
                phoneCountryCode = ""
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, LoginUrl)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("X-APM-TraceId", traceId);
            request.Headers.TryAddWithoutValidation("Cookie", "x-auth-app=EasiNote5; x-auth-token=");

            using var response = await _http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                var msg = ReadApiMessage(body) ?? $"HTTP {(int)response.StatusCode}";
                return SeewoLoginResult.Fail(ClassifyHttpStatus((int)response.StatusCode), msg);
            }
            var result = ParseLoginBody(body);
            if (result.Success)
            {
                // 登录成功后立即拉取用户资料（昵称 / 头像等），供界面与希沃快捷登录列表展示
                result.UserInfo = await FetchUserInfoAsync(result.Token);
            }
            return result;
        }
        catch (TaskCanceledException)
        {
            return SeewoLoginResult.Fail(SeewoFailureKind.Network, "请求超时");
        }
        catch (HttpRequestException ex)
        {
            return SeewoLoginResult.Fail(SeewoFailureKind.Network, $"网络请求失败：{ex.Message}");
        }
        catch (Exception ex)
        {
            return SeewoLoginResult.Fail(SeewoFailureKind.Unknown, ex.Message);
        }
    }

    private static SeewoLoginResult ParseLoginBody(string body)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(body);
            root = doc.RootElement.Clone();
        }
        catch
        {
            return SeewoLoginResult.Fail(SeewoFailureKind.Unknown, "登录接口返回了无法解析的数据");
        }

        var errorCode = GetJsonInt(root, "error_code") ?? GetJsonInt(root, "code");
        if (errorCode.HasValue && errorCode.Value != 0)
        {
            var apiMessage = ReadApiMessage(root);
            return SeewoLoginResult.Fail(
                ClassifyBusinessError(apiMessage),
                (string.IsNullOrWhiteSpace(apiMessage) ? "登录失败" : apiMessage)
                + $"（错误码 {errorCode.Value}）");
        }

        var token = GetJsonObject(root, "data") is { } data
            ? GetJsonString(data, "token")
            : "";
        if (string.IsNullOrWhiteSpace(token))
            token = GetJsonString(root, "token");
        if (!string.IsNullOrWhiteSpace(token))
            return new SeewoLoginResult { Success = true, Token = token };

        return SeewoLoginResult.Fail(SeewoFailureKind.Credential,
            ReadApiMessage(root) ?? "登录接口未返回登录令牌，请检查账号与密码");
    }

    public async Task<SeewoUserInfo?> FetchUserInfoAsync(string token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, UserInfoUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("X-auth-refer", "EnAppAndroid");
            request.Headers.TryAddWithoutValidation("X-Crypto-Version", "1");
            request.Headers.TryAddWithoutValidation("Cookie",
                $"x-auth-app=EasiNoteAndroid; x-auth-token={token}");
            using var response = await _http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            Diagnostic?.Invoke($"user/info HTTP {(int)response.StatusCode} len={body.Length}");
            if (!response.IsSuccessStatusCode)
            {
                Diagnostic?.Invoke($"user/info 失败响应前 120 字符：{body[..Math.Min(120, body.Length)]}");
                return null;
            }
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var errorCode = GetJsonInt(root, "error_code") ?? GetJsonInt(root, "code");
            if (errorCode.HasValue && errorCode.Value != 0)
            {
                Diagnostic?.Invoke($"user/info error_code={errorCode.Value}");
                return null;
            }
            if (GetJsonObject(root, "data") is not { } data)
            {
                Diagnostic?.Invoke("user/info 无 data 字段");
                return null;
            }

            var info = new SeewoUserInfo
            {
                NickName = GetJsonString(data, "nickName"),
                RealName = GetJsonString(data, "realName"),
                UserName = GetJsonString(data, "username"),
                Phone = GetJsonString(data, "phone"),
                PhotoUrl = GetJsonString(data, "photoUrl"),
                UnitName = GetJsonString(data, "unitName"),
                StageName = GetJsonString(data, "stageName"),
                SubjectName = GetJsonString(data, "subjectName"),
                AccountId = GetJsonString(data, "accountId"),
                Uid = GetJsonString(data, "uid")
            };
            // 头像回退：photoUrl 为空时尝试 userInfoExtendVo.picUrl
            if (string.IsNullOrWhiteSpace(info.PhotoUrl) &&
                GetJsonObject(data, "userInfoExtendVo") is { } extend)
            {
                info.PhotoUrl = GetJsonString(extend, "picUrl");
            }
            return info;
        }
        catch (Exception ex)
        {
            Diagnostic?.Invoke($"user/info 异常：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private async Task<SeewoLoginResult> ExchangeCoreAsync(string oldToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                TokenExchangeUrl + Uri.EscapeDataString(oldToken) + "/exchange");
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("x-auth-app", "EasiNote5");
            request.Headers.TryAddWithoutValidation("Cookie", "x-auth-app=EasiNote5");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var response = await _http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                return SeewoLoginResult.Fail(
                    ClassifyHttpStatus((int)response.StatusCode), $"HTTP {(int)response.StatusCode}");

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var data = GetJsonObject(root, "data") ?? root;
            var newToken = GetJsonString(data, "token");
            if (string.IsNullOrWhiteSpace(newToken))
                return SeewoLoginResult.Fail(SeewoFailureKind.Credential,
                    ReadApiMessage(root) ?? "Token 换发失败");

            var userInfo = await FetchUserInfoAsync(newToken);
            return new SeewoLoginResult
            {
                Success = true,
                Token = newToken,
                UserInfo = userInfo
            };
        }
        catch (TaskCanceledException)
        {
            return SeewoLoginResult.Fail(SeewoFailureKind.Network, "Token 换发超时");
        }
        catch (Exception ex)
        {
            return SeewoLoginResult.Fail(SeewoFailureKind.Network, ex.Message);
        }
    }

    // ── 失败分类 ─────────────────────────────────────────────

    /// <summary>业务错误码分类：「频繁/稍后/风控」类是临时问题，不能标成密码错误。</summary>
    private static SeewoFailureKind ClassifyBusinessError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return SeewoFailureKind.Credential;
        var text = message.ToLowerInvariant();
        if (text.Contains("验证码") || text.Contains("captcha"))
            return SeewoFailureKind.Captcha;
        if (text.Contains("频繁") || text.Contains("稍后") || text.Contains("稍候")
            || text.Contains("风控") || text.Contains("限流")
            || text.Contains("too many") || text.Contains("try again") || text.Contains("frequent"))
            return SeewoFailureKind.Server;
        return SeewoFailureKind.Credential;
    }

    private static SeewoFailureKind ClassifyHttpStatus(int statusCode)
    {
        if (statusCode is 401 or 403) return SeewoFailureKind.Credential;
        if (statusCode is 408 or 425) return SeewoFailureKind.Network;
        if (statusCode == 429 || statusCode >= 500) return SeewoFailureKind.Server;
        return SeewoFailureKind.Unknown;
    }

    private static string? ReadApiMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return ReadApiMessage(doc.RootElement);
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadApiMessage(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        var message = GetJsonString(root, "message");
        if (string.IsNullOrWhiteSpace(message)) message = GetJsonString(root, "msg");
        return string.IsNullOrWhiteSpace(message) ? null : message;
    }

    // ── JSON 工具 ────────────────────────────────────────────

    private static string GetJsonString(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object &&
        el.TryGetProperty(name, out var prop) &&
        prop.ValueKind == JsonValueKind.String
            ? prop.GetString() ?? ""
            : "";

    private static int? GetJsonInt(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object ||
            !el.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n)) return n;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out n)) return n;
        return null;
    }

    private static JsonElement? GetJsonObject(JsonElement el, string name)
    {
        if (el.ValueKind == JsonValueKind.Object &&
            el.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.Object)
            return value;
        return null;
    }

    private static string ComputeMd5(string input)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
