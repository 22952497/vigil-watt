# 构建与环境坑

全部实踩于 2026-10-04~05，特征大多是"静默失败"。机器：Windows 11 x64（26200），.NET 8 SDK 8.0.425（winget 装的，机器原本只有运行时）。

## 1. XamlCompiler 静默崩溃（最阴的坑）

**症状**：`dotnet build` 报 MSB3073（退出码 1），XamlCompiler.exe 崩溃，**没有任何报错内容**——不写 output.json、不进事件日志，错误列表全空。

**根因**：XAML 里写了控件上不存在的属性。实例：`NumberBox` 用了 `Min`/`Max`（正确是 `Minimum`/`Maximum`）。

**排查法**：二分注释各页面 XAML 定位文件；确认文件后逐属性核对。改完 XAML 属性名后 pass1 挂了，优先怀疑属性名。

## 2. 双输出路径，容易跑旧二进制

- 直接 `dotnet build src/.../xx.csproj` → 产物在 `bin\Debug\...`
- `dotnet build VigilWatt.sln`（Platform=x64）→ 产物在 `bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\`

**规则：始终用 sln 构建、从 `bin\x64\...\win-x64\` 启动**。跑错目录会拿着旧 exe 验证半天新代码，浪费一小时还得出错误结论。

## 3. 应用运行时锁 DLL

构建前先 `taskkill //IM VigilWatt.App.exe //F`，否则 MSB3021/3027 文件被占用。

## 4. 发布

```
dotnet publish src/VigilWatt.App -c Release -p:Platform=x64 -o src/publish
```

self-contained（含 Windows App SDK 运行时），约 102MB，无需安装任何依赖。产物落在 `src/publish/`（已 gitignore）。sln 的 GUID 是手写的，VS 能正常打开。

## 4.1 测试

```
dotnet test src/tests/VigilWatt.Core.Tests
```

xUnit 项目（`net8.0`、AnyCPU），用 `<Compile Include>` 直接链接 `src/VigilWatt.Core` 里的真源文件（`BatteryReadingFilter` / `MchoseV1Codec`），不引 WinRT。18 个用例，改这两个文件后必跑。

## 5. NuGet 版本降级链

引入 System.Drawing.Common 时报 NU1605：H.NotifyIcon 的依赖链要求 ≥9.0.1，显式引用 9.0.1 解决（后来 H.NotifyIcon 整个移除了，但 System.Drawing.Common 因托盘画图标仍在用）。

## 6. 输出目录里的 88 个语言文件夹（.mui 语言包）

**症状**：`bin`/`src/publish` 下出现 `af-ZA`、`de-DE`、`fr-FR`… 共 88 个语言目录，各含 2 个 `.mui` 文件（WinUI XAML 框架的多语言 UI 字符串）。

**来源**：`WindowsAppSDKSelfContained=true` 时，`Microsoft.WindowsAppSDK.SelfContained.targets` 的 `AddMicrosoftWindowsAppSDKPayloadFiles` 把 WindowsAppRuntime MSIX 解包并**全量**拷贝，`.mui` 在其中。注意它们不走 .NET 卫星程序集管道，`SatelliteResourceLanguages` 属性管不到。

**解法**（已加在 VigilWatt.App.csproj）：官方排除口 `MicrosoftWindowsAppSDKFilesExcluded`，自定义 Target `TrimWinUiMuiLanguages`（BeforeTargets=AddMicrosoftWindowsAppSDKPayloadFiles）把白名单（en-us/zh-CN/zh-TW）以外的 .mui 塞进去。改白名单时同步改 `MuiLanguagesToKeep` 属性和 `_MuiKeep` 的显式路径。

**两个坑**：
1. `$(MicrosoftWindowsAppSDKMsixContent)` 属性**不带尾反斜杠**，glob 拼接要自己补 `\`（`...MsixContent)\**\*.mui`），漏了会匹配到 0 个文件、排除静默失效——诊断用 `@(_MuiAll->Count())` 消息打印计数；
2. 该拷贝是 `PreserveNewest` 只增不减，加过滤后要**手动清一次旧输出目录**才干净。

## 7. 临时 PowerShell 测试脚本只能用 C# 5

本机 PowerShell 5.1 的 `Add-Type` 不支持：字符串插值 `$"..."`、`out var`/`out uint x` 内联声明、`string?` 可空注解。写 P/Invoke 测试脚本（如 scripts/test-rapou7.ps1）要用老式语法：先声明变量、string.Format/拼接。**更省事的替代：`pip install hidapi` 用 python 直接测 HID**（见 debugging.md）。脚本一律放 `scripts/`（见 scripts/README.md）。

## 8. 安装包（Velopack）

2026-10-06 接入。一条命令出安装包：

```
powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1
```

脚本做的事：taskkill 运行中实例 → `dotnet publish`（Release x64，同 §4）→ 读发布 exe 的 FileVersion 当包版本（截成 3 段，vpk 不收 4 段）→ `vpk pack`。产物在 `dist/`：

- `VigilWattApp-win-Setup.exe`：安装包（**仅当前用户**、无 UAC、装到 `%LocalAppData%\VigilWattApp`，自动建桌面+开始菜单快捷方式、写卸载条目）
- `VigilWattApp-win-Portable.zip`：绿色免安装版（vpk 顺带生成）
- `VigilWattApp-*-full.nupkg` + `RELEASES`：**这组文件就是"更新源"**，整体拷到任意静态目录/网盘/GitHub Releases 即可分发更新；发新版只需 bump csproj 的 `<Version>` 重跑脚本，新旧 nupkg 共存时自动增量

### 关键决策与坑

1. **packId 必须叫 `VigilWattApp`，不能叫 `VigilWatt`**。Velopack 默认装到 `%LocalAppData%\<packId>`，叫 VigilWatt 会和应用数据目录 `%LOCALAPPDATA%\VigilWatt`（settings.json/history.db/logs）撞车，升级时数据被清。脚本里有注释，改 packId 前想清楚。
2. **vpk 不指定 `--runtime` 会默认 x86**（仅警告不报错）。脚本是 x64-only 应用，传了 `--runtime win-x64`；改打包方式时别丢。
3. **同一个版本不能重复 pack**（FTL: release in channel win equal or greater）。重打同版本先删 `dist/`。
4. **PowerShell 5.1 的编码坑**：.ps1 含中文必须存成 **UTF-8 带 BOM**，否则中文注释被当 GBK 读、字符串终止符解析直接报错。改 build-installer.ps1 后确认 BOM 还在（VS Code 右下角选"通过编码保存 → UTF-8 with BOM"）。
5. **未签名**：安装/运行时 SmartScreen 可能弹"未知发布者"，点"仍要运行"即可；要消除需买代码签名证书后给 vpk 加 `--signParams`。
6. **从 bin/publish 直接跑的副本不能自更新**（Velopack `IsInstalled=false`），设置页"检查更新"会明确提示。装了的实例启动时还会**自动应用已下载的更新**（Velopack 默认行为，无需写代码）。

### 应用侧接入（已做，改这些文件时注意）

- `Program.cs`：自定义入口（csproj `DISABLE_XAML_GENERATED_MAIN` 关掉了 XAML 生成的 Main）。`VelopackApp.Build().Run()` 必须最先跑——安装/更新/卸载钩子进程带 `--veloapp-*` 参数被拉起，在这里被消费后直接退出，所以 `App.OnLaunched` 的单实例互斥不会误杀钩子进程，也不用改。
- 卸载清理：`OnBeforeUninstallFastCallback` 里调 `AutostartService.SetEnabled(false)` 删 HKCU Run 自启动残留。
- `Services/UpdateService.cs` + 设置页"软件更新"区：更新源存 settings.json 的 `updateUrl`（本地目录/网盘同步目录/GitHub 仓库 URL 都行），留空 = 不检查。
- 版本号唯一来源：`VigilWatt.App.csproj` 的 `<Version>`，exe FileVersion、设置页显示、安装包版本都从它来。

### 已验证（2026-10-06）

安装（静默）→ 目录/快捷方式/卸载条目正确、数据目录不冲突；自动更新（把 1.0.1 nupkg 丢进 `packages\` 后启动 1.0.0 → 自动升级重启到 1.0.1）；卸载 → 安装目录/快捷方式/卸载条目/HKCU Run 残留全部清掉，用户数据保留。
