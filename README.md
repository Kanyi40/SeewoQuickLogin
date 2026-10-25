# 希沃快捷登录（SeewoQuickLogin · ClassIsland 插件）

在 [ClassIsland](https://github.com/ClassIsland/ClassIsland) 2.x 中管理希沃账号，通过本地 SSO 网关为希沃白板提供快捷登录。

## 功能

- **账号管理**：保存希沃账号 + 密码（DPAPI 加密落盘），支持编辑、测试登录（测试通过才保存）
- **本地 SSO 网关**（`127.0.0.1:24300`）：应答希沃白板登录界面的账号列表与登录令牌
  （`/getData/SSOLOGIN`、`/getData/SSOLOGIN/{userId}`、`/getData/SSOLOGOUT`、`/savedata` 等）
- **自动获取资料**：登录后自动拉取希沃昵称与头像，快捷登录列表直接显示
- **登录失败分类与 60 秒冷却**：区分「密码失效」与「网络/限流临时故障」，防风控误报
- **运行状态自检**：网关状态、hosts 映射、希沃最近请求时间，均可在设置页查看
- 设置页集成于 ClassIsland 应用设置

## 安装

1. 在 ClassIsland【应用设置】→【插件】中导入 `SeewoAutoLogin.Plugin.cipx` 插件包（或从插件市场安装）。
2. 以管理员权限在 `hosts` 文件中添加一条映射（本插件不会自动修改系统文件）：

   ```
   127.0.0.1 local.id.seewo.com
   ```

   缺少该映射时，设置页会显示处理提示（复制映射行 / 打开 hosts 所在文件夹）。
3. ClassIsland 建议以管理员身份运行：希沃的 EasiAgent 看门狗会结束占用 24300 端口的非管理员进程，管理员运行的 ClassIsland 才能稳定持有该端口。

## 构建

需要 .NET 8 SDK：

```shell
dotnet publish -p:CreateCipx=true
```

插件包与 MD5 校验信息输出在项目目录的 `cipx` 文件夹中。

## 来源与致谢

本插件的希沃本地 SSO 网关协议在阅读开源项目
[SeewoAutoLogin](https://github.com/Pro-Qin/SeewoAutoLogin)（GPL-3.0，上游
[CJKmkp/SeewoAutoLogin](https://github.com/CJKmkp/SeewoAutoLogin)）源码的基础上完成，在此向原作者表示感谢。
本插件在 ClassIsland 插件体系（Avalonia）上重新实现，并补充了账号管理、设置页集成、头像昵称获取等 ClassIsland 生态功能。

## 许可证

本项目以 [GPL-3.0](./LICENSE) 许可证发布。
