using System;
using System.Diagnostics;
using System.Text;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Racso.Z0.Parser;

namespace GitLive
{
    public class GitLiveSync
    {
        private const string EmptyTreeHash = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";
        private const string GitLiveMarker = "// GitLive";
        private const int DivergenceDetected = -2;

        public enum SyncMode
        {
            Incremental,
            Repair,
            Nuke
        }

        public class SyncOptions
        {
            public string OriginalRepoPath { get; set; } = "";
            public string LiveRemoteUrl { get; set; } = "";
            public List<string> IgnoreList { get; set; } = new List<string>();
            public SyncMode Mode { get; set; } = SyncMode.Incremental;
            public bool DryRun { get; set; }
            public ConsoleLogger Logger { get; set; } = new ConsoleLogger(ConsoleLogger.VerbosityLevel.Normal);
        }

        public class SyncResult
        {
            public bool Success { get; set; }
            public int ExitCode { get; set; }
            public string? ErrorMessage { get; set; }
            public int TagsPublished { get; set; }
        }

        public SyncResult Sync(SyncOptions options)
        {
            GitRunner? publisherGit = null;
            string? tmpBranch = null;
            string? tempDir = null;

            try
            {
                var setupResult = SetupPublisherRepo(options.OriginalRepoPath, options.LiveRemoteUrl, out tempDir, out publisherGit, options.Logger);
                if (setupResult != 0)
                    return new SyncResult { Success = false, ExitCode = setupResult, ErrorMessage = "Failed to setup publisher repo" };

                var gp = publisherGit ?? throw new GitException("publisher git runner not initialized");

                var publishedSourceToLiveCommit = DeterminePublishedCommits(gp, out long lastPublishedTs, options.Logger);

                var repoTagsWithData = CollectLocalTags(gp, options.Logger);
                if (repoTagsWithData == null)
                    return new SyncResult { Success = true, ExitCode = 0, TagsPublished = 0 };

                int startIndex = CalculateStartIndex(publishedSourceToLiveCommit, repoTagsWithData, options.Mode, options.Logger, out string? divergenceError);
                if (!string.IsNullOrEmpty(divergenceError))
                    return new SyncResult { Success = false, ExitCode = 5, ErrorMessage = divergenceError };
                if (startIndex < 0)
                    return new SyncResult { Success = true, ExitCode = 0, TagsPublished = 0 };

                var tagsToPublish = repoTagsWithData.Skip(startIndex).Select(t => t.tag).ToList();
                if (tagsToPublish.Count == 0)
                {
                    return new SyncResult { Success = true, ExitCode = 0, TagsPublished = 0 };
                }

                options.Logger.VeryVerbose($"Found {tagsToPublish.Count} tag(s) to publish: {string.Join(", ", tagsToPublish)}");

                var liveMainTip = GetLiveMainTip(gp, options.Mode, options.Logger);
                if (liveMainTip == null && options.Mode != SyncMode.Nuke)
                    return new SyncResult { Success = false, ExitCode = 3, ErrorMessage = "LIVE/main not found" };

                tmpBranch = CreateTemporaryBranch(gp, liveMainTip, options.Mode, options.Logger);

                string? baseForRange = startIndex > 0 ? repoTagsWithData[startIndex - 1].tag : null;
                var createdTagToCommit = PublishTags(gp, repoTagsWithData, startIndex, tmpBranch, ref liveMainTip, 
                    ref baseForRange, options.IgnoreList, options.Mode, options.Logger);
                if (createdTagToCommit == null)
                    return new SyncResult { Success = false, ExitCode = 4, ErrorMessage = "Failed to publish tags" };

                if (!options.DryRun)
                {
                    if (options.Mode == SyncMode.Nuke)
                        DeleteRemoteTags(gp, options.Logger);

                    PushToLiveMain(gp, tmpBranch, options.Mode, options.Logger);
                    PushTags(gp, createdTagToCommit, options.Mode, options.Logger);
                    NormalizeRemoteTags(gp, repoTagsWithData, createdTagToCommit, publishedSourceToLiveCommit, options.Mode, options.Logger);
                }
                else
                {
                    options.Logger.Normal($"DRY RUN: Would push {createdTagToCommit.Count} tag(s) to LIVE remote.");
                }

                return new SyncResult { Success = true, ExitCode = 0, TagsPublished = createdTagToCommit.Count };
            }
            catch (GitException ge)
            {
                return new SyncResult { Success = false, ExitCode = 10, ErrorMessage = "Git error: " + ge.Message };
            }
            catch (Exception ex)
            {
                return new SyncResult { Success = false, ExitCode = 11, ErrorMessage = "ERROR: " + ex.Message };
            }
            finally
            {
                CleanupTemporaryResources(publisherGit, tmpBranch, tempDir);
            }
        }

        private int SetupPublisherRepo(string originalRepoPath, string liveRemoteUrl, 
            out string tempDir, out GitRunner publisherGit, ConsoleLogger logger)
        {
            tempDir = Path.Combine(Path.GetTempPath(), "gitlive-publisher-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(tempDir);

            logger.VeryVerbose($"Created temporary publisher repository at: {tempDir}");

            publisherGit = new GitRunner(tempDir);
            publisherGit.Run("init");
            publisherGit.Run("config user.email \"gitlive@transient.local\"");
            publisherGit.Run("config user.name \"GitLive Publisher\"");

            logger.VeryVerbose("Fetching from original repository...");
            publisherGit.Run($"remote add REPO {EscapeArg(originalRepoPath)}");
            publisherGit.Run("fetch REPO --tags");

            logger.VeryVerbose("Setting up LIVE remote...");
            publisherGit.Run($"remote add LIVE {EscapeArg(liveRemoteUrl)}");

            try
            {
                publisherGit.Run($"remote set-url LIVE {EscapeArg(liveRemoteUrl)}");
            }
            catch
            {
            }

            try
            {
                logger.VeryVerbose("Fetching from LIVE remote...");
                publisherGit.Run("fetch LIVE main --tags");
            }
            catch (Exception)
            {
            }

            var liveRemoteProbe = publisherGit.TryRun("ls-remote LIVE") ?? "";
            if (string.IsNullOrWhiteSpace(liveRemoteProbe))
            {
                return 3;
            }

            return 0;
        }

        private Dictionary<string, string> DeterminePublishedCommits(GitRunner gp, out long lastPublishedTs, ConsoleLogger logger)
        {
            var publishedSourceToLiveCommit = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            lastPublishedTs = -1;

            try
            {
                logger.VeryVerbose("Analyzing commits already published on LIVE/main...");
                var commitsWithTs = gp.TryRun("log --pretty=format:\"%H %ct\" refs/remotes/LIVE/main") ?? "";
                if (string.IsNullOrWhiteSpace(commitsWithTs))
                    return publishedSourceToLiveCommit;

                var lines = commitsWithTs.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var parts = line.Split(' ', 2);
                    if (parts.Length != 2)
                        continue;

                    var liveCommit = parts[0].Trim();
                    long ts = long.TryParse(parts[1].Trim(), out long parsedTs) ? parsedTs : -1;

                    string body;
                    try
                    {
                        body = gp.Run($"log -1 --format=\"%B\" {EscapeArg(liveCommit)}");
                    }
                    catch
                    {
                        continue;
                    }

                    try
                    {
                        var z0Section = body;
                        var markerIndex = body.IndexOf(GitLiveMarker);
                        if (markerIndex >= 0)
                        {
                            z0Section = body[markerIndex..];
                        }
                        
                        var node = Z0.Parse(z0Section).AsZNode();
                        if (!node["commit"])
                            continue;

                        var sourceCommitFull = node["commit"].Optional().Trim();
                        if (string.IsNullOrEmpty(sourceCommitFull))
                            continue;

                        if (!publishedSourceToLiveCommit.ContainsKey(sourceCommitFull))
                            publishedSourceToLiveCommit[sourceCommitFull] = liveCommit;

                        if (ts > lastPublishedTs)
                            lastPublishedTs = ts;
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            catch (GitException)
            {
            }

            return publishedSourceToLiveCommit;
        }

        private List<(string tag, long ts, string sourceFull, string sourceShort)>? CollectLocalTags(GitRunner gp, ConsoleLogger logger)
        {
            logger.VeryVerbose("Collecting local live/* tags...");
            var repoMergedTagsOutput = gp.TryRun("tag --list live/*") ?? "";
            var repoMergedTags = repoMergedTagsOutput.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim()).Where(t => t.Length > 0).ToList();

            if (repoMergedTags.Count == 0)
            {
                logger.VeryVerbose("No live/* tags found.");
                return null;
            }

            var repoTagsWithData = new List<(string tag, long ts, string sourceFull, string sourceShort)>();
            foreach (var tag in repoMergedTags)
            {
                var tsStr = gp.Run($"log -1 --format=\"%ct\" {EscapeArg(tag)}");
                if (!long.TryParse(tsStr.Trim(), out long ts))
                {
                    continue;
                }

                string sourceFull;
                try
                {
                    sourceFull = gp.Run($"rev-parse {EscapeArg(tag)}^{{}}").Trim();
                }
                catch (Exception)
                {
                    continue;
                }

                string sourceShort = gp.TryRun($"rev-parse --short {EscapeArg(tag)}^{{}}")?.Trim() 
                    ?? (sourceFull.Length >= 7 ? sourceFull.Substring(0, 7) : sourceFull);

                repoTagsWithData.Add((tag, ts, sourceFull, sourceShort));
            }

            if (repoTagsWithData.Count == 0)
            {
                return null;
            }

            return repoTagsWithData.OrderBy(t => t.ts).ToList();
        }

        private int CalculateStartIndex(Dictionary<string, string> publishedSourceToLiveCommit, 
            List<(string tag, long ts, string sourceFull, string sourceShort)> repoTagsWithData, 
            SyncMode mode, ConsoleLogger logger, out string? errorMessage)
        {
            errorMessage = null;

            if (mode == SyncMode.Nuke)
            {
                logger.VeryVerbose("Nuke mode: starting from first tag.");
                return 0;
            }

            if (publishedSourceToLiveCommit.Count == 0)
            {
                logger.VeryVerbose("No previously published commits found, starting from first tag.");
                return 0;
            }

            if (mode == SyncMode.Repair)
            {
                int idx = repoTagsWithData.FindIndex(t => !publishedSourceToLiveCommit.ContainsKey(t.sourceFull));
                if (idx < 0)
                {
                    logger.VeryVerbose("Repair mode: all tags already published.");
                    return -1;
                }
                logger.VeryVerbose($"Repair mode: starting from tag index {idx} ({repoTagsWithData[idx].tag}).");
                return idx;
            }

            // Incremental mode: check for divergence
            int lastIdx = -1;
            for (int i = 0; i < repoTagsWithData.Count; i++)
            {
                if (publishedSourceToLiveCommit.ContainsKey(repoTagsWithData[i].sourceFull))
                    lastIdx = i;
            }

            // In Incremental mode, if we found some published commits but not all consecutive ones,
            // it means there's a divergence
            if (lastIdx >= 0)
            {
                // Check if there are any gaps - any unpublished commit before the last published one
                for (int i = 0; i <= lastIdx; i++)
                {
                    if (!publishedSourceToLiveCommit.ContainsKey(repoTagsWithData[i].sourceFull))
                    {
                        // Found a gap - this is divergence
                        errorMessage = $"Incremental mode detected divergence: Base repository tag '{repoTagsWithData[i].tag}' (commit {repoTagsWithData[i].sourceShort}) " +
                            $"is not found in LIVE repository, but later commits are. This indicates the repositories have diverged. " +
                            $"Use --repair mode to resync from the divergence point, or --nuke to force a complete resync.";
                        logger.VeryVerbose($"Incremental mode: divergence detected at tag index {i}.");
                        return DivergenceDetected;
                    }
                }
            }

            logger.VeryVerbose($"Incremental mode: starting from tag index {lastIdx + 1}.");
            return lastIdx + 1;
        }

        private string? GetLiveMainTip(GitRunner gp, SyncMode mode, ConsoleLogger logger)
        {
            if (mode == SyncMode.Nuke)
            {
                logger.VeryVerbose("Nuke mode: not using existing LIVE/main tip.");
                return null;
            }

            try
            {
                var liveMainTip = gp.Run("rev-parse refs/remotes/LIVE/main").Trim();
                logger.VeryVerbose($"Found LIVE/main tip: {liveMainTip}");
                return liveMainTip;
            }
            catch (GitException)
            {
                logger.VeryVerbose("LIVE/main not found.");
                return null;
            }
        }

        private string CreateTemporaryBranch(GitRunner gp, string? liveMainTip, SyncMode mode, ConsoleLogger logger)
        {
            var tmpBranch = $"tmp-sync-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}-{Process.GetCurrentProcess().Id}";
            logger.VeryVerbose($"Creating temporary branch: {tmpBranch}");

            if (mode != SyncMode.Nuke)
                gp.Run($"update-ref refs/heads/{tmpBranch} {liveMainTip}");

            return tmpBranch;
        }

        private Dictionary<string, string>? PublishTags(GitRunner gp, 
            List<(string tag, long ts, string sourceFull, string sourceShort)> repoTagsWithData, 
            int startIndex, string tmpBranch, ref string? currentParent, ref string? baseForRange, 
            List<string> ignoreList, SyncMode mode, ConsoleLogger logger)
        {
            var createdTagToCommit = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (int i = startIndex; i < repoTagsWithData.Count; i++)
            {
                var tag = repoTagsWithData[i].tag;
                var sourceFull = repoTagsWithData[i].sourceFull;
                var sourceShort = repoTagsWithData[i].sourceShort;
                
                logger.Verbose($"Processing tag {tag} (commit {sourceShort})...");

                var treeId = gp.Run($"rev-parse {EscapeArg(tag)}^{{tree}}").Trim();
                if (string.IsNullOrEmpty(treeId))
                {
                    return null;
                }

                if (ignoreList.Count > 0)
                {
                    treeId = FilterTree(gp, treeId, ignoreList);
                }

                var commitCount = CalculateCommitCount(gp, tag, baseForRange);
                var commitMessage = BuildCommitMessage(gp, tag, sourceFull, sourceShort, baseForRange, commitCount);

                string commitTreeArgs = (mode == SyncMode.Nuke && i == startIndex)
                    ? $"commit-tree {EscapeArg(treeId)}"
                    : $"commit-tree {EscapeArg(treeId)} -p {EscapeArg(currentParent)}";

                var newCommit = gp.RunWithInput(commitTreeArgs, commitMessage).Trim();
                if (string.IsNullOrEmpty(newCommit))
                {
                    return null;
                }

                gp.Run($"update-ref refs/heads/{tmpBranch} {EscapeArg(newCommit)}");
                currentParent = newCommit;
                gp.Run($"tag -f {EscapeArg(tag)} {EscapeArg(newCommit)}");

                createdTagToCommit[tag] = newCommit;
                baseForRange = tag;
            }

            return createdTagToCommit;
        }

        private string BuildCommitMessage(GitRunner gp, string tag, string sourceFull, 
            string sourceShort, string? baseForRange, int commitCount)
        {
            var sbMsg = new StringBuilder();
            var displayTag = tag.StartsWith("live/") ? tag.Substring("live/".Length) : tag;
            var subject = $"GitLive: publish {displayTag} commit {sourceShort}";
            sbMsg.AppendLine(subject);
            sbMsg.AppendLine();
            sbMsg.AppendLine("// GitLive");
            sbMsg.AppendLine($"commit = {sourceFull}");
            sbMsg.AppendLine($"tag = {tag}");
            sbMsg.AppendLine($"date = {DateTimeOffset.UtcNow:O}");
            sbMsg.AppendLine($"commit-count = {commitCount}");
            return sbMsg.ToString();
        }

        private int CalculateCommitCount(GitRunner gp, string tag, string? baseForRange)
        {
            try
            {
                string logRange;
                if (!string.IsNullOrEmpty(baseForRange))
                {
                    var baseExists = gp.TryRun($"rev-parse --verify {EscapeArg(baseForRange)}");
                    logRange = baseExists == null
                        ? gp.Run($"log -1 --pretty=format:\"%H\" {EscapeArg(tag)}")
                        : gp.Run($"log --pretty=format:\"%H\" --reverse {EscapeArg(baseForRange)}..{EscapeArg(tag)}");
                }
                else
                {
                    logRange = gp.Run($"log -1 --pretty=format:\"%H\" {EscapeArg(tag)}");
                }

                if (!string.IsNullOrWhiteSpace(logRange))
                {
                    var lines = logRange.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    return lines.Length;
                }
            }
            catch
            {
            }

            return 0;
        }

        private void DeleteRemoteTags(GitRunner gp, ConsoleLogger logger)
        {
            logger.Verbose("NUKE mode: Deleting existing tags on LIVE remote...");
            var remoteTagsOutput = gp.TryRun("ls-remote --tags LIVE") ?? "";
            var remoteTagNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (var line in remoteTagsOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\t');
                if (parts.Length < 2)
                    continue;

                var refName = parts[1];
                if (!refName.StartsWith("refs/tags/"))
                    continue;

                var tagName = refName.Substring("refs/tags/".Length);
                if (tagName.EndsWith("^{}"))
                    tagName = tagName.Substring(0, tagName.Length - 3);

                remoteTagNames.Add(tagName);
            }

            foreach (var tagName in remoteTagNames)
            {
                try
                {
                    logger.VeryVerbose($"Deleting remote tag: {tagName}");
                    gp.Run($"push LIVE --delete {EscapeArg(tagName)}");
                }
                catch (Exception)
                {
                }
            }
        }

        private void PushToLiveMain(GitRunner gp, string tmpBranch, SyncMode mode, ConsoleLogger logger)
        {
            logger.Verbose("Pushing to LIVE/main...");
            // Nuke and Repair modes use force push to handle rewriting history
            var pushBranchCmd = (mode == SyncMode.Nuke || mode == SyncMode.Repair)
                ? $"push LIVE +refs/heads/{EscapeArg(tmpBranch)}:refs/heads/main"
                : $"push LIVE refs/heads/{EscapeArg(tmpBranch)}:refs/heads/main";
            gp.Run(pushBranchCmd);
        }

        private void PushTags(GitRunner gp, Dictionary<string, string> createdTagToCommit, SyncMode mode, ConsoleLogger logger)
        {
            logger.Verbose($"Pushing {createdTagToCommit.Count} tag(s) to LIVE...");
            foreach (var kv in createdTagToCommit)
            {
                var localTag = kv.Key;
                var commit = kv.Value;
                var remoteTag = localTag.StartsWith("live/") ? localTag.Substring("live/".Length) : localTag;

                logger.VeryVerbose($"Pushing tag: {localTag} -> {remoteTag}");
                // Nuke and Repair modes use force push to handle rewriting history
                var pushTagCmd = (mode == SyncMode.Nuke || mode == SyncMode.Repair)
                    ? $"push LIVE +refs/tags/{EscapeArg(localTag)}:refs/tags/{EscapeArg(remoteTag)}"
                    : $"push LIVE refs/tags/{EscapeArg(localTag)}:refs/tags/{EscapeArg(remoteTag)}";
                gp.Run(pushTagCmd);
            }
        }

        private void NormalizeRemoteTags(GitRunner gp, 
            List<(string tag, long ts, string sourceFull, string sourceShort)> repoTagsWithData, 
            Dictionary<string, string> createdTagToCommit, 
            Dictionary<string, string> publishedSourceToLiveCommit, SyncMode mode, ConsoleLogger logger)
        {
            logger.VeryVerbose("Normalizing remote tags...");
            foreach (var t in repoTagsWithData)
            {
                var localTag = t.tag;
                var remoteTag = localTag.StartsWith("live/") ? localTag.Substring("live/".Length) : localTag;

                var ls = gp.TryRun($"ls-remote --tags LIVE {EscapeArg(remoteTag)}") ?? "";
                if (!string.IsNullOrWhiteSpace(ls))
                    continue;

                if (createdTagToCommit.TryGetValue(localTag, out var createdCommit))
                {
                    try
                    {
                        logger.VeryVerbose($"Pushing missing tag: {remoteTag}");
                        // Nuke and Repair modes use force push to handle rewriting history
                        var pushMissingTagCmd = (mode == SyncMode.Nuke || mode == SyncMode.Repair)
                            ? $"push LIVE +refs/tags/{EscapeArg(localTag)}:refs/tags/{EscapeArg(remoteTag)}"
                            : $"push LIVE refs/tags/{EscapeArg(localTag)}:refs/tags/{EscapeArg(remoteTag)}";
                        gp.Run(pushMissingTagCmd);
                        continue;
                    }
                    catch (Exception)
                    {
                    }
                }

                if (publishedSourceToLiveCommit.TryGetValue(t.sourceFull, out var liveCommitSha))
                {
                    try
                    {
                        logger.VeryVerbose($"Pushing existing commit as tag: {remoteTag}");
                        // Nuke and Repair modes use force push to handle rewriting history
                        var pushExistingTagCmd = (mode == SyncMode.Nuke || mode == SyncMode.Repair)
                            ? $"push LIVE +{EscapeArg(liveCommitSha)}:refs/tags/{EscapeArg(remoteTag)}"
                            : $"push LIVE {EscapeArg(liveCommitSha)}:refs/tags/{EscapeArg(remoteTag)}";
                        gp.Run(pushExistingTagCmd);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }

        private void CleanupTemporaryResources(GitRunner? publisherGit, string? tmpBranch, string? tempDir)
        {
            try
            {
                if (publisherGit != null && !string.IsNullOrEmpty(tmpBranch))
                {
                    try
                    {
                        publisherGit.TryRun($"update-ref -d refs/heads/{tmpBranch}");
                    }
                    catch (Exception)
                    {
                    }
                }

                if (publisherGit != null)
                {
                    var tmpList = publisherGit.TryRun("for-each-ref --format=%(refname:short) refs/heads/tmp-sync-*") ?? "";
                    if (!string.IsNullOrWhiteSpace(tmpList))
                    {
                        var lines = tmpList.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var b in lines)
                        {
                            try
                            {
                                publisherGit.TryRun($"update-ref -d refs/heads/{EscapeArg(b)}");
                            }
                            catch
                            {
                            }
                        }
                    }
                }

                if (!string.IsNullOrEmpty(tempDir))
                {
                    try
                    {
                        SafeDeleteDirectory(tempDir);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            catch
            {
            }
        }

        private void SafeDeleteDirectory(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return;

            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    ClearReadOnlyAttributes(dir);
                    Directory.Delete(dir, true);
                    return;
                }
                catch
                {
                    try
                    {
                        System.Threading.Thread.Sleep(200);
                    }
                    catch
                    {
                    }
                }
            }

            try
            {
                ClearReadOnlyAttributes(dir);
                Directory.Delete(dir, true);
            }
            catch
            {
            }
        }

        private void ClearReadOnlyAttributes(string dir)
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                catch
                {
                }
            }

            foreach (var sub in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var di = new DirectoryInfo(sub);
                    di.Attributes = FileAttributes.Directory;
                }
                catch
                {
                }
            }
        }

        private string FilterTree(GitRunner git, string treeId, List<string> ignoreList)
        {
            git.Run($"read-tree {EscapeArg(treeId)}");
            git.Run("checkout-index -a -f");

            foreach (var ignorePath in ignoreList)
            {
                if (ignorePath.Contains("..") || Path.IsPathRooted(ignorePath))
                    continue;

                string fullPath = Path.Combine(git.WorkingDirectory ?? ".", ignorePath);
                if (Directory.Exists(fullPath))
                    Directory.Delete(fullPath, true);
                else if (File.Exists(fullPath))
                    File.Delete(fullPath);
            }

            git.Run("add -A");
            var newTreeId = git.Run("write-tree").Trim();
            git.Run("reset --hard");

            return newTreeId;
        }

        private string EscapeArg(string? s)
        {
            if (s == null)
                return "";
            if (s.Length == 0)
                return "\"\"";
            if (s.Any(c => char.IsWhiteSpace(c) || c == '\"'))
            {
                var escaped = s.Replace("\"", "\\\"");
                return $"\"{escaped}\"";
            }

            return s;
        }
    }

    public class GitRunner
    {
        private readonly string? workingDir;

        public string? WorkingDirectory => workingDir;

        public GitRunner(string? workingDirectory = null)
        {
            workingDir = workingDirectory;
        }

        public string Run(string args)
        {
            var res = RunInternal(args, null, throwOnError: true);
            return res.stdout;
        }

        public string? TryRun(string args)
        {
            try
            {
                return Run(args);
            }
            catch (GitException)
            {
                return null;
            }
        }

        public string RunWithInput(string args, string input)
        {
            var res = RunInternal(args, input, throwOnError: true);
            return res.stdout;
        }

        (string stdout, string stderr) RunInternal(string args, string? stdin, bool throwOnError)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = stdin != null,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            if (!string.IsNullOrEmpty(workingDir))
                psi.WorkingDirectory = workingDir;

            var proc = Process.Start(psi);
            if (proc == null)
                throw new GitException("Failed to start git process");

            using (proc)
            {
                if (stdin != null)
                {
                    proc.StandardInput.Write(stdin);
                    proc.StandardInput.Close();
                }

                var outTask = proc.StandardOutput.ReadToEndAsync();
                var errTask = proc.StandardError.ReadToEndAsync();

                proc.WaitForExit();

                var stdout = outTask.Result ?? "";
                var stderr = errTask.Result ?? "";

                if (proc.ExitCode != 0 && throwOnError)
                {
                    var msg = $"git {args} exited {proc.ExitCode}\nstdout:\n{stdout}\nstderr:\n{stderr}";
                    throw new GitException(msg);
                }

                return (stdout, stderr);
            }
        }
    }

    public class GitException : Exception
    {
        public GitException(string m) : base(m)
        {
        }
    }
}
