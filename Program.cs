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

                ZNode config = GetConfig("gitlive.z0");
                ConfigReader configReader = new ConfigReader(args, config);

                List<string> fileSelectionRules = LoadFileSelectionRules(config, logger);

                string? liveRemoteUrl = GetLiveRemoteUrl(configReader, git, logger);
                if (liveRemoteUrl == null)
                    return 2;

                liveRemoteUrl = NormalizeLiveUrl(liveRemoteUrl, logger);
                liveRemoteUrl = AddAuthentication(liveRemoteUrl, configReader, logger);

                GitLiveSync.SyncMode mode = DetermineSyncMode(args, out bool dryRun, logger);

                GitLiveSync.SyncOptions syncOptions = new GitLiveSync.SyncOptions
                {
                    OriginalRepoPath = originalRepoPath,
                    LiveRemoteUrl = liveRemoteUrl,
                    FileSelectionRules = fileSelectionRules,
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

        static List<string> LoadFileSelectionRules(ZNode config, ConsoleLogger logger)
        {
            List<string> rules = new List<string>();
            ZNode filesNode = config["files"];
            if (!filesNode)
                return rules;

            foreach (ZNode item in filesNode)
            {
                string rule = item.Optional();
                if (string.IsNullOrWhiteSpace(rule))
                    continue;

                rules.Add(rule.Trim());
            }

            if (rules.Count > 0)
                logger.Verbose($"Loaded {rules.Count} file selection rule(s) from config");

            return rules;
        }

        static string? GetLiveRemoteUrl(ConfigReader configReader, GitRunner git, ConsoleLogger logger)
        {
            // Try to get URL from ConfigReader (checks CLI, ENV, Z0 in that order)
            // Using "url" for CLI --url=, GITLIVE_URL for env, and url/public-url for Z0
            string? url = configReader.GetValue("url", ConfigSecurityLevel.All);
            
            // Also check for "public-url" in Z0 for backwards compatibility
            if (string.IsNullOrEmpty(url))
            {
                url = configReader.GetValue("public-url", ConfigSecurityLevel.All);
            }

            if (!string.IsNullOrEmpty(url))
            {
                url = url.Trim();
                if (!url.EndsWith(".git") && (url.Contains("github.com") || url.Contains("gitlab.com")))
                    url = url + ".git";

                logger.Verbose($"Using LIVE URL from configuration: {url}");
                return url;
            }

            // Fall back to git remote if no config provided
            string? remoteUrl = git.TryRun("remote get-url LIVE");
            if (remoteUrl != null)
            {
                remoteUrl = remoteUrl.Trim();
                logger.Verbose($"Found LIVE remote in current repo: {remoteUrl}");
                return remoteUrl;
            }

            logger.Error("ERROR: LIVE remote URL not provided. Pass the LIVE remote URL using --url=URL (no space), or set GITLIVE_URL environment variable, or configure it in gitlive.z0 file, or configure a 'LIVE' remote in the current repo.");
            return null;
        }

        static string NormalizeLiveUrl(string liveRemoteUrl, ConsoleLogger logger)
        {
            try
            {
                liveRemoteUrl = liveRemoteUrl.Trim().Replace("\\", "/").TrimEnd('/');
                if (!liveRemoteUrl.EndsWith(".git")) 
                    liveRemoteUrl += ".git";
                
                logger.VeryVerbose($"Normalized LIVE URL: {liveRemoteUrl}");
            }
            catch (Exception)
            {
                logger.Error($"ERROR: Failed to normalize LIVE remote URL: {liveRemoteUrl}");
            }

            return liveRemoteUrl;
        }

        static string AddAuthentication(string liveRemoteUrl, ConfigReader configReader, ConsoleLogger logger)
        {
            // Get username and password from config (allows CLI and ENV, but not Z0 for password)
            string? username = configReader.GetValue("user", ConfigSecurityLevel.All);
            string? password = configReader.GetValue("password", ConfigSecurityLevel.SecureFlexible);
            
            if (string.IsNullOrWhiteSpace(username) && string.IsNullOrWhiteSpace(password))
            {
                logger.VeryVerbose("No authentication credentials provided for LIVE URL");
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(username))
                    logger.VeryVerbose("Username provided for LIVE URL authentication");
                if (!string.IsNullOrWhiteSpace(password))
                    logger.VeryVerbose("Password provided for LIVE URL authentication");
                
                return UrlBuilder.AddAuthentication(liveRemoteUrl, username, password);
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