using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;

namespace Blackbox.UnityTeamGit.Editor
{
    internal enum PreflightState
    {
        Pass,
        Warning,
        Error
    }

    internal sealed class PreflightCheck
    {
        public string Name { get; set; }
        public string Detail { get; set; }
        public PreflightState State { get; set; }

        public bool IsBlocking
        {
            get { return State == PreflightState.Error; }
        }
    }

    internal sealed class InitializationResult
    {
        public bool Success { get; set; }
        public bool ExistingRepository { get; set; }
        public string Message { get; set; }
    }

    internal enum CloudRepositoryState
    {
        Unknown,
        Missing,
        Exists
    }

    internal sealed class RepositoryProbe
    {
        public bool LocalRepositoryExists { get; set; }
        public bool OriginConfigured { get; set; }
        public bool OriginMatches { get; set; }
        public string OriginUrl { get; set; }
        public string CurrentBranch { get; set; }
        public CloudRepositoryState CloudState { get; set; }
        public string CloudDetail { get; set; }

        public bool CanFirstPush
        {
            get
            {
                return LocalRepositoryExists &&
                       OriginConfigured &&
                       OriginMatches &&
                       CloudState == CloudRepositoryState.Missing;
            }
        }
    }

    internal sealed class GitBranchSnapshot
    {
        public string CurrentBranch { get; set; }
        public List<string> RemoteBranches { get; set; }
    }

    internal sealed class UnityTeamGitInitializer
    {
        private const int NormalTimeout = 120000;
        private const int PreflightNetworkTimeout = 20000;
        private const int PushTimeout = 30 * 60 * 1000;

        private readonly UnityTeamGitSettings settings;
        private readonly string gitExecutable;

        public RepositoryProbe LastRepositoryProbe { get; private set; }

        public UnityTeamGitInitializer(UnityTeamGitSettings settings)
        {
            this.settings = settings;
            gitExecutable = UnityTeamGitProcess.FindGitExecutable();
            LastRepositoryProbe = new RepositoryProbe { CloudState = CloudRepositoryState.Unknown };
        }

        public async Task<List<PreflightCheck>> RunPreflightAsync()
        {
            var checks = new List<PreflightCheck>();
            var root = settings.ProjectRoot;

            var validationErrors = settings.Validate().ToList();
            checks.Add(new PreflightCheck
            {
                Name = "项目和服务器配置",
                Detail = validationErrors.Count == 0 ? "工程名和连接参数有效" : string.Join(" ", validationErrors),
                State = validationErrors.Count == 0 ? PreflightState.Pass : PreflightState.Error
            });

            var projectShapeValid = Directory.Exists(Path.Combine(root, "Assets")) &&
                                    Directory.Exists(Path.Combine(root, "Packages")) &&
                                    Directory.Exists(Path.Combine(root, "ProjectSettings"));
            checks.Add(new PreflightCheck
            {
                Name = "Unity 工程目录",
                Detail = projectShapeValid ? root : "未找到 Assets、Packages 或 ProjectSettings",
                State = projectShapeValid ? PreflightState.Pass : PreflightState.Error
            });

            var unitySettingsReady = UnityEditor.VersionControlSettings.mode == "Visible Meta Files" &&
                                     EditorSettings.serializationMode == SerializationMode.ForceText;
            checks.Add(new PreflightCheck
            {
                Name = "Unity 版本控制设置",
                Detail = unitySettingsReady ? "Visible Meta Files / Force Text" : "初始化时将自动修正",
                State = unitySettingsReady ? PreflightState.Pass : PreflightState.Warning
            });

            var gitVersion = await RunGitAsync(new[] { "--version" }, NormalTimeout);
            checks.Add(new PreflightCheck
            {
                Name = "Git",
                Detail = gitVersion.Success ? gitVersion.CombinedOutput : FriendlyFailure(gitVersion),
                State = gitVersion.Success ? PreflightState.Pass : PreflightState.Error
            });

            if (!gitVersion.Success)
                return checks;

            var lfsVersion = await RunGitAsync(new[] { "lfs", "version" }, NormalTimeout);
            checks.Add(new PreflightCheck
            {
                Name = "Git LFS",
                Detail = lfsVersion.Success ? lfsVersion.CombinedOutput : FriendlyFailure(lfsVersion),
                State = lfsVersion.Success ? PreflightState.Pass : PreflightState.Error
            });

            var name = await RunGitAsync(new[] { "config", "--global", "--get", "user.name" }, NormalTimeout);
            var email = await RunGitAsync(new[] { "config", "--global", "--get", "user.email" }, NormalTimeout);
            var identityReady = name.Success && email.Success &&
                                !string.IsNullOrWhiteSpace(name.StandardOutput) &&
                                !string.IsNullOrWhiteSpace(email.StandardOutput);
            checks.Add(new PreflightCheck
            {
                Name = "Git 身份",
                Detail = identityReady
                    ? name.StandardOutput.Trim() + " <" + email.StandardOutput.Trim() + ">"
                    : "请先设置全局 user.name 和 user.email",
                State = identityReady ? PreflightState.Pass : PreflightState.Error
            });

            var keyExists = File.Exists(settings.sshPrivateKeyPath);
            checks.Add(new PreflightCheck
            {
                Name = "Gitea SSH 私钥",
                Detail = keyExists ? settings.sshPrivateKeyPath : "没有找到指定私钥",
                State = keyExists ? PreflightState.Pass : PreflightState.Error
            });

            LastRepositoryProbe = await ProbeRepositoryAsync();

            var gitDirectory = Path.Combine(root, ".git");
            if (Directory.Exists(gitDirectory) || File.Exists(gitDirectory))
            {
                checks.Add(new PreflightCheck
                {
                    Name = "本地仓库状态",
                    Detail = "已存在 Git 仓库；不会重置历史或自动 Push",
                    State = PreflightState.Pass
                });
                checks.Add(new PreflightCheck
                {
                    Name = "当前 Branch",
                    Detail = string.IsNullOrWhiteSpace(LastRepositoryProbe.CurrentBranch) ? "detached HEAD 或无法识别" : LastRepositoryProbe.CurrentBranch,
                    State = string.IsNullOrWhiteSpace(LastRepositoryProbe.CurrentBranch) ? PreflightState.Error : PreflightState.Pass
                });

                await AddRemoteCheckAsync(checks, "origin", settings.OriginUrl);
                await AddRemoteCheckAsync(checks, "tailnet", settings.TailnetUrl);
            }
            else
            {
                var containingRepository = await RunGitAsync(new[] { "rev-parse", "--show-toplevel" }, NormalTimeout);
                if (containingRepository.Success && !SamePath(containingRepository.StandardOutput.Trim(), root))
                {
                    checks.Add(new PreflightCheck
                    {
                        Name = "Git 仓库边界",
                        Detail = "工程位于另一个仓库内：" + containingRepository.StandardOutput.Trim(),
                        State = PreflightState.Error
                    });
                }
                else
                {
                    checks.Add(new PreflightCheck
                    {
                        Name = "本地仓库状态",
                        Detail = "尚未初始化，可以安全创建",
                        State = PreflightState.Pass
                    });
                }
            }

            AddCloudRepositoryCheck(checks, LastRepositoryProbe);

            if (keyExists && !string.IsNullOrWhiteSpace(settings.lanHost))
            {
                var ssh = await TestSshAsync();
                checks.Add(ssh);
            }

            return checks;
        }

        public async Task<RepositoryProbe> ProbeRepositoryAsync()
        {
            var root = settings.ProjectRoot;
            var gitDirectory = Path.Combine(root, ".git");
            var probe = new RepositoryProbe
            {
                LocalRepositoryExists = Directory.Exists(gitDirectory) || File.Exists(gitDirectory),
                CloudState = CloudRepositoryState.Unknown
            };

            if (probe.LocalRepositoryExists)
            {
                var currentBranch = await RunGitAsync(new[] { "branch", "--show-current" }, NormalTimeout);
                if (currentBranch.Success)
                    probe.CurrentBranch = currentBranch.StandardOutput.Trim();

                var origin = await RunGitAsync(new[] { "remote", "get-url", "origin" }, NormalTimeout);
                if (origin.Success && !string.IsNullOrWhiteSpace(origin.StandardOutput))
                {
                    probe.OriginConfigured = true;
                    probe.OriginUrl = origin.StandardOutput.Trim();
                    probe.OriginMatches = SameRemote(probe.OriginUrl, settings.OriginUrl);
                }
            }

            if (!File.Exists(settings.sshPrivateKeyPath))
            {
                probe.CloudDetail = "缺少 SSH 私钥，无法检查云端仓库";
                return probe;
            }

            var remote = await RunGitWithSshAsync(new[] { "ls-remote", settings.OriginUrl }, PreflightNetworkTimeout);
            if (remote.Success)
            {
                probe.CloudState = CloudRepositoryState.Exists;
                probe.CloudDetail = "已找到 " + settings.OriginUrl;
            }
            else if (IsMissingRepository(remote))
            {
                probe.CloudState = CloudRepositoryState.Missing;
                probe.CloudDetail = "未找到同名仓库；完成本地初始化后可使用“首次 Push”创建";
            }
            else
            {
                probe.CloudDetail = "无法确认云端仓库状态：" + FriendlyFailure(remote);
            }

            return probe;
        }

        public async Task<InitializationResult> InitializeAsync(Action<string> log)
        {
            var validationErrors = settings.Validate().ToList();
            if (validationErrors.Count > 0)
                throw new InvalidOperationException(string.Join(Environment.NewLine, validationErrors));

            var root = settings.ProjectRoot;
            var gitDirectory = Path.Combine(root, ".git");
            var repositoryAlreadyExisted = Directory.Exists(gitDirectory) || File.Exists(gitDirectory);

            var probe = await ProbeRepositoryAsync();
            if (probe.CloudState == CloudRepositoryState.Unknown)
                throw new InvalidOperationException("无法确认云端仓库状态，初始化已停止。" + Environment.NewLine + probe.CloudDetail);
            if (!repositoryAlreadyExisted && probe.CloudState == CloudRepositoryState.Exists)
                throw new InvalidOperationException("Gitea 已存在同名仓库，但当前工程没有本地 Git 仓库。请先克隆云端仓库，避免创建两套无关历史。");
            if (repositoryAlreadyExisted && probe.CloudState == CloudRepositoryState.Exists && !probe.OriginConfigured)
                throw new InvalidOperationException("Gitea 已存在同名仓库，但本地没有配置 origin。为避免连接到错误历史，插件不会自动关联。");

            log("设置 Unity：Visible Meta Files / Force Text");
            UnityEditor.VersionControlSettings.mode = "Visible Meta Files";
            EditorSettings.serializationMode = SerializationMode.ForceText;
            AssetDatabase.SaveAssets();

            var ignoreAdded = MergeTemplate(Path.Combine(root, ".gitignore"), UnityTeamGitTemplates.GitIgnore);
            var attributesAdded = MergeTemplate(Path.Combine(root, ".gitattributes"), UnityTeamGitTemplates.GitAttributes);
            log("模板完成：.gitignore 新增 " + ignoreAdded + " 条，.gitattributes 新增 " + attributesAdded + " 条");

            if (!repositoryAlreadyExisted)
            {
                await GitOrThrowAsync(log, new[] { "init", "-b", settings.defaultBranch.Trim() });
                await GitOrThrowAsync(log, new[] { "config", "--local", "unityteamgit.initializationStarted", "true" });
            }

            await GitOrThrowAsync(log, new[] { "lfs", "install", "--local" });
            await GitOrThrowAsync(log, new[] { "config", "--local", "lfs.ssh.automultiplex", "false" });
            await ConfigureSshAsync(log);
            await ConfigureSmartMergeAsync(log);
            await EnsureRemoteAsync(log, "origin", settings.OriginUrl);
            await EnsureRemoteAsync(log, "tailnet", settings.TailnetUrl);

            var initializationStarted = await ReadLocalBoolAsync("unityteamgit.initializationStarted");
            if (repositoryAlreadyExisted && !initializationStarted)
            {
                return new InitializationResult
                {
                    Success = true,
                    ExistingRepository = true,
                    Message = "检测到已有仓库：Unity、LFS、Smart Merge 和远程地址已检查，不会自动提交或 Push。"
                };
            }

            var head = await RunGitAsync(new[] { "rev-parse", "--verify", "HEAD" }, NormalTimeout);
            if (!head.Success)
            {
                await GitOrThrowAsync(log, new[] { "add", "--all" }, PushTimeout);
                var staged = await RunGitAsync(new[] { "diff", "--cached", "--quiet" }, NormalTimeout);
                if (staged.ExitCode == 0)
                    throw new InvalidOperationException("没有可提交的工程文件。请确认 .gitignore 没有排除 Assets、Packages 或 ProjectSettings。");

                await GitOrThrowAsync(log, new[] { "commit", "-m", settings.initialCommitMessage.Trim() }, PushTimeout);
            }

            return new InitializationResult
            {
                Success = true,
                ExistingRepository = false,
                Message = "本地初始化和首次提交已完成。确认检查结果后，点击“首次 Push”创建公开 Gitea 仓库。"
            };
        }

        public async Task<InitializationResult> FirstPushAsync(Action<string> log)
        {
            var validationErrors = settings.Validate().ToList();
            if (validationErrors.Count > 0)
                throw new InvalidOperationException(string.Join(Environment.NewLine, validationErrors));

            var probe = await ProbeRepositoryAsync();
            if (!probe.LocalRepositoryExists)
                throw new InvalidOperationException("本地 Git 仓库不存在，请先点击“初始化本地仓库”。");
            if (!probe.OriginConfigured)
                throw new InvalidOperationException("本地尚未配置 origin，请先执行初始化/修复。");
            if (!probe.OriginMatches)
                throw new InvalidOperationException("origin 与面板配置的仓库地址不同，已停止首次 Push。" + Environment.NewLine + "现有：" + probe.OriginUrl + Environment.NewLine + "期望：" + settings.OriginUrl);
            if (probe.CloudState == CloudRepositoryState.Exists)
                throw new InvalidOperationException("云端同名仓库已经存在，“首次 Push”不会覆盖它。请先拉取并确认历史，再使用普通 Git Push。");
            if (probe.CloudState != CloudRepositoryState.Missing)
                throw new InvalidOperationException("无法确认云端仓库不存在，已停止首次 Push。" + Environment.NewLine + probe.CloudDetail);

            await ConfigureSshAsync(log);

            var head = await RunGitAsync(new[] { "rev-parse", "--verify", "HEAD" }, NormalTimeout);
            if (!head.Success)
                throw new InvalidOperationException("本地还没有提交。请先执行初始化，或手动创建提交后再试。");

            var branch = await RunGitAsync(new[] { "branch", "--show-current" }, NormalTimeout);
            if (!branch.Success || string.IsNullOrWhiteSpace(branch.StandardOutput))
                throw new InvalidOperationException("无法确定当前 Git 分支。" + Environment.NewLine + FriendlyFailure(branch));

            var branchName = branch.StandardOutput.Trim();
            log("首次 Push 到局域网 Gitea；Push To Create 将创建公开仓库");
            await GitOrThrowAsync(log, new[] { "push", "--set-upstream", "origin", branchName }, PushTimeout);

            var verify = await RunGitAsync(new[] { "ls-remote", "--heads", "origin", "refs/heads/" + branchName }, NormalTimeout);
            if (!verify.Success || string.IsNullOrWhiteSpace(verify.StandardOutput))
                throw new InvalidOperationException("Push 已结束，但没有在 Gitea 找到远端分支。" + Environment.NewLine + FriendlyFailure(verify));

            await GitOrThrowAsync(log, new[] { "config", "--local", "unityteamgit.initialized", "true" });
            await RunGitAsync(new[] { "config", "--local", "--unset-all", "unityteamgit.initializationStarted" }, NormalTimeout);

            return new InitializationResult
            {
                Success = true,
                ExistingRepository = false,
                Message = "首次 Push 完成，公开仓库已创建：" + settings.LanWebRepositoryUrl
            };
        }

        public async Task<string> RebindRemotesAsync(Action<string> log)
        {
            var validationErrors = settings.Validate().ToList();
            if (validationErrors.Count > 0)
                throw new InvalidOperationException(string.Join(Environment.NewLine, validationErrors));

            var gitDirectory = Path.Combine(settings.ProjectRoot, ".git");
            if (!Directory.Exists(gitDirectory) && !File.Exists(gitDirectory))
                throw new InvalidOperationException("当前工程还不是 Git 仓库，不能执行重新绑定。请先初始化本地仓库。");

            var currentOrigin = await RunGitAsync(new[] { "remote", "get-url", "origin" }, NormalTimeout);
            var currentTailnet = await RunGitAsync(new[] { "remote", "get-url", "tailnet" }, NormalTimeout);
            var oldOrigin = currentOrigin.Success ? currentOrigin.StandardOutput.Trim() : string.Empty;
            var oldTailnet = currentTailnet.Success ? currentTailnet.StandardOutput.Trim() : string.Empty;

            if (SameRemote(oldOrigin, settings.OriginUrl) && SameRemote(oldTailnet, settings.TailnetUrl))
                return "远程仓库地址已经与当前面板配置一致，没有执行修改。";

            log("重新绑定只修改本地 Git 远程地址；不会删除本地提交、修改工作区或操作旧云端仓库");
            if (!string.IsNullOrWhiteSpace(oldOrigin))
                await GitOrThrowAsync(log, new[] { "config", "--local", "--replace-all", "unityteamgit.previousOriginUrl", oldOrigin });
            if (!string.IsNullOrWhiteSpace(oldTailnet))
                await GitOrThrowAsync(log, new[] { "config", "--local", "--replace-all", "unityteamgit.previousTailnetUrl", oldTailnet });

            await ConfigureSshAsync(log);
            await ReplaceRemoteAsync(log, "origin", settings.OriginUrl);
            await ReplaceRemoteAsync(log, "tailnet", settings.TailnetUrl);
            await RunGitAsync(new[] { "config", "--local", "--unset-all", "unityteamgit.initialized" }, NormalTimeout);

            var verifiedOrigin = await RunGitAsync(new[] { "remote", "get-url", "origin" }, NormalTimeout);
            var verifiedTailnet = await RunGitAsync(new[] { "remote", "get-url", "tailnet" }, NormalTimeout);
            if (!verifiedOrigin.Success || !SameRemote(verifiedOrigin.StandardOutput.Trim(), settings.OriginUrl) ||
                !verifiedTailnet.Success || !SameRemote(verifiedTailnet.StandardOutput.Trim(), settings.TailnetUrl))
            {
                throw new InvalidOperationException("远程地址写入后的验证失败。旧地址已记录在本地 Git 配置的 unityteamgit.previousOriginUrl / previousTailnetUrl 中。");
            }

            var probe = await ProbeRepositoryAsync();
            var nextStep = probe.CloudState == CloudRepositoryState.Missing
                ? "目标仓库不存在，可以执行“首次 Push”。"
                : probe.CloudState == CloudRepositoryState.Exists
                    ? "目标仓库已经存在，不会启用“首次 Push”；请先确认并拉取它的历史。"
                    : "远程地址已修改，但暂时无法确认目标仓库状态；请检查 SSH 后重新检查。";

            return "仓库重新绑定完成。" + Environment.NewLine +
                   "origin：" + settings.OriginUrl + Environment.NewLine +
                   "tailnet：" + settings.TailnetUrl + Environment.NewLine +
                   nextStep;
        }

        public async Task<string> PullAsync(Action<string> log)
        {
            await EnsureDailyRepositoryAsync();
            await EnsureCleanWorkingTreeAsync("Pull");

            var branchName = await GetCurrentBranchAsync();
            await GitOrThrowAsync(log, new[] { "fetch", "origin", "--prune" }, PushTimeout);
            if (!await RemoteBranchExistsAsync(branchName))
                throw new InvalidOperationException("远端不存在 origin/" + branchName + "，无法 Pull。请先发布该分支。");

            await GitOrThrowAsync(log, new[] { "pull", "--ff-only", "origin", branchName }, PushTimeout);
            return "Pull 完成：" + branchName;
        }

        public async Task<string> CommitAndPushAsync(Action<string> log, string commitMessage, string expectedBranch)
        {
            if (string.IsNullOrWhiteSpace(commitMessage))
                throw new InvalidOperationException("Commit 说明不能为空。");

            await EnsureDailyRepositoryAsync();
            await EnsureNoUnmergedFilesAsync();

            var branchName = await GetCurrentBranchAsync();
            if (string.IsNullOrWhiteSpace(expectedBranch) || !string.Equals(branchName, expectedBranch.Trim(), StringComparison.Ordinal))
                throw new InvalidOperationException("界面显示的 Branch 与 Git 当前 Branch 不一致。为避免推错分支，Push 已停止；请重新检查后再试。" + Environment.NewLine + "界面：" + expectedBranch + Environment.NewLine + "Git：" + branchName);
            log("Push 目标已锁定：HEAD -> origin/" + branchName);
            await GitOrThrowAsync(log, new[] { "fetch", "origin", "--prune" }, PushTimeout);
            await EnsureRemoteNotAheadAsync(branchName);

            await GitOrThrowAsync(log, new[] { "add", "--all" }, PushTimeout);
            var staged = await RunGitAsync(new[] { "diff", "--cached", "--quiet" }, NormalTimeout);
            if (staged.ExitCode == 1)
            {
                await GitOrThrowAsync(log, new[] { "commit", "-m", commitMessage.Trim() }, PushTimeout);
            }
            else if (staged.ExitCode == 0)
            {
                log("没有新的文件修改；将检查并推送尚未上传的本地提交");
            }
            else
            {
                throw new InvalidOperationException("无法检查暂存区状态。" + Environment.NewLine + FriendlyFailure(staged));
            }

            var confirmedBranch = await GetCurrentBranchAsync();
            if (!string.Equals(confirmedBranch, branchName, StringComparison.Ordinal))
                throw new InvalidOperationException("Push 前 Branch 已发生变化：原为 " + branchName + "，现为 " + confirmedBranch + "。操作已停止。");

            var localHead = await GitOrThrowAsync(log, new[] { "rev-parse", "HEAD" });
            await GitOrThrowAsync(log, new[] { "push", "--set-upstream", "origin", "HEAD:refs/heads/" + branchName }, PushTimeout);
            var verify = await RunGitAsync(new[] { "ls-remote", "--heads", "origin", "refs/heads/" + branchName }, NormalTimeout);
            if (!verify.Success || string.IsNullOrWhiteSpace(verify.StandardOutput))
                throw new InvalidOperationException("Push 已结束，但没有在 Gitea 找到远端分支。" + Environment.NewLine + FriendlyFailure(verify));
            var remoteHead = verify.StandardOutput.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.Equals(localHead.StandardOutput.Trim(), remoteHead, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("远端 Branch 的提交哈希与本地 HEAD 不一致，Push 验证失败。");

            return "Push 完成：" + branchName;
        }

        public async Task<GitBranchSnapshot> GetRemoteBranchesAsync(Action<string> log)
        {
            await EnsureDailyRepositoryAsync();
            await GitOrThrowAsync(log, new[] { "fetch", "origin", "--prune" }, PushTimeout);

            var result = await GitOrThrowAsync(
                log,
                new[] { "for-each-ref", "--format=%(refname:short)", "refs/remotes/origin" });
            var branches = result.StandardOutput
                .Replace("\r\n", "\n")
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.StartsWith("origin/", StringComparison.Ordinal) &&
                               !string.Equals(line, "origin/HEAD", StringComparison.Ordinal))
                .Select(line => line.Substring("origin/".Length))
                .Where(line => line.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(line => line, StringComparer.Ordinal)
                .ToList();

            return new GitBranchSnapshot
            {
                CurrentBranch = await GetCurrentBranchAsync(),
                RemoteBranches = branches
            };
        }

        public async Task<string> SwitchToRemoteBranchAsync(Action<string> log, string branchName)
        {
            branchName = NormalizeRequiredBranchName(branchName);
            await EnsureDailyRepositoryAsync();
            await EnsureCleanWorkingTreeAsync("切换 Branch");
            await ValidateBranchNameAsync(branchName);
            await GitOrThrowAsync(log, new[] { "fetch", "origin", "--prune" }, PushTimeout);

            if (!await RemoteBranchExistsAsync(branchName))
                throw new InvalidOperationException("Gitea 上不存在 Branch：" + branchName);

            var pluginBackup = BackupPluginForBranchSwitch();
            try
            {
                var local = await RunGitAsync(new[] { "show-ref", "--verify", "--quiet", "refs/heads/" + branchName }, NormalTimeout);
                if (local.Success)
                {
                    await GitOrThrowAsync(log, new[] { "switch", branchName }, PushTimeout);
                    await GitOrThrowAsync(log, new[] { "branch", "--set-upstream-to=origin/" + branchName, branchName });
                    await GitOrThrowAsync(log, new[] { "pull", "--ff-only", "origin", branchName }, PushTimeout);
                }
                else
                {
                    await GitOrThrowAsync(log, new[] { "switch", "--track", "-c", branchName, "origin/" + branchName }, PushTimeout);
                }
            }
            finally
            {
                RestorePluginIfMissing(pluginBackup);
            }

            var confirmedBranch = await GetCurrentBranchAsync();
            if (!string.Equals(confirmedBranch, branchName, StringComparison.Ordinal))
                throw new InvalidOperationException("Branch 切换验证失败：期望 " + branchName + "，实际 " + confirmedBranch);
            var upstream = await RunGitAsync(new[] { "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{upstream}" }, NormalTimeout);
            var expectedUpstream = "origin/" + branchName;
            if (!upstream.Success || !string.Equals(upstream.StandardOutput.Trim(), expectedUpstream, StringComparison.Ordinal))
                throw new InvalidOperationException("Branch 已切换，但上游连接验证失败。期望：" + expectedUpstream);

            return "已切换到 Branch：" + branchName;
        }

        public async Task<string> CreateAndPublishBranchAsync(Action<string> log, string branchName)
        {
            branchName = NormalizeRequiredBranchName(branchName);
            await EnsureDailyRepositoryAsync();
            await EnsureCleanWorkingTreeAsync("新建 Branch");
            await ValidateBranchNameAsync(branchName);
            await GitOrThrowAsync(log, new[] { "fetch", "origin", "--prune" }, PushTimeout);

            if (await RemoteBranchExistsAsync(branchName))
                throw new InvalidOperationException("Gitea 已存在同名 Branch，请改为选择并连接已有 Branch。");

            var local = await RunGitAsync(new[] { "show-ref", "--verify", "--quiet", "refs/heads/" + branchName }, NormalTimeout);
            if (local.Success)
                throw new InvalidOperationException("本地已存在同名 Branch，请在已有 Branch 列表中选择或手动处理。");

            await GitOrThrowAsync(log, new[] { "switch", "-c", branchName }, PushTimeout);
            var confirmedBranch = await GetCurrentBranchAsync();
            if (!string.Equals(confirmedBranch, branchName, StringComparison.Ordinal))
                throw new InvalidOperationException("新建 Branch 验证失败，当前 Branch 不是 " + branchName);
            await GitOrThrowAsync(log, new[] { "push", "--set-upstream", "origin", "HEAD:refs/heads/" + branchName }, PushTimeout);
            return "已新建并发布 Branch：" + branchName;
        }

        private static void AddCloudRepositoryCheck(List<PreflightCheck> checks, RepositoryProbe probe)
        {
            var state = PreflightState.Error;
            if (probe.CloudState == CloudRepositoryState.Missing)
                state = PreflightState.Warning;
            else if (probe.CloudState == CloudRepositoryState.Exists && probe.LocalRepositoryExists && probe.OriginConfigured && probe.OriginMatches)
                state = PreflightState.Pass;

            var detail = probe.CloudDetail;
            if (probe.CloudState == CloudRepositoryState.Exists && !probe.LocalRepositoryExists)
                detail += "。当前工程没有本地 Git 仓库，请克隆现有仓库，不要重新初始化";
            else if (probe.CloudState == CloudRepositoryState.Exists && !probe.OriginConfigured)
                detail += "。本地未配置 origin，插件不会自动关联可能无关的历史";

            checks.Add(new PreflightCheck
            {
                Name = "云端 Gitea 仓库",
                Detail = detail,
                State = state
            });
        }

        private async Task EnsureDailyRepositoryAsync()
        {
            var probe = await ProbeRepositoryAsync();
            if (!probe.LocalRepositoryExists)
                throw new InvalidOperationException("本地 Git 仓库不存在，请先完成初始化。");
            if (!probe.OriginConfigured)
                throw new InvalidOperationException("本地没有配置 origin，请先执行初始化/修复。");
            if (!probe.OriginMatches)
                throw new InvalidOperationException("origin 与插件配置不一致，操作已停止。" + Environment.NewLine + "现有：" + probe.OriginUrl + Environment.NewLine + "期望：" + settings.OriginUrl);
            if (probe.CloudState != CloudRepositoryState.Exists)
                throw new InvalidOperationException("无法确认 Gitea 仓库存在，日常同步已停止。" + Environment.NewLine + probe.CloudDetail);
        }

        private async Task<string> GetCurrentBranchAsync()
        {
            var branch = await RunGitAsync(new[] { "branch", "--show-current" }, NormalTimeout);
            if (!branch.Success || string.IsNullOrWhiteSpace(branch.StandardOutput))
                throw new InvalidOperationException("当前处于 detached HEAD 或无法确定 Branch。" + Environment.NewLine + FriendlyFailure(branch));
            return branch.StandardOutput.Trim();
        }

        private async Task EnsureNoUnmergedFilesAsync()
        {
            var conflicts = await RunGitAsync(new[] { "diff", "--name-only", "--diff-filter=U" }, NormalTimeout);
            if (!conflicts.Success)
                throw new InvalidOperationException("无法检查冲突状态。" + Environment.NewLine + FriendlyFailure(conflicts));
            if (!string.IsNullOrWhiteSpace(conflicts.StandardOutput))
                throw new InvalidOperationException("存在尚未解决的冲突，Push 已停止：" + Environment.NewLine + conflicts.StandardOutput.Trim());
        }

        private async Task EnsureCleanWorkingTreeAsync(string operationName)
        {
            await EnsureNoUnmergedFilesAsync();
            var status = await RunGitAsync(new[] { "status", "--porcelain" }, NormalTimeout);
            if (!status.Success)
                throw new InvalidOperationException("无法检查工作区状态。" + Environment.NewLine + FriendlyFailure(status));
            if (!string.IsNullOrWhiteSpace(status.StandardOutput))
                throw new InvalidOperationException(operationName + " 要求工作区完全干净。请先使用 Push_NoCommit、Push_Commit 或手动处理这些文件：" + Environment.NewLine + status.StandardOutput.Trim());
        }

        private async Task ValidateBranchNameAsync(string branchName)
        {
            if (string.IsNullOrWhiteSpace(branchName))
                throw new InvalidOperationException("Branch 名称不能为空。");
            var check = await RunGitAsync(new[] { "check-ref-format", "--branch", branchName.Trim() }, NormalTimeout);
            if (!check.Success)
                throw new InvalidOperationException("Branch 名称无效：" + branchName + Environment.NewLine + FriendlyFailure(check));
        }

        private static string NormalizeRequiredBranchName(string branchName)
        {
            var normalized = branchName == null ? string.Empty : branchName.Trim();
            if (normalized.Length == 0)
                throw new InvalidOperationException("Branch 名称不能为空；没有执行任何 Git 命令。");
            return normalized;
        }

        private async Task<bool> RemoteBranchExistsAsync(string branchName)
        {
            var result = await RunGitAsync(new[] { "show-ref", "--verify", "--quiet", "refs/remotes/origin/" + branchName }, NormalTimeout);
            return result.Success;
        }

        private async Task EnsureRemoteNotAheadAsync(string branchName)
        {
            if (!await RemoteBranchExistsAsync(branchName))
                return;

            var counts = await RunGitAsync(new[] { "rev-list", "--left-right", "--count", "HEAD...refs/remotes/origin/" + branchName }, NormalTimeout);
            if (!counts.Success)
                throw new InvalidOperationException("无法比较本地与远端历史。" + Environment.NewLine + FriendlyFailure(counts));

            var parts = counts.StandardOutput.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            int remoteOnly;
            if (parts.Length < 2 || !int.TryParse(parts[1], out remoteOnly))
                throw new InvalidOperationException("无法解析本地与远端的提交差异：" + counts.StandardOutput.Trim());
            if (remoteOnly > 0)
                throw new InvalidOperationException("远端比本地多 " + remoteOnly + " 个提交。为防止覆盖队友工作，Push 已停止；请先 Pull。");
        }

        private async Task AddRemoteCheckAsync(List<PreflightCheck> checks, string remoteName, string expectedUrl)
        {
            var result = await RunGitAsync(new[] { "remote", "get-url", remoteName }, NormalTimeout);
            if (!result.Success)
            {
                checks.Add(new PreflightCheck
                {
                    Name = "远程 " + remoteName,
                    Detail = "尚未配置，初始化时将添加",
                    State = PreflightState.Warning
                });
                return;
            }

            var actual = result.StandardOutput.Trim();
            var matches = SameRemote(actual, expectedUrl);
            checks.Add(new PreflightCheck
            {
                Name = "远程 " + remoteName,
                Detail = matches ? actual : "现有地址与面板配置不同：" + actual,
                State = matches ? PreflightState.Pass : PreflightState.Error
            });
        }

        private async Task<PreflightCheck> TestSshAsync()
        {
            var sshExecutable = UnityTeamGitProcess.FindSshExecutable();
            var result = await UnityTeamGitProcess.RunAsync(
                sshExecutable,
                new[]
                {
                    "-T",
                    "-p", settings.sshPort.ToString(),
                    "-i", settings.sshPrivateKeyPath,
                    "-o", "IdentitiesOnly=yes",
                    "-o", "BatchMode=yes",
                    "-o", "StrictHostKeyChecking=accept-new",
                    "-o", "ConnectTimeout=6",
                    "git@" + settings.lanHost.Trim()
                },
                settings.ProjectRoot,
                10000);

            var output = result.CombinedOutput;
            var authenticated = result.Success ||
                                output.IndexOf("authenticated", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                output.IndexOf("Gitea", StringComparison.OrdinalIgnoreCase) >= 0;

            return new PreflightCheck
            {
                Name = "NAS 局域网 SSH",
                Detail = authenticated ? "SSH Key 鉴权成功" : FriendlyFailure(result),
                State = authenticated ? PreflightState.Pass : PreflightState.Error
            };
        }

        private async Task ConfigureSshAsync(Action<string> log)
        {
            await GitOrThrowAsync(log, new[] { "config", "--local", "core.sshCommand", BuildSshCommand() });
        }

        private string BuildSshCommand()
        {
            var ssh = ToGitPath(UnityTeamGitProcess.FindSshExecutable());
            var key = ToGitPath(settings.sshPrivateKeyPath);
            return "\"" + ssh + "\" -i \"" + key + "\" -o IdentitiesOnly=yes -o BatchMode=yes" +
                   " -o StrictHostKeyChecking=accept-new -o ConnectTimeout=8" +
                   " -o ServerAliveInterval=15 -o ServerAliveCountMax=4";
        }

        private async Task ConfigureSmartMergeAsync(Action<string> log)
        {
            var toolsDirectory = Path.Combine(EditorApplication.applicationContentsPath, "Tools");
            var candidates = new[]
            {
                Path.Combine(toolsDirectory, "UnityYAMLMerge.exe"),
                Path.Combine(toolsDirectory, "UnityYAMLMerge")
            };
            var mergeTool = candidates.FirstOrDefault(File.Exists);
            if (string.IsNullOrEmpty(mergeTool))
                throw new FileNotFoundException("找不到 UnityYAMLMerge。", candidates[0]);

            var driver = "\"" + ToGitPath(mergeTool) + "\" merge -p %O %B %A %A";
            await GitOrThrowAsync(log, new[] { "config", "--local", "merge.unityyamlmerge.name", "Unity Smart Merge" });
            await GitOrThrowAsync(log, new[] { "config", "--local", "merge.unityyamlmerge.driver", driver });
            await GitOrThrowAsync(log, new[] { "config", "--local", "merge.unityyamlmerge.recursive", "binary" });
        }

        private async Task EnsureRemoteAsync(Action<string> log, string name, string expectedUrl)
        {
            var existing = await RunGitAsync(new[] { "remote", "get-url", name }, NormalTimeout);
            if (!existing.Success)
            {
                await GitOrThrowAsync(log, new[] { "remote", "add", name, expectedUrl });
                return;
            }

            var actual = existing.StandardOutput.Trim();
            if (!SameRemote(actual, expectedUrl))
            {
                throw new InvalidOperationException(
                    "远程 " + name + " 已存在且地址不同。为避免推错仓库，插件不会覆盖。" + Environment.NewLine +
                    "现有：" + actual + Environment.NewLine +
                    "期望：" + expectedUrl);
            }

            log("远程 " + name + " 已存在，地址正确");
        }

        private async Task ReplaceRemoteAsync(Action<string> log, string name, string expectedUrl)
        {
            log("重新绑定远程 " + name + "：" + expectedUrl);
            await GitOrThrowAsync(log, new[] { "config", "--local", "--replace-all", "remote." + name + ".url", expectedUrl });
            await GitOrThrowAsync(
                log,
                new[]
                {
                    "config", "--local", "--replace-all", "remote." + name + ".fetch",
                    "+refs/heads/*:refs/remotes/" + name + "/*"
                });
            await RunGitAsync(new[] { "config", "--local", "--unset-all", "remote." + name + ".pushurl" }, NormalTimeout);
        }

        private async Task<bool> ReadLocalBoolAsync(string key)
        {
            var result = await RunGitAsync(new[] { "config", "--local", "--bool", "--get", key }, NormalTimeout);
            return result.Success && string.Equals(result.StandardOutput.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<CommandResult> GitOrThrowAsync(Action<string> log, string[] arguments, int timeout = NormalTimeout)
        {
            log("$ " + UnityTeamGitProcess.Describe("git", arguments));
            var result = await RunGitAsync(arguments, timeout);
            if (!string.IsNullOrWhiteSpace(result.CombinedOutput))
                log(result.CombinedOutput);

            if (!result.Success)
                throw new InvalidOperationException("Git 命令失败。" + Environment.NewLine + FriendlyFailure(result));

            return result;
        }

        private Task<CommandResult> RunGitAsync(string[] arguments, int timeout)
        {
            var fullArguments = new List<string> { "-C", settings.ProjectRoot };
            fullArguments.AddRange(arguments);
            return UnityTeamGitProcess.RunAsync(gitExecutable, fullArguments, settings.ProjectRoot, timeout);
        }

        private Task<CommandResult> RunGitWithSshAsync(string[] arguments, int timeout)
        {
            var withSsh = new List<string> { "-c", "core.sshCommand=" + BuildSshCommand() };
            withSsh.AddRange(arguments);
            return RunGitAsync(withSsh.ToArray(), timeout);
        }

        private static int MergeTemplate(string path, string template)
        {
            var requiredLines = template.Replace("\r\n", "\n").Split('\n');
            if (!File.Exists(path))
            {
                File.WriteAllText(path, template.Replace("\r\n", "\n").TrimEnd() + "\n", new UTF8Encoding(false));
                return requiredLines.Count(line => !string.IsNullOrWhiteSpace(line));
            }

            var existingText = File.ReadAllText(path).Replace("\r\n", "\n");
            var existingLines = new HashSet<string>(
                existingText.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0),
                StringComparer.Ordinal);
            var missing = requiredLines
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && !existingLines.Contains(line))
                .ToList();

            if (missing.Count == 0)
                return 0;

            var builder = new StringBuilder(existingText.TrimEnd());
            builder.Append("\n\n# Added by Unity Team Git\n");
            foreach (var line in missing)
                builder.Append(line).Append('\n');
            File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
            return missing.Count;
        }

        private string BackupPluginForBranchSwitch()
        {
            var source = Path.Combine(settings.ProjectRoot, "Assets", "UnityTeamGit");
            if (!Directory.Exists(source))
                return string.Empty;

            var backup = Path.Combine(settings.ProjectRoot, "Library", "UnityTeamGit", "BranchSwitchBackup");
            if (Directory.Exists(backup))
                Directory.Delete(backup, true);
            if (File.Exists(backup + ".meta"))
                File.Delete(backup + ".meta");
            CopyDirectory(source, backup);
            if (File.Exists(source + ".meta"))
                File.Copy(source + ".meta", backup + ".meta", true);
            return backup;
        }

        private void RestorePluginIfMissing(string backup)
        {
            if (string.IsNullOrWhiteSpace(backup) || !Directory.Exists(backup))
                return;

            var destination = Path.Combine(settings.ProjectRoot, "Assets", "UnityTeamGit");
            var requiredScript = Path.Combine(destination, "Editor", "UnityTeamGitWindow.cs");
            if (!File.Exists(requiredScript))
                CopyDirectory(backup, destination);
            if (!File.Exists(destination + ".meta") && File.Exists(backup + ".meta"))
                File.Copy(backup + ".meta", destination + ".meta", true);
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.GetFiles(source))
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
            foreach (var directory in Directory.GetDirectories(source))
                CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }

        private static bool SameRemote(string left, string right)
        {
            return string.Equals(left.Trim().TrimEnd('/'), right.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        }

        private static bool SamePath(string left, string right)
        {
            var comparison = Environment.OSVersion.Platform == PlatformID.Win32NT
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                                 Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                                 comparison);
        }

        private static string FriendlyFailure(CommandResult result)
        {
            if (result.TimedOut)
                return "操作超时。";
            if (!string.IsNullOrWhiteSpace(result.CombinedOutput))
                return result.CombinedOutput;
            return "退出码：" + result.ExitCode;
        }

        private static bool IsMissingRepository(CommandResult result)
        {
            var output = result.CombinedOutput ?? string.Empty;
            return output.IndexOf("repository does not exist", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   output.IndexOf("repository not found", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   output.IndexOf("cannot find repository", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   output.IndexOf("could not be found", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ToGitPath(string path)
        {
            return path.Replace('\\', '/');
        }
    }
}
