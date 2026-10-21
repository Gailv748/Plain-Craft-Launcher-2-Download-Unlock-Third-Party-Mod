# Plain Craft Launcher 2 解除下载限制版（第三方修改）

> **非官方版本声明**
>
> 本项目是第三方基于 [Plain Craft Launcher 2 (PCL2)](https://github.com/Meloong-Git/PCL) **2.99.0.0 官方正式版**独立进行二次创作的产物，**与原作者龙腾猫跃及 PCL 官方没有任何关系**，也不代表官方立场。
>
> 本项目仅用于**技术研究与学习交流**，请勿用于商业用途、倒卖或任何违反当地法律法规的行为。使用本项目产生的全部后果由使用者自行承担。

## 原作者与赞助

| 项目 | 链接 |
| --- | --- |
| 原作者 | 龙腾猫跃（LTCat） |
| PCL 官方网站 | https://meloong.com/ |
| 赞助原作者（爱发电） | https://meloong.com/afd/a/LTCat |
| PCL 原仓库 | https://github.com/Meloong-Git/PCL |
| PCL 帮助文档 | https://github.com/LTCatt/PCL2Help |

如果你觉得 PCL 好用，请通过上面的赞助链接支持原作者。

## 这个版本做了什么

PCL2 `2.99.0.0` 正式版出于合规原因，把版本下载入口替换成了一个直接抛出异常的占位加载器：

```vb
PageLoaderInit(..., New LoaderTask(Of String, DlClientListResult)("关闭下载", Sub()
    Throw New Exception("为遵守相关法律法规，PCL 暂时不再提供 MC 下载功能，非常抱歉 qwq……")
End Sub), ...)
```

本项目使用 [dnlib](https://github.com/0xd4d/dnlib) 在 IL 层做**两处**最小修改，不改界面、不新增功能、不引入任何新代码：

1. `PCL.PageDownloadInstall.LoaderInit`
   把上述占位加载器替换为程序内本来就存在的官方加载器 `ModDownload.DlClientListLoader`，恢复版本列表加载。
2. `PCL.PageDownloadModify.LoadInstanceFinished`
   把 `CardMinecraft.Visibility` 从 `Collapsed` 恢复为 `Visible`，让版本修改页正常显示原版组件卡片。

补丁器源码完全公开：[`patcher/Program.cs`](patcher/Program.cs)。它不做字符串注入、不下载任何远程代码、不修改其他方法。

## 下载与使用

1. 前往本仓库的 **Releases** 页面，下载 `Plain-Craft-Launcher-2-Download-Unlock-v2.99.0.0.exe`。
2. **先备份**你原来的 `Plain Craft Launcher 2.exe`。
3. 用下载的文件替换原文件（或单独放在一个目录运行）。
4. 运行需要 Windows 与 .NET Framework 4.8。

> 仅支持 PCL2 **2.99.0.0**。其他版本的方法结构可能不同，补丁器会直接报错而不会写出损坏文件。

### 文件校验

| 文件 | 大小 | MD5 | SHA256 |
| --- | --- | --- | --- |
| 修改后（Release 资产） | 5,972,480 字节 | `b4478b66dde895b07c4ccc9722293bc3` | `e400c46dd2a88e7ccf7b142d38f94691467cb9127bc7c6ecbf83d9ee35a611bb` |
| 原始 2.99.0.0 正式版 | 5,980,160 字节 | `d6a946a007f8e84a8bfb0e239868f91a` | `6b6e1260cb5a1d6a303855f1c60cab3d0bc1aebba7e40a964180188b271e7386` |

请只从本仓库 Releases 下载，并核对 SHA256。

## 自行构建补丁

需要 .NET 8 SDK：

```bash
cd patcher
dotnet build -c Release
dotnet run -c Release -- "原始/Plain Craft Launcher 2.exe" "输出/Plain Craft Launcher 2.exe"
```

依赖：`dnlib 4.4.0`。

## 安全性说明

- 修改后的文件**未签名**，部分杀毒软件可能因“未知/被修改”给出启发式告警，这属于信誉问题，不等于检出病毒；如出现具体报毒名，欢迎提 Issue 附上名称。
- 补丁只改变上述两个方法的 IL 指令，未新增程序集引用、未新增资源、未添加网络请求。
- 你可以用 dnSpy / ILSpy 对比原始文件与修改后文件，差异应当只有上面两处。
- 本仓库不包含 PCL2 的完整源代码；PCL2 源代码请前往[原仓库](https://github.com/Meloong-Git/PCL)获取。

## 许可

本项目遵循 PCL 的《分发有限许可》与《存储库合理使用指南》，全文见 [`LICENCE`](LICENCE)。本项目名称以 `Plain Craft Launcher` 开头并带有“第三方修改”标识，属于第三方二次创作，不代表官方。

---

## English summary

An **unofficial, third-party modification** of Plain Craft Launcher 2 (PCL2) `2.99.0.0` that restores the built-in Minecraft version download feature. It applies exactly two IL patches with dnlib — replacing a stub loader that throws an exception with the launcher's existing `ModDownload.DlClientListLoader`, and restoring the `CardMinecraft` card's visibility. No new code, resources, or network endpoints are introduced.

Not affiliated with the original author (龙腾猫跃 / LTCat) or the PCL project. For research and study only. Download the patched executable from the Releases page and verify its SHA256.
