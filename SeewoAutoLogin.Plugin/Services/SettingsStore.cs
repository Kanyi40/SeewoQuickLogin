using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SeewoAutoLogin.Plugin.Models;

namespace SeewoAutoLogin.Plugin.Services;

/// <summary>
/// 设置与账号数据持久化。全部存放在插件专属目录（PluginConfigFolder）下，
/// 原子写入并保留 .bak。
/// </summary>
public class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public string Folder { get; }
    private string SettingsPath => Path.Combine(Folder, "settings.json");
    private string AccountsPath => Path.Combine(Folder, "accounts.json");

    /// <summary>诊断日志回调（读写异常时上报，避免静默失败）。</summary>
    public Action<string>? Log { get; set; }

    /// <summary>串行化所有文件读写——网关请求线程、设置页与后台任务会并发保存。</summary>
    private static readonly object FileLock = new();

    public SettingsStore(string pluginConfigFolder)
    {
        Folder = pluginConfigFolder;
        Directory.CreateDirectory(Folder);
    }

    // ── 全局设置 ──────────────────────────────────────────────

    public PluginSettings Settings { get; private set; } = new();

    public void LoadSettings()
    {
        Settings = Load<PluginSettings>(SettingsPath) ?? new PluginSettings();
    }

    public void SaveSettings() => Save(SettingsPath, Settings);

    // ── 账号 ──────────────────────────────────────────────────

    public List<SeewoAccount> LoadAccounts() =>
        Load<List<SeewoAccount>>(AccountsPath) ?? new List<SeewoAccount>();

    public void SaveAccounts(List<SeewoAccount> accounts) => Save(AccountsPath, accounts);

    // ── 底层读写（原子写 + .bak）──────────────────────────────

    private T? Load<T>(string path) where T : class
    {
        lock (FileLock)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<T>(json, JsonOptions);
            }
            catch (Exception ex)
            {
                Log?.Invoke($"{Path.GetFileName(path)} 读取失败，尝试 .bak：{ex.GetType().Name}");
                // 主文件损坏时尝试 .bak
                try
                {
                    var bak = path + ".bak";
                    if (!File.Exists(bak)) return null;
                    var result = JsonSerializer.Deserialize<T>(File.ReadAllText(bak), JsonOptions);
                    if (result != null)
                        Log?.Invoke($"{Path.GetFileName(path)} 已从 .bak 恢复（主文件损坏）");
                    return result;
                }
                catch
                {
                    Log?.Invoke($"{Path.GetFileName(path)} 与 .bak 均无法解析，数据将按空处理");
                    return null;
                }
            }
        }
    }

    private void Save<T>(string path, T data)
    {
        lock (FileLock)
        {
            var tmp = path + ".tmp";
            var json = JsonSerializer.Serialize(data, JsonOptions);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.WriteAllText(tmp, json);
                    if (File.Exists(path))
                        File.Copy(path, path + ".bak", overwrite: true);
                    File.Move(tmp, path, overwrite: true);
                    return;
                }
                catch (Exception ex) when (attempt < 3 && (ex is IOException or UnauthorizedAccessException))
                {
                    // 杀毒软件/索引服务短暂占用文件：退避重试
                    Thread.Sleep(60 * (attempt + 1));
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"写入 {Path.GetFileName(path)} 失败：{ex.GetType().Name}: {ex.Message}");
                    return;
                }
            }
        }
    }
}
