using Noggog;
using Noggog.Utility;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Synthesis.Bethesda.Execution
{
    public record DotNetVersion(string Version, bool Acceptable);

    public static class DotNetCommands
    {
        public const int MinVersion = 5;

        public static string GetBuildString(string args)
        {
            return $"build --runtime win-x64 {args}";
        }

        public static async Task<IEnumerable<(string Package, string Requested, string Resolved, string Latest)>> NugetListingQuery(string projectPath, bool outdated, bool includePrerelease, CancellationToken cancel)
        {
            // Run restore first
            {
                using var restore = ProcessWrapper.Create(
                    new ProcessStartInfo("dotnet", $"restore \"{projectPath}\""),
                    cancel: cancel);
                await restore.Run();
            }

            bool on = false;
            List<string> lines = new();
            List<string> errors = new();
            using var process = ProcessWrapper.Create(
                new ProcessStartInfo("dotnet", $"list \"{projectPath}\" package{(outdated ? " --outdated" : null)}{(includePrerelease ? " --include-prerelease" : null)}"),
                cancel: cancel);
            using var outSub = process.Output.Subscribe(s =>
            {
                if (s.Contains("Top-level Package"))
                {
                    on = true;
                    return;
                }
                if (!on) return;
                lines.Add(s);
            });
            using var errSub = process.Error.Subscribe(s => errors.Add(s));
            var result = await process.Run();
            if (errors.Count > 0)
            {
                throw new ArgumentException($"Error while retrieving nuget listings: \n{string.Join("\n", errors)}");
            }

            var ret = new List<(string Package, string Requested, string Resolved, string Latest)>();
            foreach (var line in lines)
            {
                if (!TryParseLibraryLine(
                    line, 
                    out var package,
                    out var requested, 
                    out var resolved, 
                    out var latest))
                {
                    continue;
                }
                ret.Add((package, requested, resolved, latest));
            }
            return ret;
        }

        public static bool TryParseLibraryLine(
            string line, 
            [MaybeNullWhen(false)] out string package,
            [MaybeNullWhen(false)] out string requested,
            [MaybeNullWhen(false)] out string resolved,
            [MaybeNullWhen(false)] out string latest)
        {
            var startIndex = line.IndexOf("> ");
            if (startIndex == -1)
            {
                package = default;
                requested = default;
                resolved = default;
                latest = default;
                return false;
            }
            var split = line
                .Substring(startIndex + 2)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .WithIndex()
                .Where(x => x.Index == 0 || x.Item != "(D)")
                .Select(x => x.Item)
                .ToArray();
            package = split[0];
            requested = split[1];
            resolved = split[2];
            latest = split[3];
            return true;
        }

        public static async Task<(string? MutagenVersion, string? SynthesisVersion)> QuerySynthesisVersions(string projectPath, bool current, bool includePrerelease, CancellationToken cancel)
        {
            string? mutagenVersion = null, synthesisVersion = null;
            var queries = await NugetListingQuery(projectPath, outdated: !current, includePrerelease: includePrerelease, cancel: cancel);
            foreach (var item in queries)
            {
                if (item.Package.StartsWith("Mutagen.Bethesda")
                    && !item.Package.EndsWith("Synthesis"))
                {
                    mutagenVersion = current ? item.Resolved : item.Latest;
                }
                if (item.Package.Equals("Mutagen.Bethesda.Synthesis"))
                {
                    synthesisVersion = current ? item.Resolved : item.Latest;
                }
            }
            return (mutagenVersion, synthesisVersion);
        }

        public static async Task<DotNetVersion> DotNetSdkVersion(CancellationToken cancel)
        {
            using var proc = ProcessWrapper.Create(
                new System.Diagnostics.ProcessStartInfo("dotnet", "--version"),
                cancel: cancel);
            List<string> outs = new();
            using var outp = proc.Output.Subscribe(o => outs.Add(o));
            List<string> errs = new();
            using var errp = proc.Error.Subscribe(o => errs.Add(o));
            var result = await proc.Run();
            if (errs.Count > 0)
            {
                throw new ArgumentException($"{string.Join("\n", errs)}");
            }
            if (outs.Count != 1)
            {
                throw new ArgumentException($"Unexpected messages:\n{string.Join("\n", outs)}");
            }
            return GetDotNetVersion(outs[0]);
        }

        public static DotNetVersion GetDotNetVersion(ReadOnlySpan<char> str)
        {
            var orig = str;
            var indexOf = str.IndexOf('-');
            if (indexOf != -1)
            {
                str = str.Slice(0, indexOf);
            }
            if (Version.TryParse(str, out var vers)
                && vers.Major >= MinVersion)
            {
                return new DotNetVersion(orig.ToString(), true);
            }
            return new DotNetVersion(orig.ToString(), false);
        }
        
        public static async Task<GetResponse<string>> GetExecutablePath(string projectPath, CancellationToken cancel, Action<string>? log)
        {
            // Hacky way to locate executable, but running a build and extracting the path its logs spit out
            // Tried using Buildalyzer, but it has a lot of bad side effects like clearing build outputs when
            // locating information like this.
            using var proc = ProcessWrapper.Create(
                new System.Diagnostics.ProcessStartInfo("dotnet", GetBuildString($"\"{projectPath}\"")),
                cancel: cancel);
            log?.Invoke($"({proc.StartInfo.WorkingDirectory}): {proc.StartInfo.FileName} {proc.StartInfo.Arguments}");
            List<string> outs = new();
            using var outp = proc.Output.Subscribe(o => outs.Add(o));
            List<string> errs = new();
            using var errp = proc.Error.Subscribe(o => errs.Add(o));
            var result = await proc.Run();
            if (errs.Count > 0)
            {
                throw new ArgumentException($"{string.Join("\n", errs)}");
            }
            if (!TryGetExecutablePathFromOutput(outs, out var path))
            {
                log?.Invoke($"Could not locate target executable: {string.Join(Environment.NewLine, outs)}");
                return GetResponse<string>.Fail("Could not locate target executable.");
            }
            return GetResponse<string>.Succeed(path);
        }

        public static bool TryGetExecutablePathFromOutput(IEnumerable<string> lines, [MaybeNullWhen(false)] out string output)
        {
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (!trimmed.EndsWith(".dll")) continue;
                const string delimiter = " -> ";
                var index = trimmed.IndexOf(delimiter);
                if (index == -1) continue;
                output = trimmed.Substring(index + delimiter.Length).Trim();
                return true;
            }
            output = null;
            return false;
        }

        public static async Task<ErrorResponse> Compile(string targetPath, CancellationToken cancel, Action<string>? log)
        {
            using var process = ProcessWrapper.Create(
                new ProcessStartInfo("dotnet", GetBuildString($"\"{Path.GetFileName(targetPath)}\""))
                {
                    WorkingDirectory = Path.GetDirectoryName(targetPath)!
                },
                cancel: cancel);
            log?.Invoke($"({process.StartInfo.WorkingDirectory}): {process.StartInfo.FileName} {process.StartInfo.Arguments}");
            string? firstError = null;
            bool buildFailed = false;
            List<string> output = new();
            int totalLen = 0;
            process.Output.Subscribe(o =>
            {
                // ToDo
                // Refactor off looking for a string
                if (o.StartsWith("Build FAILED"))
                {
                    buildFailed = true;
                }
                else if (buildFailed
                    && firstError == null
                    && !string.IsNullOrWhiteSpace(o)
                    && o.StartsWith("error"))
                {
                    firstError = o;
                }
                if (totalLen < 10_000)
                {
                    totalLen += o.Length;
                    output.Add(o);
                }
            });
            var result = await process.Run().ConfigureAwait(false);
            if (result == 0) return ErrorResponse.Success;
            firstError = firstError?.TrimStart($"{targetPath} : ");
            if (firstError == null && cancel.IsCancellationRequested)
            {
                firstError = "Cancelled";
            }
            return ErrorResponse.Fail(reason: firstError ?? $"Unknown Error: {string.Join(Environment.NewLine, output)}");
        }
    }
}
