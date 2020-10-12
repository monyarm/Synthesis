using LibGit2Sharp;
using Mutagen.Bethesda;
using Noggog;
using Synthesis.Bethesda.Execution.Patchers;
using Synthesis.Bethesda.Execution.Reporters;
using Synthesis.Bethesda.Execution.Settings;
using System;
using System.IO;
using System.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Synthesis.Bethesda.Execution
{
    public class GitPatcherRun : IPatcherRun
    {
        public string Name { get; }
        private readonly string _localDir;
        private GithubPatcherSettings _settings;
        public SolutionPatcherRun? SolutionRun { get; private set; }

        private Subject<string> _output = new Subject<string>();
        public IObservable<string> Output => _output;

        private Subject<string> _error = new Subject<string>();
        public IObservable<string> Error => _error;

        public GitPatcherRun(
            GithubPatcherSettings settings,
            string localDir)
        {
            _localDir = localDir;
            _settings = settings;
            Name = $"{settings.Nickname.Decorate(x => $"{x} => ")}{settings.RemoteRepoPath} => {Path.GetFileNameWithoutExtension(settings.SelectedProjectSubpath)}";
        }

        public void Dispose()
        {
        }

        public async Task Prep(GameRelease release, CancellationToken? cancel = null)
        {
            _output.OnNext("Cloning repository");
            var cloneResult = await CheckOrCloneRepo(GetResponse<string>.Succeed(_settings.RemoteRepoPath), _localDir, (x) => _output.OnNext(x), cancel ?? CancellationToken.None);
            if (cloneResult.Failed)
            {
                throw new SynthesisBuildFailure(cloneResult.Reason);
            }

            throw new NotImplementedException("Need to migrate in proper git checkouts");

            //_output.OnNext($"Locating path to solution based on local dir {_localDir}");
            //var pathToSln = GetPathToSolution(_localDir);
            //_output.OnNext($"Locating path to project based on {pathToSln} AND {_settings.SelectedProjectSubpath}");
            //var foundProjSubPath = SolutionPatcherRun.AvailableProject(pathToSln, _settings.SelectedProjectSubpath);
            //if (foundProjSubPath == null)
            //{
            //    throw new SynthesisBuildFailure("Could not locate project sub path");
            //}
            //var pathToProj = Path.Combine(_localDir, foundProjSubPath);
            //SolutionRun = new SolutionPatcherRun(
            //    _settings.Nickname,
            //    pathToSln: Path.Combine(_localDir, pathToSln), 
            //    pathToProj: pathToProj);
            //using var outputSub = SolutionRun.Output.Subscribe(this._output);
            //using var errSub = SolutionRun.Error.Subscribe(this._error);
            //await SolutionRun.Prep(release, cancel).ConfigureAwait(false);
        }

        public async Task Run(RunSynthesisPatcher settings, CancellationToken? cancel = null)
        {
            if (SolutionRun == null)
            {
                throw new SynthesisBuildFailure("Expected Solution Run object did not exist.");
            }
            using var outputSub = SolutionRun.Output.Subscribe(this._output);
            using var errSub = SolutionRun.Error.Subscribe(this._error);
            await SolutionRun.Run(settings, cancel).ConfigureAwait(false);
        }

        private static bool DeleteOldRepo(
            string localDir,
            GetResponse<string> remoteUrl,
            Action<string> logger)
        {
            if (!Directory.Exists(localDir))
            {
                logger("No local repository exists.  No cleaning to do.");
                return false;
            }
            var dirInfo = new DirectoryPath(localDir);
            if (remoteUrl.Failed)
            {
                logger("No remote repository.  Deleting local.");
                dirInfo.DeleteEntireFolder();
                return false;
            }
            try
            {
                using var repo = new Repository(localDir);
                // If it's the same remote repo, don't delete
                if (repo.Network.Remotes.FirstOrDefault()?.Url.Equals(remoteUrl.Value) ?? false)
                {
                    logger("Remote repository target matched local folder's repo.  Keeping clone.");
                    return true;
                }
            }
            catch (RepositoryNotFoundException)
            {
                logger("Repository corrupted.  Deleting local.");
                dirInfo.DeleteEntireFolder();
                return false;
            }

            logger("Remote address targeted a different repository.  Deleting local.");
            dirInfo.DeleteEntireFolder();
            return false;
        }

        public static async Task<GetResponse<(string Remote, string Local)>> CheckOrCloneRepo(
            GetResponse<string> remote,
            string localDir,
            Action<string> logger,
            CancellationToken cancel)
        {
            try
            {
                cancel.ThrowIfCancellationRequested();
                if (DeleteOldRepo(localDir: localDir, remoteUrl: remote, logger: logger))
                {
                    // Short circuiting deletion
                    return GetResponse<(string Remote, string Local)>.Succeed((remote.Value, localDir), remote.Reason);
                }
                cancel.ThrowIfCancellationRequested();
                if (remote.Failed) return GetResponse<(string Remote, string Local)>.Fail((remote.Value, string.Empty), remote.Reason);
                logger($"Cloning remote {remote.Value}");
                var clonePath = Repository.Clone(remote.Value, localDir);
                return GetResponse<(string Remote, string Local)>.Succeed((remote.Value, clonePath), remote.Reason);
            }
            catch (Exception ex)
            {
                logger($"Failure while checking/cloning repository: {ex}");
                return GetResponse<(string Remote, string Local)>.Fail((remote.Value, string.Empty), ex);
            }
        }

        public static string GetPathToSolution(string pathToRepo)
        {
            return Directory.EnumerateFiles(pathToRepo, "*.sln").FirstOrDefault();
        }

        public static string RunnerRepoDirectory(string profileID, string githubID)
        {
            return Path.Combine(Execution.Constants.WorkingDirectory, profileID, "Git", githubID, "Runner");
        }
    }
}
