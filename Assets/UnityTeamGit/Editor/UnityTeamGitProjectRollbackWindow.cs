using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Blackbox.UnityTeamGit.Editor
{
    internal sealed class GitProjectRevision
    {
        public string Hash { get; set; }
        public string ShortHash { get; set; }
        public string Date { get; set; }
        public string Author { get; set; }
        public string Subject { get; set; }
    }

    internal sealed class GitProjectHistorySnapshot
    {
        public string CurrentBranch { get; set; }
        public string CurrentHead { get; set; }
        public List<GitProjectRevision> Revisions { get; set; }
    }

    internal sealed class GitProjectRollbackResult
    {
        public string Branch { get; set; }
        public string PreviousHead { get; set; }
        public string NewHead { get; set; }
        public string TargetCommit { get; set; }
        public string Message { get; set; }
    }

    internal sealed class UnityTeamGitProjectRollbackWindow : EditorWindow
    {
        private UnityTeamGitSettings settings;
        private Action<string> log;
        private Action onRepositoryChanged;
        private readonly List<GitProjectRevision> revisions = new List<GitProjectRevision>();
        private Vector2 scroll;
        private int selectedRevision = -1;
        private string currentBranch = string.Empty;
        private string currentHead = string.Empty;
        private string status = "等待加载";
        private bool loading;
        private bool rollingBack;

        [MenuItem("Tools/Unity Team Git/Project Rollback…", priority = 22)]
        private static void ShowStandalone()
        {
            ShowWindow(UnityTeamGitSettings.Load(), null, null);
        }

        public static void ShowWindow(UnityTeamGitSettings settings, Action<string> log, Action onRepositoryChanged)
        {
            var window = CreateInstance<UnityTeamGitProjectRollbackWindow>();
            window.titleContent = new GUIContent("整项目 Commit 回退");
            window.settings = settings;
            window.log = log;
            window.onRepositoryChanged = onRepositoryChanged;
            window.minSize = new Vector2(780f, 520f);
            window.ShowUtility();
            window.LoadHistory();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("按 Commit 回退整个项目", EditorStyles.largeLabel);
            EditorGUILayout.LabelField("当前 Branch：" + (string.IsNullOrWhiteSpace(currentBranch) ? "未知" : currentBranch), EditorStyles.boldLabel);
            EditorGUILayout.LabelField(status, EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(6f);
            EditorGUILayout.HelpBox(
                "此操作不会移动 Branch、删除历史或自动 Push。它会创建一个新的本地 Commit，使所有受 Git 跟踪的项目文件恢复到目标 Commit 的快照。" +
                "为避免回退过程中删除管理工具自身，Assets/UnityTeamGit 及其 .meta 不参与回退。",
                MessageType.Warning);

            scroll = EditorGUILayout.BeginScrollView(scroll, EditorStyles.helpBox);
            for (var index = 0; index < revisions.Count; index++)
            {
                var revision = revisions[index];
                var isCurrent = string.Equals(revision.Hash, currentHead, StringComparison.OrdinalIgnoreCase);
                EditorGUILayout.BeginVertical(index == selectedRevision ? "SelectionRect" : EditorStyles.helpBox);
                using (new EditorGUI.DisabledScope(isCurrent))
                {
                    var label = revision.ShortHash + "  " + revision.Date + "  " + revision.Subject;
                    if (isCurrent)
                        label += "  （当前）";
                    var selected = GUILayout.Toggle(index == selectedRevision, label, "Radio");
                    if (selected)
                        selectedRevision = index;
                }
                EditorGUILayout.LabelField(revision.Author + " · " + revision.Hash, EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndScrollView();

            using (new EditorGUI.DisabledScope(
                       loading || rollingBack || selectedRevision < 0 || selectedRevision >= revisions.Count || EditorUnsafe()))
            {
                if (GUILayout.Button(rollingBack ? "正在创建回退 Commit……" : "将整个项目回退到选中的 Commit", GUILayout.Height(38f)))
                    RollbackSelected();
            }

            using (new EditorGUI.DisabledScope(loading || rollingBack || EditorUnsafe()))
            {
                if (GUILayout.Button("刷新 Commit 列表"))
                    LoadHistory();
            }
        }

        private async void LoadHistory()
        {
            if (loading || rollingBack)
                return;
            if (!UnityTeamGitOperationGate.TryEnter())
            {
                status = "另一个 Git 操作正在执行，请稍后重试。";
                Repaint();
                return;
            }

            loading = true;
            status = "正在读取当前 Branch 的 Commit 历史……";
            Repaint();
            try
            {
                if (settings == null)
                    settings = UnityTeamGitSettings.Load();
                var snapshot = await UnityTeamGitProjectRollback.LoadHistoryAsync(settings.ProjectRoot);
                revisions.Clear();
                revisions.AddRange(snapshot.Revisions);
                currentBranch = snapshot.CurrentBranch;
                currentHead = snapshot.CurrentHead;
                selectedRevision = revisions.FindIndex(revision =>
                    !string.Equals(revision.Hash, currentHead, StringComparison.OrdinalIgnoreCase));
                status = revisions.Count > 0
                    ? "已加载当前 Branch 最近 " + revisions.Count + " 个主线 Commit。"
                    : "当前 Branch 没有可用的 Commit。";
            }
            catch (Exception exception)
            {
                status = "读取失败：" + exception.Message;
                WriteLog(exception.ToString());
                EditorUtility.DisplayDialog("无法读取项目 Commit 历史", exception.Message, "确定");
            }
            finally
            {
                loading = false;
                UnityTeamGitOperationGate.Exit();
                Repaint();
            }
        }

        private async void RollbackSelected()
        {
            if (rollingBack || selectedRevision < 0 || selectedRevision >= revisions.Count)
                return;
            if (EditorUnsafe())
            {
                status = "Unity 正在编译、导入或进入 Play Mode，项目回退已停止。";
                return;
            }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                status = "用户取消保存场景，项目回退未开始。";
                return;
            }

            AssetDatabase.SaveAssets();
            if (!UnityTeamGitOperationGate.TryEnter())
            {
                status = "另一个 Git 操作正在执行，请稍后重试。";
                return;
            }

            rollingBack = true;
            var revision = revisions[selectedRevision];
            status = "正在准备回退到 " + revision.ShortHash + "……";
            Repaint();

            GitProjectRollbackResult result = null;
            try
            {
                result = await UnityTeamGitProjectRollback.CreateRollbackCommitAsync(
                    settings.ProjectRoot,
                    currentBranch,
                    revision,
                    WriteLog);
                status = result == null ? "用户取消，项目没有改变。" : result.Message;
            }
            catch (Exception exception)
            {
                status = "项目回退失败：" + exception.Message;
                WriteLog(exception.ToString());
                EditorUtility.DisplayDialog("整项目回退失败", exception.Message, "确定");
            }
            finally
            {
                rollingBack = false;
                UnityTeamGitOperationGate.Exit();
                Repaint();
            }

            if (result == null)
                return;

            WriteLog(result.Message);
            EditorUtility.DisplayDialog(
                "整项目回退 Commit 已创建",
                result.Message +
                "\n\n原 HEAD：" + ShortHash(result.PreviousHead) +
                "\n目标版本：" + ShortHash(result.TargetCommit) +
                "\n新 Commit：" + ShortHash(result.NewHead) +
                "\n\n请在 Unity 中检查项目；确认无误后使用 Workspace 的 Push_Commit 或 Push_NoCommit 上传。",
                "确定");

            NotifyRepositoryChanged();
            Close();
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
        }

        private void WriteLog(string message)
        {
            if (log != null)
                log(message);
        }

        private void NotifyRepositoryChanged()
        {
            if (onRepositoryChanged != null)
                onRepositoryChanged();
        }

        private static string ShortHash(string hash)
        {
            if (string.IsNullOrWhiteSpace(hash))
                return string.Empty;
            return hash.Substring(0, Math.Min(12, hash.Length));
        }

        private static bool EditorUnsafe()
        {
            return EditorApplication.isCompiling ||
                   EditorApplication.isUpdating ||
                   EditorApplication.isPlayingOrWillChangePlaymode;
        }
    }

    internal static class UnityTeamGitProjectRollback
    {
        private const int NormalTimeout = 120000;
        private const int RestoreTimeout = 600000;
        private static readonly string[] ProjectPathspec =
        {
            ".",
            ":(exclude)Assets/UnityTeamGit",
            ":(exclude)Assets/UnityTeamGit.meta"
        };

        internal static async Task<GitProjectHistorySnapshot> LoadHistoryAsync(string root)
        {
            await EnsureRepositoryAsync(root);
            var branch = await CurrentBranchAsync(root);
            var head = await GitOrThrowAsync(root, null, new[] { "rev-parse", "HEAD" });
            var history = await GitOrThrowAsync(
                root,
                null,
                new[]
                {
                    "log",
                    "--first-parent",
                    "--max-count=100",
                    "--date=iso-strict",
                    "--format=%H%x09%h%x09%ad%x09%an%x09%s",
                    "HEAD"
                });

            var revisions = new List<GitProjectRevision>();
            foreach (var line in history.StandardOutput.Replace("\r\n", "\n").Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                var parts = line.Split('\t');
                if (parts.Length < 5)
                    continue;
                revisions.Add(new GitProjectRevision
                {
                    Hash = parts[0],
                    ShortHash = parts[1],
                    Date = parts[2],
                    Author = parts[3],
                    Subject = string.Join("\t", parts.Skip(4).ToArray())
                });
            }

            return new GitProjectHistorySnapshot
            {
                CurrentBranch = branch,
                CurrentHead = head.StandardOutput.Trim(),
                Revisions = revisions
            };
        }

        internal static async Task<GitProjectRollbackResult> CreateRollbackCommitAsync(
            string root,
            string expectedBranch,
            GitProjectRevision targetRevision,
            Action<string> log)
        {
            if (targetRevision == null || string.IsNullOrWhiteSpace(targetRevision.Hash))
                throw new InvalidOperationException("目标 Commit 无效。");

            await EnsureRepositoryAsync(root);
            await EnsureNoGitOperationInProgressAsync(root);
            await EnsureCleanWorkingTreeAsync(root);

            var branch = await CurrentBranchAsync(root);
            if (string.IsNullOrWhiteSpace(expectedBranch) ||
                !string.Equals(branch, expectedBranch.Trim(), StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "界面显示的 Branch 与 Git 当前 Branch 不一致。回退已停止。\n界面：" + expectedBranch + "\nGit：" + branch);

            await EnsureUpstreamNotAheadAsync(root);

            var headResult = await GitOrThrowAsync(root, log, new[] { "rev-parse", "HEAD" });
            var originalHead = headResult.StandardOutput.Trim();
            var targetResult = await GitOrThrowAsync(
                root,
                log,
                new[] { "rev-parse", "--verify", targetRevision.Hash.Trim() + "^{commit}" });
            var targetCommit = targetResult.StandardOutput.Trim();
            if (string.Equals(originalHead, targetCommit, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("目标 Commit 就是当前 HEAD，不需要回退。");

            var ancestor = await RunGitAsync(root, new[] { "merge-base", "--is-ancestor", targetCommit, originalHead });
            if (ancestor.ExitCode == 1)
                throw new InvalidOperationException("目标 Commit 不属于当前 Branch 的历史，已拒绝跨历史恢复。");
            if (!ancestor.Success)
                throw new InvalidOperationException("无法验证目标 Commit 与当前 Branch 的关系。\n" + FriendlyFailure(ancestor));

            var countResult = await GitOrThrowAsync(
                root,
                null,
                new[] { "rev-list", "--first-parent", "--count", targetCommit + ".." + originalHead });
            int commitDistance;
            if (!int.TryParse(countResult.StandardOutput.Trim(), out commitDistance))
                commitDistance = 0;

            var statArguments = new List<string> { "diff", "--shortstat", targetCommit, originalHead, "--" };
            statArguments.AddRange(ProjectPathspec);
            var statResult = await RunGitAsync(root, statArguments.ToArray());
            var stat = statResult.Success && !string.IsNullOrWhiteSpace(statResult.StandardOutput)
                ? statResult.StandardOutput.Trim()
                : "项目文件存在版本差异";

            if (!EditorUtility.DisplayDialog(
                    "确认回退整个项目",
                    "Branch：" + branch +
                    "\n当前 HEAD：" + ShortHash(originalHead) +
                    "\n目标 Commit：" + targetRevision.ShortHash + " · " + targetRevision.Date + " · " + targetRevision.Subject +
                    "\n跨度：约 " + commitDistance + " 个主线 Commit" +
                    "\n差异：" + stat +
                    "\n\n将创建一个新的本地回退 Commit，不会 reset、删除历史或自动 Push。" +
                    "\n工作区必须保持完全干净；Assets/UnityTeamGit 管理插件自身会保留。",
                    "创建回退 Commit",
                    "取消"))
                return null;

            log?.Invoke("开始整项目回退：" + ShortHash(originalHead) + " -> " + ShortHash(targetCommit));
            var restoreArguments = BuildRestoreArguments(targetCommit);
            var restore = await RunGitAsync(root, restoreArguments, RestoreTimeout);
            if (!restore.Success)
            {
                var cleanup = await RestoreOriginalAsync(root, originalHead);
                throw new InvalidOperationException(
                    "恢复目标快照失败；" + cleanup + "\n" + FriendlyFailure(restore));
            }

            var stagedArguments = new List<string> { "diff", "--cached", "--quiet", "--" };
            stagedArguments.AddRange(ProjectPathspec);
            var staged = await RunGitAsync(root, stagedArguments.ToArray());
            if (staged.ExitCode == 0)
            {
                await RestoreOriginalAsync(root, originalHead);
                throw new InvalidOperationException("目标 Commit 与当前项目快照没有差异，不需要创建回退 Commit。");
            }
            if (staged.ExitCode != 1)
            {
                var cleanup = await RestoreOriginalAsync(root, originalHead);
                throw new InvalidOperationException("无法检查回退暂存区；" + cleanup + "\n" + FriendlyFailure(staged));
            }

            var commitMessage = "Restore project to " + ShortHash(targetCommit) + ": " + targetRevision.Subject;
            var commit = await RunGitAsync(root, new[] { "commit", "-m", commitMessage }, RestoreTimeout);
            if (!commit.Success)
            {
                var cleanup = await RestoreOriginalAsync(root, originalHead);
                throw new InvalidOperationException("创建回退 Commit 失败；" + cleanup + "\n" + FriendlyFailure(commit));
            }

            var newHeadResult = await GitOrThrowAsync(root, log, new[] { "rev-parse", "HEAD" });
            var newHead = newHeadResult.StandardOutput.Trim();
            var parentResult = await GitOrThrowAsync(root, null, new[] { "rev-parse", "HEAD^" });
            if (!string.Equals(parentResult.StandardOutput.Trim(), originalHead, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "回退 Commit 已创建，但父提交验证异常。新 Commit：" + ShortHash(newHead) + "。请停止 Push 并人工检查 Git 历史。");

            var verifyArguments = new List<string> { "diff", "--quiet", targetCommit, newHead, "--" };
            verifyArguments.AddRange(ProjectPathspec);
            var verify = await RunGitAsync(root, verifyArguments.ToArray());
            if (!verify.Success)
                throw new InvalidOperationException(
                    "回退 Commit 已创建，但项目快照验证未通过。新 Commit：" + ShortHash(newHead) + "。请停止 Push 并人工检查差异。");

            return new GitProjectRollbackResult
            {
                Branch = branch,
                PreviousHead = originalHead,
                NewHead = newHead,
                TargetCommit = targetCommit,
                Message = "已在 " + branch + " 创建本地回退 Commit：" + ShortHash(newHead) +
                          "。项目内容已恢复到 " + ShortHash(targetCommit) + "；尚未 Push。"
            };
        }

        private static async Task EnsureRepositoryAsync(string root)
        {
            var repository = await RunGitAsync(root, new[] { "rev-parse", "--is-inside-work-tree" });
            if (!repository.Success || !string.Equals(repository.StandardOutput.Trim(), "true", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("当前 Unity 工程不是有效的 Git 工作区。");
        }

        private static async Task<string> CurrentBranchAsync(string root)
        {
            var branch = await GitOrThrowAsync(root, null, new[] { "rev-parse", "--abbrev-ref", "HEAD" });
            var name = branch.StandardOutput.Trim();
            if (string.IsNullOrWhiteSpace(name) || string.Equals(name, "HEAD", StringComparison.Ordinal))
                throw new InvalidOperationException("当前处于 detached HEAD，不能创建整项目回退 Commit。");
            return name;
        }

        private static async Task EnsureCleanWorkingTreeAsync(string root)
        {
            var status = await GitOrThrowAsync(
                root,
                null,
                new[] { "status", "--porcelain=v1", "--untracked-files=all" });
            if (!string.IsNullOrWhiteSpace(status.StandardOutput))
                throw new InvalidOperationException(
                    "整项目回退要求工作区完全干净。请先 Commit/Push 当前修改，或手动处理后重试。\n\n" + status.StandardOutput.Trim());
        }

        private static async Task EnsureNoGitOperationInProgressAsync(string root)
        {
            foreach (var marker in new[] { "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD" })
            {
                var result = await RunGitAsync(root, new[] { "rev-parse", "-q", "--verify", marker });
                if (result.Success)
                    throw new InvalidOperationException("检测到未完成的 Git 操作（" + marker + "），请先完成或取消后再回退项目。");
            }
        }

        private static async Task EnsureUpstreamNotAheadAsync(string root)
        {
            var upstream = await RunGitAsync(
                root,
                new[] { "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{upstream}" });
            if (!upstream.Success)
                return;

            var compare = await GitOrThrowAsync(
                root,
                null,
                new[] { "rev-list", "--left-right", "--count", "HEAD...@{upstream}" });
            var parts = compare.StandardOutput.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            int upstreamAhead;
            if (parts.Length >= 2 && int.TryParse(parts[1], out upstreamAhead) && upstreamAhead > 0)
                throw new InvalidOperationException(
                    "远端跟踪 Branch 比本地领先 " + upstreamAhead + " 个 Commit。请先 Pull，再执行整项目回退。");
        }

        private static string[] BuildRestoreArguments(string sourceCommit)
        {
            var arguments = new List<string>
            {
                "restore",
                "--source=" + sourceCommit,
                "--staged",
                "--worktree",
                "--"
            };
            arguments.AddRange(ProjectPathspec);
            return arguments.ToArray();
        }

        private static async Task<string> RestoreOriginalAsync(string root, string originalHead)
        {
            var restore = await RunGitAsync(root, BuildRestoreArguments(originalHead), RestoreTimeout);
            return restore.Success
                ? "已恢复到操作前的干净状态。"
                : "自动恢复操作前状态失败，请勿继续 Push，并人工检查：" + FriendlyFailure(restore);
        }

        private static async Task<CommandResult> GitOrThrowAsync(
            string root,
            Action<string> log,
            string[] arguments,
            int timeout = NormalTimeout)
        {
            if (log != null)
                log("$ " + UnityTeamGitProcess.Describe("git", arguments));
            var result = await RunGitAsync(root, arguments, timeout);
            if (!result.Success)
                throw new InvalidOperationException("Git 命令失败。\n" + FriendlyFailure(result));
            return result;
        }

        private static Task<CommandResult> RunGitAsync(string root, string[] arguments, int timeout = NormalTimeout)
        {
            var fullArguments = new List<string> { "-C", root };
            fullArguments.AddRange(arguments);
            return UnityTeamGitProcess.RunAsync(
                UnityTeamGitProcess.FindGitExecutable(),
                fullArguments,
                root,
                timeout);
        }

        private static string FriendlyFailure(CommandResult result)
        {
            if (result.TimedOut)
                return "操作超时。";
            if (!string.IsNullOrWhiteSpace(result.CombinedOutput))
                return result.CombinedOutput;
            return "退出码：" + result.ExitCode;
        }

        private static string ShortHash(string hash)
        {
            if (string.IsNullOrWhiteSpace(hash))
                return string.Empty;
            return hash.Substring(0, Math.Min(12, hash.Length));
        }
    }
}
