using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SeewoAutoLogin.Plugin.Models;

namespace SeewoAutoLogin.Plugin.Services;

/// <summary>
/// 本地 SSO 网关：希沃白板登录界面轮询 local.id.seewo.com:24300（经 hosts 指向本机），
/// 本网关应答账号列表与登录令牌，实现「快捷登录」。
///
/// 实现说明：使用 TcpListener 绑定回环地址手写最小 HTTP 应答——
/// HttpListener 注册 local.id.seewo.com 前缀需要 URLACL/管理员权限，TcpListener 不需要。
/// 安全：只服务回环来源 + Host 白名单 + 限流 + 日志脱敏（本类不打印任何令牌/密码）。
/// </summary>
public class SeewoSsoGateway
{
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        HostsFileService.HostName, "127.0.0.1", "localhost"
    };

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    /// <summary>登录失败冷却：冷却期内不再打登录接口（防风控）。</summary>
    private readonly ConcurrentDictionary<string, (DateTime AtUtc, string Message)> _failures = new();

    /// <summary>最近一次收到希沃 SSOLOGIN 请求的时间（UTC），用于健康自检。</summary>
    public DateTime? LastSsoRequestAtUtc { get; private set; }

    public bool IsRunning { get; private set; }
    public int Port { get; private set; } = 24300;

    /// <summary>最近一次启动失败的-readable原因（端口占用等），供设置页展示。</summary>
    public string? LastStartError { get; private set; }

    private readonly SeewoAuthService _auth;
    private readonly SettingsStore _store;
    private readonly Action<string>? _log;

    public SeewoSsoGateway(SeewoAuthService auth, SettingsStore store, Action<string>? log = null)
    {
        _auth = auth;
        _store = store;
        _log = log;
    }

    /// <summary>
    /// 启动网关（不抛异常的包装，供自动重试使用）。
    /// </summary>
    public bool TryStart(int port)
    {
        try
        {
            Start(port);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 启动网关。优先绑定首选端口；被占用时依次尝试 24301~24304
    /// （白板客户端请求 24300 失败会自动重试 +1~+4，见 EasiNote 反编译 RetryPostRequestWithPort），
    /// 全部失败且允许时才对希沃服务做接管。
    /// </summary>
    public void Start(int port)
    {
        if (IsRunning) return;
        Port = port;
        LastStartError = null;

        var bound = TryBind(port, out var bindError);
        if (!bound)
        {
            _log?.Invoke($"绑定首选端口 {port} 失败（占用者：{DescribePortOwner(port)}；{bindError}）");

            // 接管优先：24300 是新旧客户端唯一共同端口。被希沃服务（EasiAgent 等）占用时
            // 直接结束它抢回首选端口，而不是先回退——旧版客户端（如 5.2.4.8772）不重试
            // 备用端口，回退成功对白板等于失效（2026-10-26 学校大屏 gateway.log 实证）。
            if (_store.Settings.AutoTakeOverPort && TryStopSeewoServiceOnPort(port))
            {
                for (var i = 0; i < 3 && !bound; i++)
                {
                    Thread.Sleep(1200);
                    bound = TryBind(port, out bindError);
                    if (bound) Port = port;
                }
            }

            // 接管失败或被禁止：回退到客户端重试范围端口兜底
            for (var p = port + 1; p <= port + 4 && !bound; p++)
            {
                bound = TryBind(p, out bindError);
                if (bound) Port = p;
            }
        }
        if (!bound)
        {
            LastStartError = $"端口 {port}~{port + 4} 均被占用：{bindError}";
            throw new InvalidOperationException(LastStartError);
        }

        IsRunning = true;
        _cts = new CancellationTokenSource();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        if (Port != port)
        {
            // 旧版白板（如 5.2.4.8772）可能没有 RetryPostRequestWithPort 端口重试逻辑，
            // 回退端口下快捷登录可能不生效——明确提示，便于现场处置
            _log?.Invoke($"警告：已回退到备用端口 127.0.0.1:{Port}（首选 {port} 被占用）。" +
                         "旧版希沃白板可能不尝试备用端口，若白板不显示账号，请结束希沃服务（EasiAgent）后重启本插件。");
        }
        _log?.Invoke($"SSO 网关已启动：127.0.0.1:{Port}");
    }

    /// <summary>查询端口占用者描述（用于诊断日志），如「EasiAgent(pid=1234)」「http.sys(系统保留)」。</summary>
    private string DescribePortOwner(int port)
    {
        try
        {
            var psi = new ProcessStartInfo("netstat", "-ano -p tcp")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true
            };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);

            foreach (var line in output.Split('\n'))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5 || !parts[0].Equals("TCP", StringComparison.OrdinalIgnoreCase) ||
                    !parts[3].Equals("LISTENING", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!parts[1].EndsWith($":{port}", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!int.TryParse(parts[4], out var pid))
                    continue;
                if (pid is 4 or 0)
                    return "http.sys(系统保留)";
                var proc = Process.GetProcessById(pid);
                return $"{proc.ProcessName}(pid={pid})";
            }
            return "未发现监听进程";
        }
        catch (Exception ex)
        {
            return $"查询失败({ex.GetType().Name})";
        }
    }

    private bool TryBind(int port, out string? error)
    {
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 找出占用端口的进程，若是希沃自家服务（EasiAgent/EasiNote 系列）则结束它。
    /// 非希沃进程一律不动。需要当前进程有足够权限（ClassIsland 提权运行时可用）。
    /// </summary>
    private bool TryStopSeewoServiceOnPort(int port)
    {
        try
        {
            var psi = new ProcessStartInfo("netstat", "-ano -p tcp")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true
            };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);

            foreach (var line in output.Split('\n'))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                // TCP  本地地址  远程地址  状态  PID
                if (parts.Length < 5 || !parts[0].Equals("TCP", StringComparison.OrdinalIgnoreCase) ||
                    !parts[3].Equals("LISTENING", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!parts[1].EndsWith($":{port}", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!int.TryParse(parts[4], out var pid))
                    continue;

                // PID 4 = http.sys（内核）：希沃服务常用 HttpListener 注册端口，
                // 此时直接按进程名查找并结束希沃服务
                if (pid is 4 or 0)
                    return TryKillSeewoServiceByName();

                var proc = Process.GetProcessById(pid);
                var name = proc.ProcessName;
                if (!name.StartsWith("EasiAgent", StringComparison.OrdinalIgnoreCase) &&
                    !name.StartsWith("EasiNote", StringComparison.OrdinalIgnoreCase))
                {
                    _log?.Invoke($"端口 {port} 被非希沃进程 {name}(pid={pid}) 占用，不自动结束");
                    return false;
                }

                _log?.Invoke($"端口 {port} 被希沃服务 {name}(pid={pid}) 占用，自动结束以接管（希沃会自行重新拉起它）");
                proc.Kill();
                proc.WaitForExit(3000);
                Thread.Sleep(800);
                return true;
            }
            // netstat 未找到监听项（可能为 http.sys 保留）时也按名字找
            return TryKillSeewoServiceByName();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"接管端口 {port} 失败：{ex.Message}");
        }
        return false;
    }

    /// <summary>按进程名查找并结束希沃后台服务（EasiAgent/EasiNote 系列）。</summary>
    private bool TryKillSeewoServiceByName()
    {
        try
        {
            var victims = Process.GetProcesses()
                .Where(p =>
                {
                    try
                    {
                        var n = p.ProcessName;
                        return n.StartsWith("EasiAgent", StringComparison.OrdinalIgnoreCase) ||
                               n.StartsWith("EasiNote", StringComparison.OrdinalIgnoreCase);
                    }
                    catch
                    {
                        return false;
                    }
                })
                .ToList();
            if (victims.Count == 0)
            {
                _log?.Invoke("未找到可结束的希沃服务进程");
                return false;
            }
            foreach (var proc in victims)
            {
                _log?.Invoke($"结束希沃服务 {proc.ProcessName}(pid={proc.Id}) 以接管端口（希沃会自行重新拉起它）");
                try
                {
                    proc.Kill();
                    proc.WaitForExit(3000);
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"结束 {proc.ProcessName} 失败：{ex.Message}");
                }
            }
            Thread.Sleep(800);
            return true;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"按名结束希沃服务失败：{ex.Message}");
            return false;
        }
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _log?.Invoke("SSO 网关已停止");
    }

    // ── 连接循环 ─────────────────────────────────────────────

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var listener = _listener!;
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            _ = Task.Run(() => HandleClientAsync(client, ct), ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using var _ = client;
            var remote = (IPEndPoint)client.Client.RemoteEndPoint!;
            if (!IPAddress.IsLoopback(remote.Address)) return; // 只服务本机

            client.ReceiveTimeout = 15000;
            client.SendTimeout = 15000;
            var stream = client.GetStream();

            // ── 读请求头 ──
            var headerBytes = new MemoryStream();
            var buffer = new byte[4096];
            int headerEnd;
            while ((headerEnd = FindHeaderEnd(headerBytes)) < 0)
            {
                if (headerBytes.Length > 64 * 1024) return; // 防恶意超大头
                var n = await stream.ReadAsync(buffer, ct);
                if (n == 0) return;
                headerBytes.Write(buffer, 0, n);
            }

            var head = Encoding.Latin1.GetString(headerBytes.ToArray());
            var lines = head.Split("\r\n");
            var requestLine = lines[0].Split(' ');
            if (requestLine.Length < 2) return;
            var method = requestLine[0];
            var rawPath = requestLine[1];
            var queryIdx = rawPath.IndexOf('?');
            var path = (queryIdx >= 0 ? rawPath[..queryIdx] : rawPath).TrimEnd('/');
            if (path.Length == 0) path = "/";

            string? host = null, origin = null;
            var contentLength = 0;
            foreach (var line in lines.Skip(1))
            {
                var idx = line.IndexOf(':');
                if (idx <= 0) continue;
                var name = line[..idx].Trim();
                var value = line[(idx + 1)..].Trim();
                if (name.Equals("Host", StringComparison.OrdinalIgnoreCase)) host = value;
                else if (name.Equals("Origin", StringComparison.OrdinalIgnoreCase)) origin = value;
                else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(value, out contentLength);
            }

            // ── 读请求体（/savedata 有 POST body，直接丢弃）──
            var bodyReceived = headerBytes.Length - (headerEnd + 4);
            var remaining = Math.Min(contentLength, 1024 * 1024) - bodyReceived;
            var discard = new byte[8192];
            while (remaining > 0)
            {
                var n = await stream.ReadAsync(
                    discard, 0, (int)Math.Min(discard.Length, remaining), ct);
                if (n == 0) break;
                remaining -= n;
            }

            // Host 白名单（防任意本机进程伪造）
            var hostName = (host ?? "").Split(':')[0];
            if (!AllowedHosts.Contains(hostName))
            {
                await WriteJsonAsync(stream, 404, new { message = "not_found", statusCode = "404" });
                return;
            }

            // CORS：希沃端 CEF 页面会带 Origin，回显放行；本机绑定本身已挡住外部来源
            if (!string.IsNullOrEmpty(origin))
            {
                // 只回显本地来源，其它一律拒绝
                if (origin.Contains("local.id.seewo.com") || origin.Contains("127.0.0.1") ||
                    origin.Contains("localhost"))
                {
                    /* 回显在响应头统一处理 */
                }
                else
                {
                    await WriteJsonAsync(stream, 403, new { message = "forbidden_origin", statusCode = "403" });
                    return;
                }
            }

            if (method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(stream, 200, new { message = "success", statusCode = "200" });
                return;
            }

            await RouteAsync(stream, method, path, ct);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"网关连接处理异常：{ex.GetType().Name}");
        }
    }

    private async Task RouteAsync(NetworkStream stream, string method, string path, CancellationToken ct)
    {
        _log?.Invoke($"请求：{method} {path}");
        // GET /getData/SSOLOGIN → 账号列表
        if (method == "GET" && path.Equals("/getData/SSOLOGIN", StringComparison.OrdinalIgnoreCase))
        {
            LastSsoRequestAtUtc = DateTime.UtcNow;
            var settings = _store.Settings;
            var accounts = _store.LoadAccounts()
                .Where(a => !string.IsNullOrEmpty(a.Username))
                .Take(settings.MaxVisibleAccounts)
                .ToList();

            // 字段类型严格按白板端 SeewoUserInfo（反编译自 EasiNote5 5.2.4.9838）：
            // pt_sysicourl=字符串数组、pt_timestamp=long 数值，类型不匹配会导致整个列表反序列化失败。
            // 关键：白板 XAML 角标 Image 绑定 SysIcoUrl[1]（固定取第二个元素，希沃自身传 16px/24px 两个 URL），
            // 列表必须至少两个元素，否则索引越界、绑定静默失败、角标显示空圆圈。
            var sysIcoUrls = new List<string>
            {
                $"http://local.id.seewo.com:{Port}/sysicon16.png",
                $"http://local.id.seewo.com:{Port}/sysicon.png"
            };
            var items = accounts.Select(a => (object)new Dictionary<string, object>
            {
                ["pt_nickname"] = a.EffectiveName,
                ["pt_appid"] = a.Id,
                ["pt_userid"] = a.Id,
                ["pt_username"] = string.IsNullOrWhiteSpace(a.UserInfo?.RealName)
                    ? a.EffectiveName : a.UserInfo!.RealName,
                ["pt_photourl"] = a.UserInfo?.PhotoUrl ?? "",
                // 登录来源角标：显示 ClassIsland Logo（http URL，由本网关伺服）
                ["pt_sysicourl"] = sysIcoUrls,
                ["pt_session"] = a.Id,
                ["pt_timestamp"] = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                ["pt_type"] = "0"
            }).ToList();

            // 资料自愈：账号缺昵称/头像（旧版本数据）时，后台补一次登录拉取资料，
            // 下一次轮询（2 秒后）白板界面即显示头像。不阻塞本次应答。
            EnsureProfilesAsync(accounts);

            await WriteJsonAsync(stream, 200,
                new { message = "success", statusCode = "200", data = items });
            return;
        }

        // GET /getData/SSOLOGIN/{userId} → 登录并返回令牌
        if (method == "GET" && path.StartsWith("/getData/SSOLOGIN/", StringComparison.OrdinalIgnoreCase))
        {
            LastSsoRequestAtUtc = DateTime.UtcNow;
            var userId = path["/getData/SSOLOGIN/".Length..];
            // 注意：从同一份列表中取账号并保存同一份列表，避免加载新副本覆盖掉本次修改
            var accounts = _store.LoadAccounts();
            var account = accounts
                .FirstOrDefault(a => a.Id == userId || a.Username == userId);
            if (account == null)
            {
                await WriteJsonAsync(stream, 404,
                    new { message = "user_not_found", statusCode = "404" });
                return;
            }

            var settings = _store.Settings;
            var password = SecureStore.Unprotect(account.EncryptedPassword);
            if (string.IsNullOrEmpty(password))
            {
                account.LastError = "本地密码无法解密（可能更换了 Windows 账户），请在插件设置中重新录入密码";
                account.LastFailureKind = SeewoFailureKind.Credential;
                await WriteJsonAsync(stream, 401, new
                {
                    message = "credential_invalid",
                    statusCode = "401",
                    detail = account.LastError
                });
                return;
            }

            // 会话复用：有效会话直接返回 token，避免每次轮询都打登录接口
            if (_auth.HasSession(account.Id))
            {
                var session = _auth.GetSession(account.Id)!;
                account.CountRequest();
                _store.SaveAccounts(accounts);
                await WriteLoginSuccessAsync(stream, session.Token);
                return;
            }

            // 失败冷却：60s 内不重试（防风控升级）
            if (_failures.TryGetValue(account.Id, out var lastFailure) &&
                (DateTime.UtcNow - lastFailure.AtUtc).TotalSeconds < settings.FailureCooldownSeconds)
            {
                await WriteJsonAsync(stream, 401, new
                {
                    message = "login_failed",
                    statusCode = "401",
                    detail = lastFailure.Message
                });
                return;
            }

            var result = await _auth.LoginAsync(account, password);
            if (!result.Success)
            {
                _failures[account.Id] = (DateTime.UtcNow, result.ErrorMessage ?? "登录失败");
                await WriteJsonAsync(stream, 401, new
                {
                    message = "login_failed",
                    statusCode = "401",
                    detail = result.ErrorMessage
                });
                return;
            }

            _failures.TryRemove(account.Id, out _);
            account.UserInfo = result.UserInfo ?? account.UserInfo;
            account.CountRequest();
            _store.SaveAccounts(accounts);
            await WriteLoginSuccessAsync(stream, result.Token);
            return;
        }

        // GET /getData/SSOLOGOUT
        if (method == "GET" && path.Equals("/getData/SSOLOGOUT", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonAsync(stream, 200, new { message = "success", statusCode = "200" });
            return;
        }

        // GET /sysicon*.png → ClassIsland Logo（登录来源角标）
        // 白板端角标绑定 SysIcoUrl[1]（24px 档）；16/24 两个尺寸均伺服同一张 Logo
        if (method == "GET" && (path.Equals("/sysicon.png", StringComparison.OrdinalIgnoreCase) ||
                                path.Equals("/sysicon16.png", StringComparison.OrdinalIgnoreCase) ||
                                path.Equals("/sysicon24.png", StringComparison.OrdinalIgnoreCase) ||
                                path.Equals("/sysico.png", StringComparison.OrdinalIgnoreCase)))
        {
            var icon = GetSysIconBytes();
            if (icon.Length == 0)
            {
                await WriteJsonAsync(stream, 404, new { message = "not_found", statusCode = "404" });
                return;
            }
            await WriteBinaryAsync(stream, 200, "image/png", icon);
            return;
        }

        // POST /deleteData → 白板端删除快捷账号，应答成功即可
        if (method == "POST" && path.Equals("/deleteData", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonAsync(stream, 200, new { message = "success", statusCode = "200" });
            return;
        }

        // POST /savedata → 希沃保存数据，直接应答成功
        if (method == "POST" && path.Equals("/savedata", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonAsync(stream, 200, new { message = "success", statusCode = "200" });
            return;
        }

        await WriteJsonAsync(stream, 404, new { message = "not_found", statusCode = "404" });
    }

    // ── 资料自愈 ─────────────────────────────────────────────

    private static byte[]? _sysIconBytes;

    /// <summary>加载随插件分发的 ClassIsland Logo（伺服给白板登录页作来源角标）。</summary>
    private static byte[] GetSysIconBytes()
    {
        if (_sysIconBytes != null) return _sysIconBytes;
        foreach (var rel in new[] { "Assets/logo.png", "logo.png" })
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, rel);
                if (File.Exists(path))
                {
                    _sysIconBytes = File.ReadAllBytes(path);
                    break;
                }
            }
            catch { }
        }
        _sysIconBytes ??= Array.Empty<byte>();
        return _sysIconBytes;
    }

    /// <summary>正在补拉资料的账号集合（防并发重复登录）。</summary>
    private readonly HashSet<string> _profileRefreshing = new();

    /// <summary>
    /// 对缺少用户资料的账号在后台补一次登录以拉取昵称/头像。
    /// 触发条件：已录入密码 + 无有效会话 + 不在失败冷却期；每账号同时只跑一个。
    /// </summary>
    private void EnsureProfilesAsync(List<SeewoAccount> accounts)
    {
        foreach (var account in accounts)
        {
            if (string.IsNullOrEmpty(account.EncryptedPassword)) continue;
            if (!string.IsNullOrEmpty(account.UserInfo?.PhotoUrl)) continue;
            if (_auth.HasSession(account.Id)) continue;
            if (_failures.ContainsKey(account.Id)) continue;
            if (!_profileRefreshing.Add(account.Id)) continue;

            var list = accounts;
            _ = Task.Run(async () =>
            {
                try
                {
                    var password = SecureStore.Unprotect(account.EncryptedPassword);
                    if (string.IsNullOrEmpty(password)) return;
                    var result = await _auth.LoginAsync(account, password);
                    _log?.Invoke(result.Success
                        ? $"已自动补齐账号资料：{account.EffectiveName}"
                        : $"自动补齐资料失败（{account.EffectiveName}）：{result.ErrorMessage}");
                }
                catch
                {
                    // 静默：失败状态已由 LoginAsync 记录，冷却期内不会重复尝试
                }
                finally
                {
                    _profileRefreshing.Remove(account.Id);
                    try { _store.SaveAccounts(list); } catch { }
                }
            });
        }
    }

    // ── 响应输出 ─────────────────────────────────────────────

    private static async Task WriteLoginSuccessAsync(NetworkStream stream, string token)
    {
        var json = JsonSerializer.Serialize(new
        {
            message = "success",
            statusCode = "200",
            data = new { pt_token = token }
        });
        var body = Encoding.UTF8.GetBytes(json);
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 200 OK\r\n");
        sb.Append("Content-Type: application/json; charset=utf-8\r\n");
        sb.Append($"Content-Length: {body.Length}\r\n");
        sb.Append("Connection: close\r\n");
        sb.Append($"Set-Cookie: pt_token={token}; Path=/; HttpOnly; SameSite=Lax\r\n");
        sb.Append($"X-Auth-Token: {token}\r\n");
        sb.Append("\r\n");
        var head = Encoding.Latin1.GetBytes(sb.ToString());
        await stream.WriteAsync(head);
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    private static async Task WriteJsonAsync(NetworkStream stream, int status, object data)
    {
        var json = JsonSerializer.Serialize(data);
        var body = Encoding.UTF8.GetBytes(json);
        var statusText = status switch
        {
            200 => "OK",
            401 => "Unauthorized",
            403 => "Forbidden",
            404 => "Not Found",
            429 => "Too Many Requests",
            _ => "Error"
        };
        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {status} {statusText}\r\n");
        sb.Append("Content-Type: application/json; charset=utf-8\r\n");
        sb.Append($"Content-Length: {body.Length}\r\n");
        sb.Append("Connection: close\r\n");
        sb.Append("Access-Control-Allow-Origin: *\r\n");
        sb.Append("Access-Control-Allow-Methods: GET, POST, DELETE, OPTIONS\r\n");
        sb.Append("Access-Control-Allow-Headers: Content-Type, Authorization\r\n");
        sb.Append("\r\n");
        var head = Encoding.Latin1.GetBytes(sb.ToString());
        await stream.WriteAsync(head);
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    private static async Task WriteBinaryAsync(NetworkStream stream, int status, string contentType, byte[] body)
    {
        var statusText = status == 200 ? "OK" : "Error";
        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {status} {statusText}\r\n");
        sb.Append($"Content-Type: {contentType}\r\n");
        sb.Append($"Content-Length: {body.Length}\r\n");
        sb.Append("Connection: close\r\n");
        sb.Append("Cache-Control: max-age=86400\r\n");
        sb.Append("Access-Control-Allow-Origin: *\r\n");
        sb.Append("\r\n");
        var head = Encoding.Latin1.GetBytes(sb.ToString());
        await stream.WriteAsync(head);
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    private static int FindHeaderEnd(MemoryStream ms)
    {
        var buf = ms.GetBuffer();
        var len = (int)ms.Length;
        for (var i = 0; i + 3 < len; i++)
        {
            if (buf[i] == 13 && buf[i + 1] == 10 && buf[i + 2] == 13 && buf[i + 3] == 10)
                return i;
        }
        return -1;
    }
}
