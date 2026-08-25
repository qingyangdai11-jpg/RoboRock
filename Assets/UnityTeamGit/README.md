# Unity Team Git

第一版 Unity Editor 工具，用于把新建 Unity 工程初始化到团队 NAS Gitea。

## 打开方式

导入后窗口会自动打开，也可以使用：

`Tools > Unity Team Git > Setup`

初始化完成后的日常操作使用：

`Tools > Unity Team Git > Workspace`

浏览并下载 NAS 中的团队插件使用：

`Tools > Unity Team Git > NAS Plugin Library`

## 第一版能力

- 设置 `Visible Meta Files` 和 `Force Text`
- 合并 Unity `.gitignore` 与 `.gitattributes`
- 初始化 Git、Git LFS、Unity Smart Merge
- 配置局域网 `origin` 和 Tailnet 备用远程
- “打开 Gitea 仓库”默认使用局域网地址，并提供独立的 Tailscale 打开按钮
- 分别检查本地 Git 仓库、origin 连接和云端同名仓库
- 本地初始化与首次 Push 分为两个按钮
- 首次提交后，通过 Gitea Push To Create 创建公开仓库
- 已有仓库保护：不重置历史、不覆盖远程、不自动强推
- Setup 面板提供“重新绑定仓库”：二次确认后只覆盖本地 `origin` / `tailnet` 地址，保留工作区、`.git` 和全部 Commit，并记录上一组地址；目标云端仓库不存在时才重新开放“首次 Push”
- 日常同步仅提供 `Pull`、`Push_NoCommit`、`Push_Commit` 三个按钮
- `Push_NoCommit` 自动使用当前时间作为 Commit 说明；`Push_Commit` 弹窗填写说明
- Branch 面板可读取 Gitea Branch、连接已有 Branch，或新建并立即发布 Branch
- Pull 与 Branch 切换要求干净工作区；远端领先时拒绝 Push
- Push 同时校验界面 Branch、Git 当前 Branch 和远端提交哈希，目标固定为 `HEAD:refs/heads/<当前Branch>`
- Branch 切换前缓存插件及根 `.meta`；目标 Branch 未包含插件时会自动恢复，避免工具删除自身或改变 GUID
- 空 Branch 名会在任何 Git 命令执行前被拒绝
- 初始化配置与日常同步拆为两个独立窗口
- 在 Project 面板右击已跟踪的 `.cs` 文件，可恢复到 `HEAD^` 或从最近 100 条文件 Commit 历史中选择版本
- 单文件恢复只修改工作区，不移动 Branch/HEAD；覆盖前备份到 `Library/UnityTeamGit/FileRestoreBackup`
- Workspace 提供“按 Commit 回退整个项目”：从当前 Branch 最近 100 个主线 Commit 中选择目标快照，要求工作区完全干净，并创建新的本地回退 Commit
- 整项目回退不会执行 `reset --hard`、历史重写或自动 Push；`Assets/UnityTeamGit` 管理插件自身会保留，确认项目无误后再使用现有 Push 按钮上传
- NAS 插件库根目录的每个一级文件夹视为一个插件；文件夹内放置一个 `.unitypackage`，或直接放置完整代码/资源
- 面板以左右两列显示“已安装 / 未安装”插件，支持按文件夹名称筛选
- `.unitypackage` 下载到本地暂存区后自动打开 Unity 原生导入界面；纯代码插件复制到可配置的 `Assets` 目录并保留文件夹结构和 `.meta`
- 每次安装都会在 `ProjectSettings/UnityTeamGitNasPluginLibrary.json` 记录资源清单；Remove 只删除清单内文件、清理空目录，并保留后来加入的其他内容
- UnityPackage 若会覆盖工程已有文件则拒绝安装；卸载前若发现插件文件已被修改会再次确认，已有目录的原始 `.meta` 会被恢复
- NAS 插件库路径保存在本机 EditorPrefs，不保存 NAS 密码；路径越界、符号链接和 Unity 忙碌时拒绝安装或卸载

## 服务端前提

Gitea 需要启用 `ENABLE_PUSH_CREATE_USER` 或 `ENABLE_PUSH_CREATE_ORG`，并将 `DEFAULT_PUSH_CREATE_PRIVATE` 设为 `false`。

NAS 插件库需要将一个目录以 SMB 共享给团队电脑，例如 `\\192.168.28.25\共享名\UnityPlugins`。请给普通成员只读权限；需要维护插件库的人员再单独授予写权限。

推荐目录结构：

```text
UnityPlugins/
├─ OdinInspector/
│  └─ OdinInspector.unitypackage
└─ TeamUtilities/
   ├─ Editor/
   └─ Runtime/
```

一个插件文件夹中只能放置一个 `.unitypackage`。如果没有安装包，文件夹内全部文件和子目录会作为纯代码/资源插件导入。
