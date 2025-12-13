using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace GitLive
{
    /// <summary>
    /// Handles file selection using an ordered list of add/remove rules.
    /// Rules are evaluated in order, with each rule either adding or removing files from the selection.
    /// </summary>
    public class FileSelector
    {
        /// <summary>
        /// Represents a file selection rule (add or remove).
        /// </summary>
        public class Rule
        {
            public enum RuleType { Add, Remove }
            
            public RuleType Type { get; set; }
            public string Pattern { get; set; } = "";
            
            public Rule(RuleType type, string pattern)
            {
                Type = type;
                Pattern = pattern;
            }
            
            /// <summary>
            /// Checks if a file path matches this rule's pattern.
            /// Supports wildcards (* and ?) and directory matching.
            /// </summary>
            public bool Matches(string filePath)
            {
                // Normalize path separators to forward slashes
                filePath = filePath.Replace('\\', '/');
                var pattern = Pattern.Replace('\\', '/');
                
                // Handle patterns ending with / (directories) - match directory and all contents
                if (pattern.EndsWith("/"))
                {
                    pattern = pattern + "**";
                }
                
                // Convert Ant-style glob pattern to regex
                // ** matches zero or more path segments
                // * matches zero or more characters except /
                // ? matches exactly one character except /
                var regexPattern = ConvertAntPatternToRegex(pattern);
                
                try
                {
                    // Use case-sensitive matching to match git's behavior
                    // Compiled option improves performance when matching many files
                    return Regex.IsMatch(filePath, regexPattern, RegexOptions.Compiled);
                }
                catch (RegexMatchTimeoutException)
                {
                    // Pattern took too long to match
                    return false;
                }
                catch (ArgumentException)
                {
                    // Invalid regex pattern
                    return false;
                }
            }
            
            private string ConvertAntPatternToRegex(string pattern)
            {
                var result = new System.Text.StringBuilder();
                result.Append("^");
                
                for (int i = 0; i < pattern.Length; i++)
                {
                    char c = pattern[i];
                    
                    if (c == '*')
                    {
                        // Check for **
                        if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                        {
                            // ** matches zero or more path segments
                            // Check if there's a / before and after
                            bool hasSlashBefore = i > 0 && pattern[i - 1] == '/';
                            bool hasSlashAfter = i + 2 < pattern.Length && pattern[i + 2] == '/';
                            
                            if (hasSlashBefore && hasSlashAfter)
                            {
                                // Pattern like "foo/**/bar" - handle the middle **
                                result.Length--; // Remove the / we already appended
                                result.Append("(/.*)?/"); // Match / or /.../
                                i += 2; // Skip ** and the /
                            }
                            else if (hasSlashBefore)
                            {
                                // Pattern like "foo/**" at end
                                result.Length--; // Remove the / we already appended
                                result.Append("/.*");
                                i++; // Skip the second *
                            }
                            else if (hasSlashAfter)
                            {
                                // Pattern like "**/foo" at start
                                result.Append("(.*/)?");
                                i += 2; // Skip ** and the /
                            }
                            else
                            {
                                // Pattern is just ** or **something without slashes
                                result.Append(".*");
                                i++; // Skip the second *
                            }
                        }
                        else
                        {
                            // * matches zero or more characters except /
                            result.Append("[^/]*");
                        }
                    }
                    else if (c == '?')
                    {
                        // ? matches exactly one character except /
                        result.Append("[^/]");
                    }
                    else if ("\\[]{}()+|^$.".Contains(c))
                    {
                        // Escape regex special characters
                        result.Append('\\');
                        result.Append(c);
                    }
                    else
                    {
                        result.Append(c);
                    }
                }
                
                result.Append("$");
                return result.ToString();
            }
        }
        
        private readonly List<Rule> _rules = new List<Rule>();
        
        /// <summary>
        /// Adds a rule from a string specification.
        /// Format: "+ pattern" for add, "- pattern" for remove
        /// </summary>
        public void AddRule(string ruleSpec)
        {
            if (string.IsNullOrWhiteSpace(ruleSpec))
                return;
                
            var trimmed = ruleSpec.Trim();
            
            if (trimmed.StartsWith("+"))
            {
                var pattern = trimmed.Substring(1).Trim();
                if (!string.IsNullOrWhiteSpace(pattern))
                    _rules.Add(new Rule(Rule.RuleType.Add, pattern));
            }
            else if (trimmed.StartsWith("-"))
            {
                var pattern = trimmed.Substring(1).Trim();
                if (!string.IsNullOrWhiteSpace(pattern))
                    _rules.Add(new Rule(Rule.RuleType.Remove, pattern));
            }
        }
        
        /// <summary>
        /// Filters a git tree using the configured rules.
        /// Uses git plumbing commands to efficiently manipulate the tree without checkout.
        /// </summary>
        /// <param name="git">Git runner instance</param>
        /// <param name="treeId">The tree SHA to filter</param>
        /// <returns>The filtered tree SHA</returns>
        public string FilterTree(GitRunner git, string treeId)
        {
            if (_rules.Count == 0)
                return treeId;
            
            // Get all files in the source tree recursively
            var lsTreeOutput = git.Run($"ls-tree -r {treeId}");
            var entries = ParseLsTreeOutput(lsTreeOutput);
            
            if (entries.Count == 0)
                return treeId;
            
            // Evaluate rules to determine which files to include
            var selectedFiles = EvaluateRules(entries);
            
            if (selectedFiles.Count == 0)
            {
                // Return empty tree
                return CreateEmptyTree(git);
            }
            
            // Build new tree using git plumbing
            return BuildTreeFromSelection(git, selectedFiles);
        }
        
        /// <summary>
        /// Parses the output of git ls-tree -r command.
        /// </summary>
        private List<TreeEntry> ParseLsTreeOutput(string output)
        {
            var entries = new List<TreeEntry>();
            
            if (string.IsNullOrWhiteSpace(output))
                return entries;
            
            var lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            
            foreach (var line in lines)
            {
                // Format: <mode> <type> <object>\t<path>
                var parts = line.Split('\t');
                if (parts.Length != 2)
                    continue;
                
                var metaParts = parts[0].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (metaParts.Length != 3)
                    continue;
                
                entries.Add(new TreeEntry
                {
                    Mode = metaParts[0],
                    Type = metaParts[1],
                    ObjectSha = metaParts[2],
                    Path = parts[1]
                });
            }
            
            return entries;
        }
        
        /// <summary>
        /// Evaluates the rules against the file list to determine selected files.
        /// If first rule is add (+): start with empty selection (only added files included)
        /// If first rule is remove (-): start with all files (only removed files excluded)
        /// </summary>
        private HashSet<TreeEntry> EvaluateRules(List<TreeEntry> entries)
        {
            var selected = new HashSet<TreeEntry>(new TreeEntryComparer());
            
            // Determine starting state based on first rule
            if (_rules.Count > 0 && _rules[0].Type == Rule.RuleType.Remove)
            {
                // First rule is remove: start with all files
                foreach (var entry in entries)
                    selected.Add(entry);
            }
            // Otherwise (first rule is add or no rules): start with empty selection
            
            foreach (var rule in _rules)
            {
                var matchingEntries = entries.Where(e => rule.Matches(e.Path)).ToList();
                
                if (rule.Type == Rule.RuleType.Add)
                {
                    foreach (var entry in matchingEntries)
                        selected.Add(entry);
                }
                else // Remove
                {
                    foreach (var entry in matchingEntries)
                        selected.Remove(entry);
                }
            }
            
            return selected;
        }
        
        /// <summary>
        /// Builds a new tree from the selected files using git plumbing commands.
        /// Git commands may throw GitException if operations fail.
        /// </summary>
        private string BuildTreeFromSelection(GitRunner git, HashSet<TreeEntry> selectedFiles)
        {
            // Start with an empty index
            // Note: git.Run throws GitException on failure, which is handled by caller
            git.Run("read-tree --empty");
            
            // Add each selected file to the index using update-index --cacheinfo
            foreach (var entry in selectedFiles.OrderBy(e => e.Path))
            {
                // Only add blob entries (files), not tree entries (directories)
                if (entry.Type == "blob")
                {
                    git.Run($"update-index --add --cacheinfo {entry.Mode},{entry.ObjectSha},{EscapeArg(entry.Path)}");
                }
            }
            
            // Write the tree
            var newTreeId = git.Run("write-tree").Trim();
            return newTreeId;
        }
        
        /// <summary>
        /// Creates an empty tree and returns its SHA.
        /// </summary>
        private string CreateEmptyTree(GitRunner git)
        {
            git.Run("read-tree --empty");
            return git.Run("write-tree").Trim();
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
        
        /// <summary>
        /// Represents a git tree entry.
        /// </summary>
        private class TreeEntry
        {
            public string Mode { get; set; } = "";
            public string Type { get; set; } = "";
            public string ObjectSha { get; set; } = "";
            public string Path { get; set; } = "";
        }
        
        /// <summary>
        /// Comparer for TreeEntry based on path.
        /// </summary>
        private class TreeEntryComparer : IEqualityComparer<TreeEntry>
        {
            public bool Equals(TreeEntry? x, TreeEntry? y)
            {
                if (x == null && y == null) return true;
                if (x == null || y == null) return false;
                return x.Path == y.Path;
            }
            
            public int GetHashCode(TreeEntry obj)
            {
                return obj.Path.GetHashCode();
            }
        }
    }
}
