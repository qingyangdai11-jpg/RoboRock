using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Blackbox.UnityTeamGit.Editor
{
    [Serializable]
    internal sealed class UnityTeamGitSettings
    {
        private const string PrefsPrefix = "Blackbox.UnityTeamGit.";
        private static readonly Regex SafeNamePattern = new Regex("^[A-Za-z0-9._-]+$", RegexOptions.Compiled);

        public string owner = "blackbox-admin";
        public string lanHost = "192.168.28.25";
        public int sshPort = 36401;
        public string tailnetHost = "unity-git.tailf97c34.ts.net";
        public string lanWebBaseUrl = "http://192.168.28.25:41969";
        public string webBaseUrl = "https://unity-git.tailf97c34.ts.net";
        public string defaultBranch = "main";
        public string initialCommitMessage = "Initial Unity project";
        public string sshPrivateKeyPath = DefaultSshPrivateKeyPath();

        public string ProjectRoot
        {
            get { return Path.GetFullPath(Path.Combine(Application.dataPath, "..")); }
        }

        public string RepositoryName
        {
            get { return new DirectoryInfo(ProjectRoot).Name; }
        }

        public string OriginUrl
        {
            get { return BuildSshUrl(lanHost); }
        }

        public string TailnetUrl
        {
            get { return BuildSshUrl(tailnetHost); }
        }

        public string LanWebRepositoryUrl
        {
            get
            {
                return lanWebBaseUrl.TrimEnd('/') + "/" + owner + "/" + RepositoryName;
            }
        }

        public string TailnetWebRepositoryUrl
        {
            get
            {
                return webBaseUrl.TrimEnd('/') + "/" + owner + "/" + RepositoryName;
            }
        }

        public static UnityTeamGitSettings Load()
        {
            var settings = new UnityTeamGitSettings
            {
                owner = EditorPrefs.GetString(PrefsPrefix + "Owner", "blackbox-admin"),
                lanHost = EditorPrefs.GetString(PrefsPrefix + "LanHost", "192.168.28.25"),
                sshPort = EditorPrefs.GetInt(PrefsPrefix + "SshPort", 36401),
                tailnetHost = EditorPrefs.GetString(PrefsPrefix + "TailnetHost", "unity-git.tailf97c34.ts.net"),
                lanWebBaseUrl = EditorPrefs.GetString(PrefsPrefix + "LanWebBaseUrl", "http://192.168.28.25:41969"),
                webBaseUrl = EditorPrefs.GetString(PrefsPrefix + "WebBaseUrl", "https://unity-git.tailf97c34.ts.net"),
                defaultBranch = EditorPrefs.GetString(PrefsPrefix + "DefaultBranch", "main"),
                initialCommitMessage = EditorPrefs.GetString(PrefsPrefix + "InitialCommitMessage", "Initial Unity project"),
                sshPrivateKeyPath = EditorPrefs.GetString(PrefsPrefix + "SshPrivateKeyPath", DefaultSshPrivateKeyPath())
            };

            return settings;
        }

        public void Save()
        {
            EditorPrefs.SetString(PrefsPrefix + "Owner", owner.Trim());
            EditorPrefs.SetString(PrefsPrefix + "LanHost", lanHost.Trim());
            EditorPrefs.SetInt(PrefsPrefix + "SshPort", sshPort);
            EditorPrefs.SetString(PrefsPrefix + "TailnetHost", tailnetHost.Trim());
            EditorPrefs.SetString(PrefsPrefix + "LanWebBaseUrl", lanWebBaseUrl.Trim());
            EditorPrefs.SetString(PrefsPrefix + "WebBaseUrl", webBaseUrl.Trim());
            EditorPrefs.SetString(PrefsPrefix + "DefaultBranch", defaultBranch.Trim());
            EditorPrefs.SetString(PrefsPrefix + "InitialCommitMessage", initialCommitMessage.Trim());
            EditorPrefs.SetString(PrefsPrefix + "SshPrivateKeyPath", sshPrivateKeyPath.Trim());
        }

        public IEnumerable<string> Validate()
        {
            if (!SafeNamePattern.IsMatch(RepositoryName))
                yield return "工程文件夹名只能包含英文字母、数字、点、下划线和连字符。";

            if (string.IsNullOrWhiteSpace(owner) || !SafeNamePattern.IsMatch(owner.Trim()))
                yield return "Gitea 用户或组织名格式无效。";

            if (string.IsNullOrWhiteSpace(lanHost))
                yield return "局域网主机不能为空。";

            if (sshPort < 1 || sshPort > 65535)
                yield return "SSH 端口必须位于 1 到 65535 之间。";

            if (string.IsNullOrWhiteSpace(tailnetHost))
                yield return "Tailnet 主机不能为空。";

            if (!IsHttpUrl(lanWebBaseUrl))
                yield return "Gitea 局域网 Web 地址必须是有效的 HTTP(S) 地址。";

            if (!IsHttpUrl(webBaseUrl))
                yield return "Gitea Tailscale Web 地址必须是有效的 HTTP(S) 地址。";

            if (string.IsNullOrWhiteSpace(defaultBranch) || !SafeNamePattern.IsMatch(defaultBranch.Trim()))
                yield return "默认分支名格式无效。";

            if (string.IsNullOrWhiteSpace(initialCommitMessage))
                yield return "首次提交说明不能为空。";

            if (string.IsNullOrWhiteSpace(sshPrivateKeyPath))
                yield return "SSH 私钥路径不能为空。";
        }

        private string BuildSshUrl(string host)
        {
            return "ssh://git@" + host.Trim() + ":" + sshPort + "/" + owner.Trim() + "/" + RepositoryName + ".git";
        }

        private static bool IsHttpUrl(string value)
        {
            Uri uri;
            return Uri.TryCreate(value, UriKind.Absolute, out uri) &&
                   (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        private static string DefaultSshPrivateKeyPath()
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(profile, ".ssh", "id_ed25519_gitea_unity");
        }
    }
}
