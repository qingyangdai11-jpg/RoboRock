using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Blackbox.UnityTeamGit.Editor
{
    internal sealed class GitFileRevision
    {
        public string Hash { get; set; }
        public string ShortHash { get; set; }
        public string Date { get; set; }
        public string Author { get; set; }
        public string Subject { get; set; }
    }

    internal static class UnityTeamGitFileRestore
    {
        private const int NormalTimeout = 120000;
        private const string RestorePreviousMenu = "Assets/Unity Team Git/将该文件恢复到上个 Commit 版本";
        private const string RestoreSelectedMenu = "Assets/Unity Team Git/将该文件恢复到指定 Commit 版本…";

        [MenuItem(RestorePreviousMenu, priority = 2000)]
        private static async void RestorePreviousCommit()
        {
            var assetPath = GetSelectedCodeAssetPath();
            if (string.IsNullOrEmpty(assetPath))
                return;
            if (!UnityTeamGitOperationGate.TryEnter())
            {
                EditorUtility.DisplayDialog("无法恢复文件", "另一个 Git 操作正在执行，请稍后重试。", "确定");
                return;
            }

            try
            {
                if (EditorUnsafe())
                    throw new InvalidOperationException("Unity 正在编译、导入或进入 Play Mode，文件恢复已停止。");
                var root = ProjectRoot();
                var previous = await RunGitAsync(root, new[] { "rev-parse", "HEAD^" });
                if (!previous.Success || string.IsNullOrWhiteSpace(previous.StandardOutput))
                    throw new InvalidOperationException("当前 Branch 没有可用的上一个 Commit。");
                await RestoreToCommitCoreAsync(assetPath, previous.StandardOutput.Trim(), "上个 Commit（HEAD^）");
            }
            catch (Exception exception)
            {
                EditorUtility.DisplayDialog("无法恢复文件", exception.Message, "确定");
            }
            finally
            {
                UnityTeamGitOperationGate.Exit();
            }
        }

        [MenuItem(RestorePreviousMenu, true)]
        [MenuItem(RestoreSelectedMenu, true)]
        private static bool ValidateCodeFileMenu()
        {
            return !string.IsNullOrEmpty(GetSelectedCodeAssetPath()) &&
                   Directory.Exists(Path.Combine(ProjectRoot(), ".git"));
        }

        [MenuItem(RestoreSelectedMenu, priority = 2001)]
        private static void RestoreSelectedCommit()
        {
            var assetPath = GetSelectedCodeAssetPath();
            if (!string.IsNullOrEmpty(assetPath))
                UnityTeamGitFileHistoryWindow.ShowWindow(assetPath);
        }

        internal static async Task<List<GitFileRevision>> LoadHistoryAsync(string assetPath)
        {
            if (!UnityTeamGitOperationGate.TryEnter())
                throw new InvalidOperationException("另一个 Git 操作正在执行，请稍后重试。");

            try
            {
                ValidateAssetPath(assetPath);
                var root = ProjectRoot();
                await EnsureTrackedAsync(root, assetPath);

                var result = await RunGitAsync(
                    root,
                    new[]
                    {
                        "log",
                        "--max-count=100",
                        "--date=iso-strict",
                        "--format=%H%x09%h%x09%ad%x09%an%x09%s",
                        "--",
                        assetPath
                    });
                if (!result.Success)
                    throw new InvalidOperationException("无法读取文件历史。" + Environment.NewLine + FriendlyFailure(result));

                var revisions = new List<GitFileRevision>();
                foreach (var line in result.StandardOutput.Replace("\r\n", "\n").Split('\n'))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;
                    var parts = line.Split('\t');
                    if (parts.Length < 5)
                        continue;
                    revisions.Add(new GitFileRevision
                    {
                        Hash = parts[0],
                        ShortHash = parts[1],
                        Date = parts[2],
                        Author = parts[3],
                        Subject = string.Join("\t", parts.Skip(4).ToArray())
                    });
                }
                return revisions;
            }
            finally
            {
                UnityTeamGitOperationGate.Exit();
            }
        }

        internal static async Task RestoreToCommitAsync(string assetPath, string commitHash, string commitLabel)
        {
            ValidateAssetPath(assetPath);
            if (string.IsNullOrWhiteSpace(commitHash))
                throw new InvalidOperationException("Commit 不能为空。");
            if (!UnityTeamGitOperationGate.TryEnter())
                throw new InvalidOperationException("另一个 Git 操作正在执行，请稍后重试。");

            try
            {
                await RestoreToCommitCoreAsync(assetPath, commitHash, commitLabel);
            }
            finally
            {
                UnityTeamGitOperationGate.Exit();
            }
        }

        private static async Task RestoreToCommitCoreAsync(string assetPath, string commitHash, string commitLabel)
        {
            ValidateAssetPath(assetPath);
            if (string.IsNullOrWhiteSpace(commitHash))
                throw new InvalidOperationException("Commit 不能为空。");
            if (EditorUnsafe())
                throw new InvalidOperationException("Unity 正在编译、导入或进入 Play Mode，文件恢复已停止。");

            var root = ProjectRoot();
            await EnsureTrackedAsync(root, assetPath);
            await EnsureNotStagedOrConflictedAsync(root, assetPath);

            var commit = await RunGitAsync(root, new[] { "rev-parse", "--verify", commitHash + "^{commit}" });
            if (!commit.Success)
                throw new InvalidOperationException("指定 Commit 无效。" + Environment.NewLine + FriendlyFailure(commit));
            var resolvedCommit = commit.StandardOutput.Trim();

            var exists = await RunGitAsync(root, new[] { "cat-file", "-e", resolvedCommit + ":" + assetPath });
            if (!exists.Success)
                throw new InvalidOperationException("该 Commit 中不存在文件：" + assetPath);

            if (!EditorUtility.DisplayDialog(
                    "确认恢复单个文件",
                    "文件：" + assetPath + "\n来源：" + commitLabel + "\nCommit：" + resolvedCommit.Substring(0, Math.Min(12, resolvedCommit.Length)) +
                    "\n\n这不会移动 Branch 或删除 Commit，但会覆盖该文件当前未提交的内容。覆盖前会创建本地备份。",
                    "确认恢复",
                    "取消"))
                return;

            var backup = BackupCurrentFile(root, assetPath);
            var restore = await RunGitAsync(root, new[] { "restore", "--source=" + resolvedCommit, "--worktree", "--", assetPath });
            if (!restore.Success)
                throw new InvalidOperationException("文件恢复失败。" + Environment.NewLine + FriendlyFailure(restore));

            EditorUtility.DisplayDialog(
                "文件已恢复",
                "已从 " + commitLabel + " 恢复：\n" + assetPath +
                "\n\nBranch 和 Commit 历史没有改变。该文件现在是工作区修改，请检查后再提交。" +
                "\n\n恢复前备份：\n" + backup,
                "确定");
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
        }

        private static async Task EnsureTrackedAsync(string root, string assetPath)
        {
            var tracked = await RunGitAsync(root, new[] { "ls-files", "--error-unmatch", "--", assetPath });
            if (!tracked.Success)
                throw new InvalidOperationException("该文件尚未被 Git 跟踪，无法从 Commit 恢复：" + assetPath);
        }

        private static async Task EnsureNotStagedOrConflictedAsync(string root, string assetPath)
        {
            var conflicts = await RunGitAsync(root, new[] { "ls-files", "-u", "--", assetPath });
            if (!conflicts.Success)
                throw new InvalidOperationException("无法检查文件冲突状态。" + Environment.NewLine + FriendlyFailure(conflicts));
            if (!string.IsNullOrWhiteSpace(conflicts.StandardOutput))
                throw new InvalidOperationException("该文件存在未解决冲突，请先处理冲突再恢复。");

            var staged = await RunGitAsync(root, new[] { "diff", "--cached", "--quiet", "--", assetPath });
            if (staged.ExitCode == 1)
                throw new InvalidOperationException("该文件已有暂存修改。为避免索引与工作区内容不一致，请先取消暂存再恢复。");
            if (staged.ExitCode != 0)
                throw new InvalidOperationException("无法检查文件暂存状态。" + Environment.NewLine + FriendlyFailure(staged));
        }

        private static string BackupCurrentFile(string root, string assetPath)
        {
            var source = ResolveAssetFullPath(root, assetPath);
            if (!File.Exists(source))
                throw new FileNotFoundException("找不到当前文件，无法创建恢复前备份。", source);

            var backupRoot = Path.Combine(
                root,
                "Library",
                "UnityTeamGit",
                "FileRestoreBackup",
                DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
            var destination = Path.Combine(backupRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            File.Copy(source, destination, false);
            return destination;
        }

        private static void ValidateAssetPath(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath) || !assetPath.StartsWith("Assets/", StringComparison.Ordinal))
                throw new InvalidOperationException("只能恢复当前 Unity 工程 Assets 下的代码文件。");
            if (!string.Equals(Path.GetExtension(assetPath), ".cs", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("当前版本只支持恢复 C#（.cs）代码文件。");
            ResolveAssetFullPath(ProjectRoot(), assetPath);
        }

        private static string ResolveAssetFullPath(string root, string assetPath)
        {
            var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullPath = Path.GetFullPath(Path.Combine(rootFull, assetPath.Replace('/', Path.DirectorySeparatorChar)));
            var prefix = rootFull + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("文件路径超出 Unity 工程范围。");
            return fullPath;
        }

        private static string GetSelectedCodeAssetPath()
        {
            var path = AssetDatabase.GetAssetPath(Selection.activeObject);
            if (string.IsNullOrWhiteSpace(path) ||
                !path.StartsWith("Assets/", StringComparison.Ordinal) ||
                !string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase) ||
                AssetDatabase.IsValidFolder(path))
                return string.Empty;
            return path.Replace('\\', '/');
        }

        private static string ProjectRoot()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        }

        private static Task<CommandResult> RunGitAsync(string root, string[] arguments)
        {
            var fullArguments = new List<string> { "-C", root };
            fullArguments.AddRange(arguments);
            return UnityTeamGitProcess.RunAsync(
                UnityTeamGitProcess.FindGitExecutable(),
                fullArguments,
                root,
                NormalTimeout);
        }

        private static bool EditorUnsafe()
        {
            return EditorApplication.isCompiling ||
                   EditorApplication.isUpdating ||
                   EditorApplication.isPlayingOrWillChangePlaymode;
        }

        private static string FriendlyFailure(CommandResult result)
        {
            if (result.TimedOut)
                return "操作超时。";
            if (!string.IsNullOrWhiteSpace(result.CombinedOutput))
                return result.CombinedOutput;
            return "退出码：" + result.ExitCode;
        }
    }
}
