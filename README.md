# Plain Craft Launcher 2 解除下载限制版（第三方修改）

> 🌐 **项目官网**：<https://newpcl2.netlify.app>

> **非官方版本声明**
>
> 本项目是第三方基于 [Plain Craft Launcher 2 (PCL2)](https://github.com/Meloong-Git/PCL) **2.99.0.0 官方正式版**独立进行二次创作的产物，**与原作者龙腾猫跃及 PCL 官方没有任何关系**，也不代表官方立场。
>
> 本项目仅用于**技术研究与学习交流**，请勿用于商业用途、倒卖或任何违反当地法律法规的行为。使用本项目产生的全部后果由使用者自行承担。

## 原作者与赞助

| 项目         | 链接                                   |
| ---------- | ------------------------------------ |
| 原作者        | 龙腾猫跃（LTCat）                          |
| PCL 官方网站   | [pcl.](https://meloong.com/)         |
| 赞助原作者（爱发电） | <https://meloong.com/afd/a/LTCat>    |
| PCL 原仓库    | <https://github.com/Meloong-Git/PCL> |
| PCL 帮助文档   | <https://github.com/LTCatt/PCL2Help> |

如果你觉得 PCL 好用，请通过上面的赞助链接支持原作者。

## 这个版本做了什么

PCL2 `2.99.0.0` 正式版出于合规原因，通过 `ModSecret.RestrictMcDownload = True` 关闭了多项下载与整合包功能。本项目使用 [dnlib](https://github.com/0xd4d/dnlib) 在 IL 层做**五处**最小修改：其中四处恢复以下五项功能，另一处在每次启动时强制显示第三方修改公告。不新增界面、不引入任何新代码、不下载任何远程代码：

| # | 功能 | 修改点 | 说明 |
| - | ---- | ------ | ---- |
| 1 | 恢复版本清单加载 | `PCL.PageDownloadInstall.LoaderInit` | 把抛异常的占位加载器替换回官方 `ModDownload.DlClientListLoader` |
| 2 | 恢复版本卡片 | `PCL.PageDownloadModify.LoadInstanceFinished` | 把 `CardMinecraft.Visibility` 从 `Collapsed` 恢复为 `Visible` |
| 3 | 恢复整合包安装安静 | `PCL.FormMain` 拖放处理 | 恢复后整合包安装异常由原有 catch 安静处理（无需额外补丁） |
| 4 | 恢复手动安装整合包入口 | `PCL.ModModpack.ModpackInstall()` | 恢复文件选择框 → 调用 `ModpackInstall(File)` |
| 5 | 恢复整合包分派 | `PCL.ModModpack.ModpackInstall(File,…)` | 恢复 `Select Case PackType` 分派到 `InstallPackCurseForge/HMCL/MMC/MCBBS/Modrinth/LauncherPack/Compress` |
| 6 | 启动强制公告 | `PCL.FormMain` 加载入口 + 初始化线程（双注入点） | 每次启动弹出「第三方修改版公告」，写明仅用于学习交流、不得以任何方式牟利，必须点击「我已知悉」才能继续 |

> 公告逻辑编译为 `ModMain.ShowThirdPartyNotice()`，并在启动流程的**两个独立位置**各插入一次调用；即使有人改掉其中一处，另一处仍会弹出。是否已弹过只记录在**进程内静态字段**中，不写配置文件、不读注册表、不依赖网络，删除任何文件或修改任何设置都不会让它消失。
>
> 需要说明的技术边界：任何在用户电脑上运行的程序都可以被逆向重写，理论上仍可通过反编译修改二进制来移除公告——这一点对任何客户端软件都无法避免。本补丁能保证的是：不改二进制就无法绕过。

补丁器源码完全公开：[`patcher/Program.cs`](patcher/Program.cs)。它不下载任何远程代码、不修改其他方法。所有被调用的方法（`DlClientListLoader`、`InstallPack*`、`ModMain.MyMsgBox` 等）均为原版程序中已存在的代码。

## 下载与使用

1. 通过以下任一渠道下载 `Plain-Craft-Launcher-2-Download-Unlock-v2.99.0.0-signed.exe`：

   | 渠道 | 链接 | 说明 |
   | --- | --- | --- |
   | **蓝奏云（推荐）** | <https://wwayr.lanzout.com/iLtnO4czip3i> | 国内访问快，无需登录；提取码 `2t1q` |
   | 项目官网 | <https://newpcl2.netlify.app> | 同目录直链 + GitHub 镜像 |
   | GitHub Releases | [Releases 页面](https://github.com/Gailv748/Plain-Craft-Launcher-2-Download-Unlock-Third-Party-Mod/releases) | 需能访问 GitHub |

2. **先备份**你原来的 `Plain Craft Launcher 2.exe`。
3. 用下载的文件替换原文件（或单独放在一个目录运行）。
4. 运行需要 **Windows 7 SP1 / 8 / 8.1 / 10 / 11**（64 位）与 .NET Framework 4.8。启动公告调用的是 PCL 自带的 WPF 消息框，不含任何与系统版本相关的代码，因此上述系统表现一致。

> 仅支持 PCL2 **2.99.0.0**。其他版本的方法结构可能不同，补丁器会直接报错而不会写出损坏文件。

### 数字签名说明（重要）

Release 中的 exe 已使用**自签名**代码签名证书进行 Authenticode 签名，并附加了 DigiCert 时间戳。签名信息如下：

| 字段 | 值 |
| --- | --- |
| 签名者（Publisher） | `PCL2 Third-Party Mod (Non-Official)` |
| 组织 | `Third-Party Modification` |
| 组织单位 | `Learning-Only No-Profit` |
| 程序名称 | `PCL2解除下载限制版-第三方修改版｜非官方｜仅供学习研究使用｜不进行任何商业获利` |
| 更多信息 | <https://newpcl2.netlify.app> |
| 时间戳 | DigiCert Trusted G4 TimeStamping |
| 有效期 | 2026-10-21 至 2036-10-18 |

> **关于「未知发布者」提示**：由于这是**自签名证书**（非 CA 机构签发），Windows 默认不受信任，可能仍提示「未知发布者」。这并不代表文件有问题——右键文件 → 属性 → 数字签名 即可看到完整签名信息，包括上面的「非官方 / 仅供学习研究 / 不得牟利」声明。如需消除提示，可将随附的自签名根证书导入「受信任的根证书颁发机构」（仅供个人学习研究使用）。

### 文件校验

| 文件              | 大小           | MD5                                | SHA256                                                             |
| --------------- | ------------ | ---------------------------------- | ------------------------------------------------------------------ |
| 签名版（Release 资产） | 5,981,496 字节 | `—` | `47b38e5762e5e7ec89010251c93ff87834654834f97fb73677850a889f946d36` |
| 原始 2.99.0.0 正式版 | 5,980,160 字节 | `d6a946a007f8e84a8bfb0e239868f91a` | `6b6e1260cb5a1d6a303855f1c60cab3d0bc1aebba7e40a964180188b271e7386` |

请只从上述官方渠道（蓝奏云 / 项目官网 / 本仓库 Releases）下载，并核对 SHA256。

## 自行构建补丁

需要 .NET 8 SDK：

```bash
cd patcher
dotnet build -c Release
dotnet run -c Release -- "原始/Plain Craft Launcher 2.exe" "输出/Plain Craft Launcher 2.exe"
```

依赖：`dnlib 4.4.0`。

## 安全性说明

- 自行构建且**未签名**的文件，或本项目的自签名文件，部分杀毒软件可能因“未知/被修改”给出启发式告警，这属于信誉问题，不等于检出病毒；如出现具体报毒名，欢迎提 Issue 附上名称。
- 补丁只改变上述五个方法的 IL 指令，未新增程序集引用、未新增资源、未添加网络请求。
- 你可以用 dnSpy / ILSpy 对比原始文件与修改后文件，差异应当只有上述五处。
- 本仓库不包含 PCL2 的完整源代码；PCL2 源代码请前往[原仓库](https://github.com/Meloong-Git/PCL)获取。

## 许可

本项目遵循 PCL 的《分发有限许可》与《存储库合理使用指南》，全文见 [`LICENCE`](LICENCE)。本项目名称以 `Plain Craft Launcher` 开头并带有“第三方修改”标识，属于第三方二次创作，不代表官方。

***

## English summary

An **unofficial, third-party modification** of Plain Craft Launcher 2 (PCL2) `2.99.0.0` that restores the built-in Minecraft version download and modpack installation features disabled by the official build. It applies five minimal IL patches with dnlib: (1) restore the version list loader, (2) restore the version card visibility, (3) restore the manual modpack install entry, (4) restore the modpack type dispatcher, and (5) show a mandatory third-party/learning-only/no-profit notice on every startup. All called methods (`DlClientListLoader`, `InstallPack*`, `ModMain.MyMsgBox`, etc.) already exist in the original binary — no new code, resources, or network endpoints are introduced. Supported on 64-bit Windows 7 SP1 / 8 / 8.1 / 10 / 11 with .NET Framework 4.8.

Not affiliated with the original author (龙腾猫跃 / LTCat) or the PCL project. For research and study only. Download the patched executable from [Lanzou Cloud (recommended, access code `2t1q`)](https://wwayr.lanzout.com/iLtnO4czip3i), the project website, or the Releases page, and verify its SHA256.
