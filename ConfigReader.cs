using Racso.Z0.Parser;

namespace GitLive
{
    /// <summary>
    /// Defines the security level for reading configuration values.
    /// Controls which sources are allowed for sensitive data.
    /// </summary>
    public enum ConfigSecurityLevel
    {
        /// <summary>
        /// Only environment variables are allowed (most secure)
        /// </summary>
        SecureStrict,

        /// <summary>
        /// Environment variables and CLI arguments are allowed (no Z0 file)
        /// </summary>
        SecureFlexible,

        /// <summary>
        /// All sources are allowed: CLI, ENV, and Z0 file
        /// </summary>
        All
    }

    /// <summary>
    /// Reads configuration values from multiple sources with priority:
    /// CLI arguments (highest) > Environment variables (medium) > Z0 file (lowest)
    /// </summary>
    public class ConfigReader
    {
        private readonly string[] cliArgs;
        private readonly ZNode z0Config;
        private const string EnvPrefix = "GITLIVE_";

        public ConfigReader(string[] cliArgs, ZNode z0Config)
        {
            this.cliArgs = cliArgs ?? Array.Empty<string>();
            this.z0Config = z0Config ?? ParsingNode.NewRootNode().AsZNode();
        }

        /// <summary>
        /// Gets a configuration value with the specified security level.
        /// </summary>
        /// <param name="variableName">The variable name (case insensitive)</param>
        /// <param name="securityLevel">Security level controlling which sources are allowed</param>
        /// <param name="defaultValue">Default value if not found</param>
        /// <returns>The configuration value or default value if not found</returns>
        public string? GetValue(string variableName, ConfigSecurityLevel securityLevel = ConfigSecurityLevel.All, string? defaultValue = null)
        {
            if (string.IsNullOrWhiteSpace(variableName))
                return defaultValue;

            // Priority 1: CLI arguments (if allowed by security level)
            if (securityLevel != ConfigSecurityLevel.SecureStrict)
            {
                var cliValue = GetFromCli(variableName);
                if (cliValue != null)
                    return cliValue;
            }

            // Priority 2: Environment variables (always allowed)
            var envValue = GetFromEnv(variableName);
            if (envValue != null)
                return envValue;

            // Priority 3: Z0 file (only if allowed by security level)
            if (securityLevel == ConfigSecurityLevel.All)
            {
                var z0Value = GetFromZ0(variableName);
                if (z0Value != null)
                    return z0Value;
            }

            return defaultValue;
        }

        /// <summary>
        /// Gets a value from CLI arguments using case-insensitive matching.
        /// Expected format: --variable-name=value
        /// </summary>
        private string? GetFromCli(string variableName)
        {
            foreach (var arg in cliArgs)
            {
                if (arg.StartsWith("--", StringComparison.OrdinalIgnoreCase) && arg.Contains('='))
                {
                    var parts = arg.Split('=', 2);
                    if (parts.Length == 2)
                    {
                        var argName = parts[0].Substring(2); // Remove "--"
                        if (NormalizeVariableName(argName) == NormalizeVariableName(variableName))
                        {
                            return parts[1].Trim();
                        }
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Gets a value from environment variables using case-insensitive matching.
        /// Expected format: GITLIVE_VARIABLE_NAME
        /// </summary>
        private string? GetFromEnv(string variableName)
        {
            var envVarName = EnvPrefix + variableName.ToUpperInvariant();
            
            // Try exact match first (with uppercase)
            var value = Environment.GetEnvironmentVariable(envVarName);
            if (value != null)
                return value;

            // Try case-insensitive search through all environment variables
            var normalizedTarget = NormalizeVariableName(envVarName);
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                var key = entry.Key?.ToString();
                if (key != null && NormalizeVariableName(key) == normalizedTarget)
                {
                    return entry.Value?.ToString();
                }
            }

            return null;
        }

        /// <summary>
        /// Gets a value from Z0 configuration file.
        /// Z0 parser already handles case-insensitive matching for keys.
        /// </summary>
        private string? GetFromZ0(string variableName)
        {
            if (!z0Config.Exists)
                return null;

            var node = z0Config[variableName];
            if (node.Exists)
            {
                return node.Optional();
            }

            return null;
        }

        /// <summary>
        /// Normalizes a variable name for case-insensitive comparison.
        /// Treats hyphens and underscores as equivalent.
        /// </summary>
        private static string NormalizeVariableName(string name)
        {
            return name.ToLowerInvariant().Replace('-', '_');
        }
    }
}
