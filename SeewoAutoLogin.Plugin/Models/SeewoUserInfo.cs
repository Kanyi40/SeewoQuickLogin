namespace SeewoAutoLogin.Plugin.Models;

/// <summary>希沃用户资料（来自 /api/v2/user/info）。</summary>
public class SeewoUserInfo
{
    public string NickName { get; set; } = "";
    public string RealName { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Phone { get; set; } = "";
    public string PhotoUrl { get; set; } = "";
    public string UnitName { get; set; } = "";
    public string StageName { get; set; } = "";
    public string SubjectName { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string Uid { get; set; } = "";
}
