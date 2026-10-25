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
                if (parts.Length >= 2 &&
                    parts[0].Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
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
}
