# AGENTS.md

> 给 AI 助手（以及未来的自己）的**行为规则**。开工前先读本文件，再读 `doc/project/handoff.md`。
> 项目知识在 `doc/`（入口 `doc/README.md`）；本文件只写"必须遵守的约束"，不重复知识。

VigilWatt：Windows 无线设备电量监控（WinUI 3 / .NET 8，个人自用）。不做游戏内显示、HTTP API、账号体系。

## 1. git 红线（最硬，无例外）

- **未经用户明确确认，不得执行任何 git 写操作**：`commit` / `push` / `merge` / `rebase` / `reset` / `checkout` / `branch` / `tag` / `stash` / 改 remote。
- 只读命令可自由执行：`status` / `log` / `diff` / `show` / `blame` / `ls-files`。
- 要提交时：先说清"改了什么、打算怎么提交"，等用户同意再动。
- 确认后：提交信息**从简**；一轮改动（哪怕跨几个主题）**合并成一个提交**，不拆分。
- 中文提交信息先写文件，再 `git commit -F <文件>`（避免 Git Bash 传中文乱码）。

## 2. 代码注释

- 严格按 `doc/dev/comment-style.md`。注释只回答"**为什么**"，且只在不写就会踩坑时才写。
- "是什么 / 曾经怎样 / 怎么验证的 / 帧长什么样" 一律进 `doc/`，代码里只留指针。
- 硬配额：**类头 ≤ 5 行、方法/属性 ≤ 2 行、字段 ≤ 1 行**。超了说明内容该进 `doc/`。
- 注释引用的 `doc/xxx.md §y` 必须指向真实存在的小节。

## 3. 动代码前

- 先读 `doc/project/handoff.md`（会话快照）+ 相关专题文档；冲突时以专题文档为准。
- 逐轮历史在 `doc/project/changelog.md`：**本轮做了什么、为什么这么改，收工后追加到那里**，不要把流水账写回 handoff（那里只放当前状态）。
- **UI 改版：先出方案，用户确认后再改代码。**
- 硬件写入类改动：先读 `doc/hardware/safety.md`（零写入原则、写入闸门）。
- 改读数过滤器 / 帧解析 / 历史库后，**必须重跑单测**。

## 4. 构建与测试

- 必须用 **.NET 8 SDK**（机器上只有运行时时：`winget install Microsoft.DotNet.SDK.8`）。
- 构建：`dotnet build VigilWatt.sln -c Debug`
- 运行：从 `src\VigilWatt.App\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\` 启动。
  必须用 **sln** 构建（产物在 `bin\x64\...`），直接构建 csproj 的产物在 `bin\...`，不要混用。
- 测试：`dotnet test src/tests/VigilWatt.Core.Tests`
- 发布：`dotnet publish src/VigilWatt.App -c Release -p:Platform=x64 -o publish`
  发布前确认没有实例在运行——exe 被占用会导致 `MSB3027` 构建失败。

## 5. 不要碰

- 构建/发布产物：`bin/`、`obj/`、`publish/`、`dist/`（均已 gitignore）。
- 助手元数据目录：`.workbuddy/`、`.workbuddy-ai/`、`.zcode/`（已 gitignore，**规则写这里等于没写**）。
- `.vpk-github-token`（GitHub PAT，绝不入库）。
- 仓库根不放临时文件；一次性调试脚本放 `scripts/`，并补 `scripts/README.md` 索引。

## 6. 静默失败的两个坑（必记）

- XAML 里写了不存在的属性（如 `NumberBox` 的 `Min`，应为 `Minimum`）会让 XamlCompiler **静默崩溃**（`MSB3073`，无任何报错输出）。
- 项目目录下不能有多余的 `.xaml`（会被自动 glob 成编译项）。

其余坑见 `README.md` 末尾"最常踩的几条速记"与 `doc/dev/`。

## 7. 沟通

- 中文交流，文档中文；界面只中英文。
