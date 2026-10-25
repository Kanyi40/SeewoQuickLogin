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

    private static T? Load<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch
        {
            // 主文件损坏时尝试 .bak
            try
            {
                var bak = path + ".bak";
                if (!File.Exists(bak)) return null;
                return JsonSerializer.Deserialize<T>(File.ReadAllText(bak), JsonOptions);
            }
            catch
            {
                return null;
            }
        }
    }

    private static void Save<T>(string path, T data)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(data, JsonOptions));
        if (File.Exists(path))
            File.Copy(path, path + ".bak", overwrite: true);
        File.Move(tmp, path, overwrite: true);
    }
}
