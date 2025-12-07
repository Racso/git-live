using Racso.Z0.Parser;

namespace GitLive
{
    class Program
    {
        static int Main(string[] args)
        {
            ConsoleLogger.VerbosityLevel verbosity = DetermineVerbosity(args);
            ConsoleLogger logger = new ConsoleLogger(verbosity);
            GitRunner git = new GitRunner();

            try
            {
                string? originalRepoPath = DetectRepository(git, logger);
                if (originalRepoPath == null)
                    return 1;

                ZNode config = GetConfig("gitlive");

                List<string> ignoreList = LoadIgnoreList(config, logger);

                string? liveRemoteUrl = GetLiveRemoteUrl(args, config, git, logger);
                if (liveRemoteUrl == null)
                    return 2;

                liveRemoteUrl = NormalizeLiveUrl(liveRemoteUrl, logger);

                GitLiveSync.SyncMode mode = DetermineSyncMode(args, out bool dryRun, logger);

                GitLiveSync.SyncOptions syncOptions = new GitLiveSync.SyncOptions
                {
                    OriginalRepoPath = originalRepoPath,
                    LiveRemoteUrl = liveRemoteUrl,
                    IgnoreList = ignoreList,
                    Mode = mode,
                    DryRun = dryRun,
                    Logger = logger
                };

                GitLiveSync gitLiveSync = new GitLiveSync();
                GitLiveSync.SyncResult result = gitLiveSync.Sync(syncOptions);

                if (!result.Success)
                {
                    if (!string.IsNullOrEmpty(result.ErrorMessage))
                        logger.Error(result.ErrorMessage);
                    return result.ExitCode;
                }

                if (result.TagsPublished == 0)
                    logger.Normal("No new tags to publish.");
                else
                    logger.Normal($"\nSync completed successfully. Published {result.TagsPublished} tag(s).");

                return 0;
            }
            catch (Exception ex)
            {
                logger.Error("ERROR: " + ex.Message);
                return 11;
            }
        }

        private static ZNode GetConfig(string fileToRead)
        {
            try
            {
                string content = File.ReadAllText(fileToRead);
                ParsingNode parsed = Z0.Parse(content);
                return parsed.AsZNode();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Warning: Failed to parse config file '{fileToRead}': {ex.Message}");
                return ParsingNode.NewRootNode().AsZNode();
            }
        }

        static ConsoleLogger.VerbosityLevel DetermineVerbosity(string[] args)
        {
            // Check for very-verbose first to ensure it takes precedence over verbose
            if (args.Any(a => a == "-vv" || a == "--very-verbose"))
                return ConsoleLogger.VerbosityLevel.VeryVerbose;
            if (args.Any(a => a == "-v" || a == "--verbose"))
                return ConsoleLogger.VerbosityLevel.Verbose;
            return ConsoleLogger.VerbosityLevel.Normal;
        }

        static string? DetectRepository(GitRunner git, ConsoleLogger logger)
        {
            git.Run("rev-parse --git-dir");
            logger.Verbose("Detected git repository.");

            string originalRepoPath = git.Run("rev-parse --show-toplevel").Trim();
            if (string.IsNullOrEmpty(originalRepoPath))
            {
                logger.Error("ERROR: cannot determine repository top-level path.");
                return null;
            }

            return originalRepoPath;
        }

        static List<string> LoadIgnoreList(ZNode config, ConsoleLogger logger)
        {
            List<string> ignoreList = new List<string>();
            ZNode ignoreNode = config["ignore"];
            if (!ignoreNode)
                return ignoreList;

            foreach (ZNode item in ignoreNode)
            {
                string ignorePath = item.Optional();
                if (string.IsNullOrWhiteSpace(ignorePath))
                    continue;

                ignoreList.Add(ignorePath.Trim());
            }

            if (ignoreList.Count > 0)
                logger.Verbose($"Loaded ignore list from config: {string.Join(", ", ignoreList)}");

            return ignoreList;
        }

        static string? GetLiveRemoteUrl(string[] args, ZNode config, GitRunner git, ConsoleLogger logger)
        {
            string? urlArg = args.FirstOrDefault(a => a.StartsWith("--url="));
            if (!string.IsNullOrEmpty(urlArg))
            {
                string url = urlArg.Substring("--url=".Length).Trim();
                logger.Verbose($"Using LIVE URL from argument: {url}");
                return url;
            }

            string configUrl = config["public-url"];
            if (!string.IsNullOrEmpty(configUrl))
            {
                string url = configUrl.Trim();
                if (!url.EndsWith(".git") && (url.Contains("github.com") || url.Contains("gitlab.com")))
                    url = url + ".git";

                logger.Verbose($"Using LIVE URL from config file: {url}");
                return url;
            }

            string? remoteUrl = git.TryRun("remote get-url LIVE");
            if (remoteUrl != null)
            {
                remoteUrl = remoteUrl.Trim();
                logger.Verbose($"Found LIVE remote in current repo: {remoteUrl}");
                return remoteUrl;
            }

            logger.Error("ERROR: LIVE remote URL not provided. Pass the LIVE remote URL using --url=URL (no space), or configure it in gitlive.z0 file, or configure a 'LIVE' remote in the current repo.");
            return null;
        }

        static string NormalizeLiveUrl(string liveRemoteUrl, ConsoleLogger logger)
        {
            try
            {
                liveRemoteUrl = liveRemoteUrl.Trim().Replace("\\", "/");

                if (Uri.TryCreate(liveRemoteUrl, UriKind.Absolute, out Uri? parsed) &&
                    (parsed.Scheme == "http" || parsed.Scheme == "https"))
                {
                    string path = parsed.AbsolutePath ?? "";
                    while (path.EndsWith("/"))
                        path = path[..^1];

                    path = path.Replace("/.git", ".git").Replace(".git/", ".git");
                    while (path.Contains(".git.git"))
                        path = path.Replace(".git.git", ".git");

                    UriBuilder builder = new UriBuilder(parsed.Scheme, parsed.Host,
                        parsed.IsDefaultPort ? -1 : parsed.Port, path.Trim());
                    liveRemoteUrl = builder.Uri.ToString().TrimEnd('/');
                }
                else
                {
                    while (liveRemoteUrl.EndsWith("/"))
                        liveRemoteUrl = liveRemoteUrl[..^1];

                    liveRemoteUrl = liveRemoteUrl.Replace("/.git", ".git").Replace(".git/", ".git");
                    while (liveRemoteUrl.Contains(".git.git"))
                        liveRemoteUrl = liveRemoteUrl.Replace(".git.git", ".git");
                }

                logger.VeryVerbose($"Normalized LIVE URL: {liveRemoteUrl}");
            }
            catch (Exception)
            {
            }

            return liveRemoteUrl;
        }

        static GitLiveSync.SyncMode DetermineSyncMode(string[] args, out bool dryRun, ConsoleLogger logger)
        {
            dryRun = args.Any(a => a == "--dry-run");

            // Check for explicit mode flags (new approach)
            bool hasIncremental = args.Any(a => a == "--incremental");
            bool hasRepair = args.Any(a => a == "--repair");
            bool hasNuke = args.Any(a => a == "--nuke");

            // Also support legacy flags for backward compatibility
            bool hasFullLegacy = args.Any(a => a == "--full");

            GitLiveSync.SyncMode mode;

            if (hasNuke)
            {
                mode = GitLiveSync.SyncMode.Nuke;
                logger.Verbose("Running in Nuke mode: will delete existing tags on LIVE and fully resync (this will overwrite LIVE/main history).");
            }
            else if (hasRepair || hasFullLegacy)
            {
                mode = GitLiveSync.SyncMode.Repair;
                logger.Verbose("Running in Repair mode (will scan all live/ tags and start from first missing).");
            }
            else if (hasIncremental)
            {
                mode = GitLiveSync.SyncMode.Incremental;
                logger.Verbose("Running in Incremental mode (will start after latest published commit found on LIVE).");
            }
            else
            {
                // Default mode is Incremental
                mode = GitLiveSync.SyncMode.Incremental;
                logger.Verbose("Running in Incremental mode (will start after latest published commit found on LIVE).");
            }

            if (dryRun)
            {
                logger.Normal("Running in DRY RUN mode: no changes will be pushed to LIVE remote.");
            }

            return mode;
        }
    }
}