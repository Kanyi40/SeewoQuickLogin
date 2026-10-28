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

**班级大屏 / 机房环境**：如果 C 盘开启了冰点还原或还原卡，hosts 会在每次重启后被还原，
请在插件设置页开启「自动写入 hosts 映射」，由插件在每次启动时自动补写（需管理员运行）。
另外建议开启「端口被希沃白板本体占用时也强制接管」仅在确认无人使用白板时使用。

**建议以管理员身份运行 ClassIsland**：接管 24300 端口（结束希沃 EasiAgent）与写入 hosts 都需要管理员权限；
设置页「运行权限」一栏会显示当前状态。设置页还提供「一键修复」按钮，可一次完成开网关、补 hosts、检查端口。

**已知环境问题与自动处理**：

| 情况 | 插件行为 |
|------|---------|
| 希沃 EasiAgent 占用 24300 | 只结束受信任路径（`\Seewo\EasiAgent\EasiAgent.exe`）的进程，紧逼重试抢占端口；抢不到时回退备用端口并在界面上标注警告 |
| 白板本体 EasiNote 占用 24300 | 默认拒绝结束以保护未保存的板书，可在设置页开启强制接管 |
| 系统残留失效代理（如 Clash 关闭后仍指向 127.0.0.1:10809） | 登录请求失败时自动改直连重试，并在 30 分钟内优先直连 |
| 网关监听异常退出 | 每 10 秒自检端口连通性，异常时自动重启监听 |
| 修改 hosts 后白板无反应 | 需要重启希沃白板让其重新解析域名（设置页提示） |

**已实测协议一致的希沃白板版本**：5.2.4.8772、5.2.4.9838、5.2.4.11451。

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
