using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Racso.TransientGit
{
    public enum Do
    {
        Nothing,
        Commit
    }

    public readonly struct Repository
    {
        public string RepoPath { get; }

        public Repository(string repoPath)
        {
            RepoPath = repoPath;
        }
    }

    public readonly struct Branch
    {
        public Repository Repo { get; }
        public string BranchName { get; }

        public Branch(Repository repo, string branchName)
        {
            Repo = repo;
            BranchName = branchName;
        }
    }

    public readonly struct File
    {
        public Branch Branch { get; }
        public string FileName { get; }
        public string FilePath => Path.Combine(Branch.Repo.RepoPath, FileName);

        public File(Branch branch, string fileName)
        {
            Branch = branch;
            FileName = fileName;
        }
    }

    public readonly struct Commit
    {
        public string Hash { get; }
        public Repository Repo { get; }

        public Commit(string hash, Repository repo)
        {
            Hash = hash;
            Repo = repo;
        }
    }

    public class TransientGit : IDisposable
    {
        private readonly List<Repository> _repositories = new();
        private bool _disposed;
        private Repository? _selectedRepository;
        private Branch? _selectedBranch;
        private File? _selectedFile;
        private Commit? _selectedCommit;

        public Repository NewRepo(string? path = null)
        {
            var tempPath = path ?? Path.Combine(Path.GetTempPath(), $"transient-git-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempPath);

            var repo = new Repository(tempPath);
            _repositories.Add(repo);
            _selectedRepository = repo;

            RunGit(repo, "init");
            RunGit(repo, "config user.email \"test@transientgit.local\"");
            RunGit(repo, "config user.name \"TransientGit Test\"");

            // Set initial branch as selected
            _selectedBranch = new Branch(repo, GetCurrentBranch(repo));

            return repo;
        }

        public File File(Branch branch, string fileName)
        {
            var file = new File(branch, fileName);
            var filePath = file.FilePath;

            if (!System.IO.File.Exists(filePath))
            {
                System.IO.File.WriteAllText(filePath, string.Empty);
            }

            _selectedFile = file;
            return file;
        }

        public File File(string fileName)
        {
            if (_selectedBranch == null)
                throw new InvalidOperationException("No branch selected for File operation");

            return File(_selectedBranch.Value, fileName);
        }

        public void WriteLine(File file, string line, Do doAction = Do.Nothing)
        {
            System.IO.File.AppendAllText(file.FilePath, line + Environment.NewLine);
            _selectedFile = file;

            if (doAction == Do.Commit)
            {
                RunGit(file.Branch.Repo, $"add \"{file.FileName}\"");
                var escapedLine = line.Replace("\"", "\\\"").Replace("$", "\\$").Replace("`", "\\`");
                RunGit(file.Branch.Repo, $"commit -m \"Add line: {escapedLine}\"");
                var hash = RunGit(file.Branch.Repo, "rev-parse HEAD").Trim();
                _selectedCommit = new Commit(hash, file.Branch.Repo);
            }
        }

        public void WriteLine(string line, Do doAction = Do.Nothing)
        {
            if (_selectedFile == null)
                throw new InvalidOperationException("No file selected for WriteLine operation");

            WriteLine(_selectedFile.Value, line, doAction);
        }

        public Commit Commit(Repository repo, string message)
        {
            var escapedMessage = message.Replace("\"", "\\\"").Replace("$", "\\$").Replace("`", "\\`");
            RunGit(repo, $"commit --allow-empty -m \"{escapedMessage}\"");
            var hash = RunGit(repo, "rev-parse HEAD").Trim();
            var commit = new Commit(hash, repo);
            _selectedCommit = commit;
            return commit;
        }

        public Commit Commit(string message)
        {
            if (_selectedRepository == null)
                throw new InvalidOperationException("No repository selected for Commit operation");

            return Commit(_selectedRepository.Value, message);
        }

        public Commit GetCommit(Repository repo, string hash)
        {
            var commit = new Commit(hash, repo);
            _selectedCommit = commit;
            return commit;
        }

        public Commit GetCommit(string hash)
        {
            if (_selectedRepository == null)
                throw new InvalidOperationException("No repository selected for GetCommit operation");

            return GetCommit(_selectedRepository.Value, hash);
        }

        public string Exec(Repository repo, string gitCommand)
        {
            return RunGit(repo, gitCommand);
        }

        public string Exec(string gitCommand)
        {
            if (_selectedRepository == null)
                throw new InvalidOperationException("No repository selected for Exec operation");

            return RunGit(_selectedRepository.Value, gitCommand);
        }

        public void Select(Repository repo)
        {
            _selectedRepository = repo;
        }

        public void Select(Branch branch)
        {
            _selectedBranch = branch;
            _selectedRepository = branch.Repo;
        }

        public void Select(File file)
        {
            _selectedFile = file;
            _selectedBranch = file.Branch;
            _selectedRepository = file.Branch.Repo;
        }

        public void Select(Commit commit)
        {
            _selectedCommit = commit;
            _selectedRepository = commit.Repo;
        }

        public Branch Branch(Repository repo, string branchName)
        {
            var branchExists = TryRunGit(repo, $"show-ref --verify --quiet refs/heads/{branchName}");
            if (branchExists)
            {
                RunGit(repo, $"checkout {branchName}");
            }
            else
            {
                RunGit(repo, $"checkout -b {branchName}");
            }
            var branch = new Branch(repo, branchName);
            _selectedBranch = branch;
            return branch;
        }

        public Branch Branch(Commit commit, string branchName)
        {
            RunGit(commit.Repo, $"checkout -b {branchName} {commit.Hash}");
            var branch = new Branch(commit.Repo, branchName);
            _selectedBranch = branch;
            return branch;
        }

        public Branch Branch(string branchName)
        {
            if (_selectedRepository == null)
                throw new InvalidOperationException("No repository selected for Branch operation");

            return Branch(_selectedRepository.Value, branchName);
        }

        public Branch Branch(string commitHash, string branchName)
        {
            if (_selectedRepository == null)
                throw new InvalidOperationException("No repository selected for Branch operation");

            var commit = new Commit(commitHash, _selectedRepository.Value);
            return Branch(commit, branchName);
        }

        public void SeedCommits(File file, int startNumber, int endNumber)
        {
            for (int i = startNumber; i <= endNumber; i++)
            {
                WriteLine(file, i.ToString(), Do.Commit);
            }
        }

        public void Dispose(Repository repo)
        {
            if (Directory.Exists(repo.RepoPath))
            {
                try
                {
                    SafeDeleteDirectory(repo.RepoPath);
                    _repositories.Remove(repo);
                    
                    // Clear selected state if disposing the selected repository
                    if (_selectedRepository?.RepoPath == repo.RepoPath)
                    {
                        _selectedRepository = null;
                        _selectedBranch = null;
                        _selectedFile = null;
                        _selectedCommit = null;
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Warning: Failed to delete repository at {repo.RepoPath}: {ex.Message}");
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            foreach (var repo in _repositories.ToList())
            {
                Dispose(repo);
            }

            _repositories.Clear();
        }

        private string GetCurrentBranch(Repository repo)
        {
            try
            {
                return RunGit(repo, "rev-parse --abbrev-ref HEAD").Trim();
            }
            catch
            {
                // If we can't get the branch (e.g., no commits yet), default to "main"
                return "main";
            }
        }

        private string RunGit(Repository repo, string arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = repo.RepoPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
                throw new InvalidOperationException("Failed to start git process");

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();

            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Git command failed: git {arguments}\n" +
                    $"Exit code: {process.ExitCode}\n" +
                    $"Output: {output}\n" +
                    $"Error: {error}");
            }

            return output;
        }

        private bool TryRunGit(Repository repo, string arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = repo.RepoPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
                return false;

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();

            process.WaitForExit();

            return process.ExitCode == 0;
        }

        private static void SafeDeleteDirectory(string path)
        {
            if (!Directory.Exists(path))
                return;

            ClearReadOnlyAttributes(path);

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Directory.Delete(path, true);
                    return;
                }
                catch (IOException)
                {
                    if (attempt < 2)
                        System.Threading.Thread.Sleep(100);
                }
                catch (UnauthorizedAccessException)
                {
                    if (attempt < 2)
                        System.Threading.Thread.Sleep(100);
                }
            }

            try
            {
                Directory.Delete(path, true);
            }
            catch (Exception)
            {
                // Final attempt failed, silently ignore
            }
        }

        private static void ClearReadOnlyAttributes(string path)
        {
            var dirInfo = new DirectoryInfo(path);

            foreach (var file in dirInfo.GetFiles("*", SearchOption.AllDirectories))
            {
                try
                {
                    file.Attributes = FileAttributes.Normal;
                }
                catch (Exception)
                {
                    // Ignore IO or security errors when clearing attributes
                }
            }

            foreach (var dir in dirInfo.GetDirectories("*", SearchOption.AllDirectories))
            {
                try
                {
                    dir.Attributes = FileAttributes.Normal;
                }
                catch (Exception)
                {
                    // Ignore IO or security errors when clearing attributes
                }
            }
        }
    }
}
