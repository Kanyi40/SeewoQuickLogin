using System.IO;

namespace SeewoAutoLogin.Plugin.Services;

/// <summary>
/// hosts 映射检测（local.id.seewo.com → 127.0.0.1）。
/// 按用户要求：本插件【不自动写入】系统 hosts 文件，仅在状态页提示用户手动处理，
/// 并提供"复制映射行 / 打开 hosts 所在文件夹"两个辅助动作。
/// </summary>
public class HostsFileService
{
    public const string HostName = "local.id.seewo.com";
    public const string MappingLine = "127.0.0.1 " + HostName;

    public static string HostsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts");

    public string HostsFolder => Path.GetDirectoryName(HostsPath) ?? "C:\\";

    /// <summary>hosts 中是否存在有效的 local.id.seewo.com 映射。</summary>
    public bool HasMapping()
    {
        try
        {
            if (!File.Exists(HostsPath)) return false;
            foreach (var raw in File.ReadLines(HostsPath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                // 去掉行内注释后再判断
                var hash = line.IndexOf('#');
                if (hash >= 0) line = line[..hash].Trim();
                var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                // 同时接受 IPv4 / IPv6 回环地址（部分环境优先解析 ::1）
                if (parts.Length >= 2 &&
                    (parts[0].Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                     parts[0].Equals("::1", StringComparison.OrdinalIgnoreCase)) &&
                    parts[1].Equals(HostName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
            // 无读取权限等情况按"未映射"处理
        }
        return false;
    }

    /// <summary>
    /// 补写 hosts 映射（供 AutoWriteHosts 使用）。已有映射时直接返回成功。
    /// 需要管理员权限；写入前备份为 hosts.seewo.bak，失败时返回 false 并给出原因。
    /// </summary>
    public bool TryEnsureMapping(out string message)
    {
        if (HasMapping())
        {
            message = "hosts 映射已存在";
            return true;
        }
        try
        {
            var content = File.Exists(HostsPath) ? File.ReadAllText(HostsPath) : "";
            // 备份一次（保留最近一次原始 hosts）
            try
            {
                if (File.Exists(HostsPath))
                    File.Copy(HostsPath, HostsPath + ".seewo.bak", overwrite: true);
            }
            catch
            {
                // 备份失败不阻断写入
            }
            var append = (content.Length > 0 && !content.EndsWith('\n') ? "\n" : "")
                         + "# Added by ClassIsland SeewoQuickLogin\n"
                         + MappingLine + "\n";
            File.AppendAllText(HostsPath, append);
            message = $"已写入 hosts 映射：{MappingLine}";
            return true;
        }
        catch (Exception ex)
        {
            message = $"写入 hosts 失败（通常是没有管理员权限或文件被占用）：{ex.GetType().Name}";
            return false;
        }
    }
}
