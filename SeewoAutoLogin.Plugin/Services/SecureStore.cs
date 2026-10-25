using System.Security.Cryptography;
using System.Text;

namespace SeewoAutoLogin.Plugin.Services;

/// <summary>
/// DPAPI 加密落盘。密码 / 令牌以当前 Windows 用户为作用域加密，
/// 换机器或换用户都无法解密（对应"需要重新录入密码"场景）。
/// </summary>
public static class SecureStore
{
    public static string? Protect(string plain)
    {
        try
        {
            var bytes = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(bytes);
        }
        catch
        {
            return null;
        }
    }

    public static string? Unprotect(string? cipherBase64)
    {
        if (string.IsNullOrWhiteSpace(cipherBase64)) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(
                Convert.FromBase64String(cipherBase64), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }
}
