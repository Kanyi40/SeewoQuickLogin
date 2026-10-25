# 希沃快捷登录（ClassIsland 插件）

在 ClassIsland 中管理希沃账号，通过本地 SSO 网关为希沃白板提供快捷登录。

## 功能

- 希沃账号管理：账号 + 密码（DPAPI 加密落盘）、一键测试登录
- 本地 SSO 网关（127.0.0.1:24300）：为希沃白板登录界面提供账号列表与登录令牌
  （`/getData/SSOLOGIN`、`/getData/SSOLOGIN/{userId}`、`/getData/SSOLOGOUT`、`/savedata`）
- 登录失败分类与 60 秒冷却：区分「密码失效」与「网络/限流临时故障」，防风控误报
- 运行状态自检：网关、hosts 映射、希沃最近请求时间
- 设置页集成于 ClassIsland 应用设置

## 前提

hosts 文件中需要一条映射（需要管理员手动添加，本插件不会自动修改系统文件）：

```
127.0.0.1 local.id.seewo.com
```

缺少该映射时，设置页会显示处理提示（复制映射行 / 打开 hosts 所在文件夹）。

## 构建

需要 .NET 8 SDK：

```shell
dotnet build -c Release
```

输出目录中会生成 `.cipx` 插件包。

## 调试

`Properties/launchSettings.json` 已配置指向本机 ClassIsland 实例（`-epp` 外部插件路径方式）。

## 来源与致谢

本插件的希沃本地 SSO 网关协议（`local.id.seewo.com:24300` 的账号列表、令牌交换等）在阅读开源项目
[SeewoAutoLogin](https://github.com/Pro-Qin/SeewoAutoLogin)（GPL-3.0，上游
[CJKmkp/SeewoAutoLogin](https://github.com/CJKmkp/SeewoAutoLogin)）源码的基础上完成，在此向原作者表示感谢。

本插件在 ClassIsland 插件体系（Avalonia）上重新实现，并补充了账号管理、设置页集成、头像昵称获取等 ClassIsland 生态功能。
因参考了上述 GPL-3.0 项目，本项目同样以 GPL-3.0 许可证开源。

## 许可证

本项目以 [GPL-3.0](https://www.gnu.org/licenses/gpl-3.0.txt) 许可证发布，详见仓库根目录的 `LICENSE` 文件。
