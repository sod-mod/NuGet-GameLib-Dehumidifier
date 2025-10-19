using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.AssemblyPublicizer;
using Build.Schema;
using Build.Tasks;
using Build.util;
using Cake.Common;
using Cake.Common.IO;
using Cake.Common.Tools.Command;
using Cake.Common.Tools.DotNet;
using Cake.Common.Tools.DotNet.NuGet.Push;
using Cake.Core;
using Cake.Core.Diagnostics;
using Cake.Core.IO;
using Cake.Frosting;
using Cake.Git;
using Json.Schema;
using Json.Schema.Serialization;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using Path = System.IO.Path;

namespace Build;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        return new CakeHost()
            .UseContext<BuildContext>()
            .Run(args);
    }
}

public class BuildContext : FrostingContext
{
   public const string DehumidifierVersionDiscriminatorPrefix = "ngd";

    public string GameFolderName { get; }
    public int? GameBuildId { get; }
    public string SteamUsername { get; }
    public string NugetApiKey { get; }

    public DirectoryPath RootDirectory { get; }
    public DirectoryPath GameDirectory => RootDirectory.Combine("Games").Combine(GameFolderName);

    public Versioner Versioner { get; }

    public Schema.GameMetadata GameMetadata { get; set; }
    public Schema.GameVersionMap GameVersions { get; set; }
    public SteamAppInfo GameAppInfo { get; set; }
    public Dictionary<string, Task> AssemblyProcessingTasks { get; set; }

    private ReadOnlyDictionary<string, IList<IPackageSearchMetadata>>? _deployedPackageMetadata;

    public IDictionary<string, IList<IPackageSearchMetadata>> DeployedPackageMetadata
    {
        get => _deployedPackageMetadata ?? throw new InvalidOperationException();
        set => _deployedPackageMetadata = new ReadOnlyDictionary<string, IList<IPackageSearchMetadata>>(value);
    }

    private ReadOnlyDictionary<PackageIdentity, DownloadResourceResult>? _nuGetPackageDownloadResults;

    public IDictionary<PackageIdentity, DownloadResourceResult> NuGetPackageDownloadResults
    {
        get => _nuGetPackageDownloadResults ?? throw new InvalidOperationException();
        set => _nuGetPackageDownloadResults = new ReadOnlyDictionary<PackageIdentity, DownloadResourceResult>(value);
    }

    private ReadOnlyDictionary<NuGetFramework, ISet<string>>? _frameworkTargetDependencyAssemblyNames;

    public IDictionary<NuGetFramework, ISet<string>> FrameworkTargetDependencyAssemblyNames
    {
        get => _frameworkTargetDependencyAssemblyNames ?? throw new InvalidOperationException();
        set => _frameworkTargetDependencyAssemblyNames = new ReadOnlyDictionary<NuGetFramework, ISet<string>>(value);
    }

    public BuildContext(ICakeContext context) : base(context)
    {
        GameFolderName = context.Argument<string>("game");
        GameBuildId = context.Argument<int?>("build", null);
        SteamUsername = context.Argument<string>("steam-username", "");
        NugetApiKey = context.Argument<string>("nuget-api-key", "");

        RootDirectory = context.Environment.WorkingDirectory.GetParent();
        Versioner = new(RootDirectory.FullPath);
    }

    public GitCommit InferredGitCommit(string message)
    {
        var name = this.GitConfigGet<string>(RootDirectory, "user.name");
        var email = this.GitConfigGet<string>(RootDirectory, "user.email");

        return this.GitCommit(RootDirectory, name, email, message);
    }
}

[TaskName("Clean")]
public sealed class CleanTask : FrostingTaskBase<BuildContext>
{
    public override void Run(BuildContext context)
    {
        context.Log.Information("Cleaning up previous build artifacts...");
        context.CleanDirectories(context.RootDirectory.Combine("Games/*/dist").FullPath);
    }
}

[TaskName("RegisterJSONSchemas")]
public sealed class RegisterJsonSchemasTask : FrostingTaskBase<BuildContext>
{
    public override void Run(BuildContext context)
    {
        var dotNetTfmSchema = JsonSchema.FromFile(context.RootDirectory.Combine("assets").Combine("dotnet-target-framework-moniker.schema.json").FullPath);
        SchemaRegistry.Global.Register(dotNetTfmSchema);
        var semVerSchema = JsonSchema.FromFile(context.RootDirectory.Combine("assets").Combine("semver.schema.json").FullPath);
        SchemaRegistry.Global.Register(semVerSchema);
    }
}

[TaskName("Prepare")]
[IsDependentOn(typeof(CleanTask))]
[IsDependentOn(typeof(RegisterJsonSchemasTask))]
public sealed class PrepareTask : AsyncFrostingTaskBase<BuildContext>
{
    public static JsonSerializerOptions GameMetadataSerializerOptions = new()
    {
        Converters =
        {
            new ValidatingJsonConverter(),
        },
        WriteIndented = true,
    };

    public async Task<GameMetadata> DeserializeGameMetadata(BuildContext context)
    {
        if (context.GameDirectory.GetDirectoryName().Equals("Games"))
            throw new ArgumentException("No game folder name provided. Supply one with the '--game [folder name]' switch.");

        context.Log.Information("Deserializing game metadata ...");
        await using FileStream gameDataStream = File.OpenRead(context.GameDirectory.CombineWithFilePath("metadata.json").FullPath);

        return await JsonSerializer.DeserializeAsync<Schema.GameMetadata>(gameDataStream, GameMetadataSerializerOptions)
            ?? throw new ArgumentException("Game metadata could not be deserialized.");
    }

    public async Task<GameVersionMap> DeserializeGameVersions(BuildContext context)
    {
        Matcher versionFileMatcher = new();
        versionFileMatcher.AddInclude("*.json");

        var versionsPath = context.GameDirectory.Combine("versions");
        var versionFileMatches = versionFileMatcher.Execute(
            new DirectoryInfoWrapper(new DirectoryInfo(versionsPath.FullPath))
        ).Files;

        var gameVersions = await Task.WhenAll(
            versionFileMatches
                .Select(match => versionsPath.CombineWithFilePath(match.Path))
                .Select(filePath => DeserializeGameVersion(context, filePath))
        );
        GameVersionMap gameVersionsMap = new();
        foreach (var gameVersion in gameVersions)
        {
            gameVersionsMap[gameVersion.BuildId] = gameVersion;
        }

        return gameVersionsMap;
    }

    public async Task<GameVersionEntry> DeserializeGameVersion(BuildContext context, FilePath gameVersionFilePath)
    {
        await using FileStream versionEntryStream = File.OpenRead(gameVersionFilePath.FullPath);

        return await JsonSerializer.DeserializeAsync<Schema.GameVersionEntry>(versionEntryStream, GameMetadataSerializerOptions)
            ?? throw new ArgumentException($"Game version {gameVersionFilePath.GetFilename()} could not be deserialized.");
    }

    public override async Task RunAsync(BuildContext context)
    {
        context.GameMetadata = await DeserializeGameMetadata(context);
        context.GameVersions = await DeserializeGameVersions(context);
        context.Environment.WorkingDirectory = context.GameDirectory;
    }
}

[TaskName("HandleUnknownSteamBuild")]
[IsDependentOn(typeof(FetchSteamAppInfoTask))]
public sealed class HandleUnknownSteamBuildTask : AsyncFrostingTaskBase<BuildContext>
{
    private async Task SerializeGameMetadata(BuildContext context)
    {
        context.Log.Information("Serializing modified game metadata ...");
        await using FileStream gameDataStream = File.OpenWrite(context.GameDirectory.CombineWithFilePath("metadata.json").FullPath);
        await JsonSerializer.SerializeAsync(
            gameDataStream,
            context.GameMetadata,
            new JsonSerializerOptions
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                WriteIndented = true,
            }
        );
    }

    private async Task SerializeGameVersion(BuildContext context, GameVersionEntry gameVersionEntry)
    {
        context.Log.Information($"Serializing game version entry for build {gameVersionEntry.BuildId} ...");
        var versionsPath = context.GameDirectory.Combine("versions");
        context.EnsureDirectoryExists(versionsPath.FullPath);
        await using FileStream versionDataStream = File.OpenWrite(versionsPath.CombineWithFilePath($"{gameVersionEntry.BuildId}.json").FullPath);
        await JsonSerializer.SerializeAsync(
            versionDataStream,
            gameVersionEntry,
            new JsonSerializerOptions
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                WriteIndented = true,
            }
        );
    }

    private async Task OpenVersionNumberPullRequest(BuildContext context)
    {
        var publicBranchInfo = context.GameAppInfo.Branches["public"];
        if (publicBranchInfo == null) throw new Exception("Current public branch info not found.");

        var branchName = $"{context.GameDirectory.GetDirectoryName()}-build-{publicBranchInfo.BuildId}";
        await context.ProcessAsync(
            new CommandSettings
            {
                ToolName = "git",
                ToolExecutableNames = new[] { "git", "git.exe" },
            },
            new ProcessArgumentBuilder()
                .Append("fetch")
                .Append("--prune")
                .Append("origin")
        );

        // Check if branch exists on origin and delete it (force)
        var branches = context.GitBranches(context.RootDirectory);
        if (branches.Any(branch => branch.FriendlyName == $"origin/{branchName}"))
        {
            Console.WriteLine("Version entry branch already exists on 'origin', deleting and recreating...");

            // Delete remote branch
            try
            {
                context.Command(
                    new CommandSettings
                    {
                        ToolName = "git",
                        ToolExecutableNames = new[] { "git", "git.exe" },
                    },
                    new ProcessArgumentBuilder()
                        .Append("push")
                        .Append("origin")
                        .Append("--delete")
                        .Append(branchName)
                );
                Console.WriteLine("Remote branch deleted successfully.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to delete remote branch: {ex.Message}");
            }
        }

        context.Log.Information("Adding new (partial) version entry to game metadata ...");

        var newVersionEntry = new GameVersionEntry
        {
            BuildId = publicBranchInfo.BuildId,
            TimeUpdated = publicBranchInfo.TimeUpdated,
            GameVersion = "", // Will be updated later by UpdateVersionFromDownloadTask
            Depots = context.GameMetadata.Steam.DistributionDepots.Select(depotPair => depotPair.Value.DepotId)
                .Select(depotId => context.GameAppInfo.Depots[depotId])
                .Select(depot => new SteamGameDepotVersion
                {
                    DepotId = depot.DepotId,
                    ManifestId = depot.Manifests["public"].ManifestId,
                })
                .ToDictionary(depotVersion => depotVersion.DepotId),
            FrameworkTargets = context.GameVersions.Latest()?.FrameworkTargets ?? new List<FrameworkTarget>
            {
                new()
                {
                    TargetFrameworkMoniker = "netstandard2.0",
                    NuGetDependencies = new List<NuGetDependency>()
                },
            },
        };
        await SerializeGameVersion(context, newVersionEntry);

        context.Log.Information("Opening version entry pull request ...");
        context.GitCreateBranch(context.RootDirectory, branchName, true);
        context.GitAdd(context.RootDirectory, context.GameDirectory.CombineWithFilePath("versions"));
        context.InferredGitCommit($"add game version entry for {context.GameAppInfo.Name} build {publicBranchInfo.BuildId}");
        context.Command(
            new CommandSettings
            {
                ToolName = "git",
                ToolExecutableNames = new[] { "git", "git.exe" },
            },
            new ProcessArgumentBuilder()
                .Append("push")
                .Append("--set-upstream")
                .Append("origin")
                .Append(branchName)
        );

        context.Command(
            new CommandSettings
            {
                ToolName = "gh",
                ToolExecutableNames = new[] { "gh", "gh.exe" },
            },
            new ProcessArgumentBuilder()
                .Append("pr")
                .Append("create")
                .AppendSwitch("--title", $"\"[{context.GameDirectory.GetDirectoryName()}] Version entry - Build {publicBranchInfo.BuildId}\"")
                .AppendSwitch(
                    "--body",
                    $"\"Contains partially patched `metadata.json` for {context.GameAppInfo.Name} build {publicBranchInfo.BuildId}.\n" +
                    $"Game version will be automatically populated from version.txt after download.\""
                )
                .AppendSwitch("--head", branchName)
        );

        // Auto-merge the PR
        context.Log.Information("Auto-merging version entry PR...");
        context.Command(
            new CommandSettings
            {
                ToolName = "gh",
                ToolExecutableNames = new[] { "gh", "gh.exe" },
            },
            new ProcessArgumentBuilder()
                .Append("pr")
                .Append("merge")
                .Append(branchName)
                .Append("--merge")
                .Append("--delete-branch")
        );

        context.Log.Information("Version entry PR created and merged automatically.");
    }

    public override async Task RunAsync(BuildContext context)
    {
        var mostRecentKnownVersion = context.GameVersions.Latest();
        if (mostRecentKnownVersion == null)
        {
            // If there are no known versions, open a pull request.
            await OpenVersionNumberPullRequest(context);
            return;
        }

        var currentVersion = context.GameAppInfo.Branches["public"];
        // If the current version is the latest known version, check TimeUpdated matches and do nothing.
        if (currentVersion.BuildId == mostRecentKnownVersion.BuildId)
        {
            if (currentVersion.TimeUpdated != mostRecentKnownVersion.TimeUpdated)
                context.Log.Warning($"TimeUpdated for most recent known version is inaccurate - Should be {currentVersion.TimeUpdated}");

            return;
        }

        // If the current version is known, but not the latest known version, warn and do nothing.
        if (context.GameVersions.Values.Any(version => version.BuildId == currentVersion.BuildId))
        {
            context.Log.Warning("Current version is known, but is not latest?");
            return;
        }

        // If the current version is unknown, open a pull request.
        await OpenVersionNumberPullRequest(context);
    }
}

[TaskName("Fetch NuGet context")]
[IsDependentOn(typeof(PrepareTask))]
public sealed class ListDeployedPackageVersionsTask : NuGetTaskBase
{
    private PackageMetadataResource _packageMetadataResource = null!;
    private readonly FloatRange _absoluteLatestFloatRange = new FloatRange(NuGetVersionFloatBehavior.AbsoluteLatest);

    private readonly Dictionary<PackageIdentity, IList<PackageIdentity>> _resolvedPackageDependencies = new();

    private async Task<IPackageSearchMetadata[]> FetchNuGetPackageMetadata(BuildContext context, string packageId)
    {
        context.Log.Information($"Fetching index for NuGet package '{packageId}'");
        return (await _packageMetadataResource.GetMetadataAsync(packageId, true, false, SourceCache,
                NullLogger.Instance, default))
            .ToArray();
    }

    public override async Task RunAsync(BuildContext context)
    {
        _packageMetadataResource = await SourceRepository.GetResourceAsync<PackageMetadataResource>();

        var deployedPackageMetadata = await Task.WhenAll(
            context.GameMetadata.NuGetPackageNames.Select(name => FetchNuGetPackageMetadata(context, name))
        );

        context.DeployedPackageMetadata = deployedPackageMetadata
            .Zip(context.GameMetadata.NuGetPackageNames)
            .ToDictionary(
                item => item.Second,
                item => item.First.ToList() as IList<IPackageSearchMetadata>
            );
    }
}

[TaskName("CheckPackageBuildIdUpToDate")]
[IsDependentOn(typeof(HandleUnknownSteamBuildTask))]
[IsDependentOn(typeof(ListDeployedPackageVersionsTask))]
public sealed class CheckPackageBuildIdUpToDateTask : AsyncFrostingTaskBase<BuildContext>
{

    public override async Task RunAsync(BuildContext context)
    {
        // Get current build ID from Steam app info
        var currentBuildId = context.GameAppInfo.Branches["public"].BuildId;
        context.Log.Information($"Current build ID: {currentBuildId}");
        
        // Check if this build ID is already processed (has version.json file)
        var versionFilePath = context.GameDirectory.Combine("versions").CombineWithFilePath($"{currentBuildId}.json");
        var isAlreadyProcessed = File.Exists(versionFilePath.FullPath);
        
        context.Log.Information($"Version file path: {versionFilePath.FullPath}");
        context.Log.Information($"Version file exists: {isAlreadyProcessed}");
        
        // List all files in versions directory for debugging
        var versionsDir = context.GameDirectory.Combine("versions");
        if (Directory.Exists(versionsDir.FullPath))
        {
            var files = Directory.GetFiles(versionsDir.FullPath, "*.json");
            context.Log.Information($"Files in versions directory: {string.Join(", ", files)}");
        }
        else
        {
            context.Log.Information("Versions directory does not exist");
        }
        
        // Always include the current build ID if it needs processing
        // (either not processed yet, or processed but needs version update)
        var outdatedBuildIds = new List<long> { currentBuildId };
        var outdatedBuildIdsJson = JsonSerializer.Serialize(outdatedBuildIds);

        var githubOutputFile = Environment.GetEnvironmentVariable("GITHUB_OUTPUT", EnvironmentVariableTarget.Process);
        if (!string.IsNullOrWhiteSpace(githubOutputFile))
        {
            await using var textWriter = new StreamWriter(githubOutputFile!, true, Encoding.UTF8);
            await textWriter.WriteLineAsync("outdated-version-buildIds<<EOF");
            await textWriter.WriteLineAsync(outdatedBuildIdsJson);
            await textWriter.WriteLineAsync("EOF");
        }
        else
        {
            Console.WriteLine($"::set-output name=outdated-version-buildIds::{outdatedBuildIdsJson}");
        }
        
        context.Log.Information($"Final buildIds JSON: {outdatedBuildIdsJson}");
    }
}

[TaskName("DownloadNuGetDependencies")]
[IsDependentOn(typeof(PrepareTask))]
public sealed class DownloadNuGetDependenciesTask : NuGetTaskBase
{
    private static readonly PackageDownloadContext PackageDownloadContext = new(SourceCache);
    private static NuGetPathContext _pathContext = null!;
    private static DownloadResource _downloadResource = null!;
    private static DownloadResource _bepInDownloadResource = null!;

    private async Task<DownloadResourceResult> DownloadNuGetPackageVersion(BuildContext context, PackageIdentity packageIdentity)
    {
        var result = await _downloadResource.GetDownloadResourceResultAsync(
            packageIdentity,
            PackageDownloadContext,
            _pathContext.UserPackageFolder,
            NullLogger.Instance,
            default
        );

        if (result.Status is DownloadResourceResultStatus.Available) return result;

        return await _bepInDownloadResource.GetDownloadResourceResultAsync(
            packageIdentity,
            PackageDownloadContext,
            _pathContext.UserPackageFolder,
            NullLogger.Instance,
            default
        );
    }

    public override async Task RunAsync(BuildContext context)
    {
        _pathContext = NuGetPathContext.Create(context.RootDirectory.FullPath);
        _downloadResource = await SourceRepository.GetResourceAsync<DownloadResource>();
        _bepInDownloadResource = await BepInSourceRepository.GetResourceAsync<DownloadResource>();

        // Get current build ID and find the corresponding version entry
        var currentBuildId = context.GameAppInfo.Branches["public"].BuildId;
        var currentVersionEntry = context.GameVersions.ContainsKey(currentBuildId) 
            ? context.GameVersions[currentBuildId] 
            : context.GameVersions.Latest();

        if (currentVersionEntry == null)
        {
            context.Log.Error($"No version entry found for build {currentBuildId} and no latest version available");
            return;
        }

        var downloadResults = await Task.WhenAll(
            currentVersionEntry.FrameworkTargets
                .SelectMany(target => target.NuGetDependencies)
                .Select(dependency => DownloadNuGetPackageVersion(context, dependency.ToPackageIdentity()))
        );

        context.NuGetPackageDownloadResults = downloadResults
            .ToDictionary(result => result.PackageReader.GetIdentity());
    }
}

[TaskName("CacheDependencyAssemblyNames")]
[IsDependentOn(typeof(DownloadNuGetDependenciesTask))]
public sealed class CacheDependencyAssemblyNamesTask : AsyncFrostingTaskBase<BuildContext>
{
    private async Task<IEnumerable<string>> DependencyAssemblyNamesForTfmFromPackage(
        BuildContext context,
        FrameworkTarget target,
        PackageReaderBase packageReader,
        CancellationToken token
    )
    {
        var itemEnumerables = await Task.WhenAll(
            GetLibItems(),
            GetRefItems(),
            GetBuildItems()
        );

        var items = itemEnumerables.SelectMany(itemEnumerable => itemEnumerable);
        var itemFileNames = items.Select(item => Path.GetFileName(item))
            .Where(fileName => Path.GetExtension(fileName) == ".dll");

        return itemFileNames.ToHashSet();

        async Task<IEnumerable<string>> GetLibItems()
        {
            var libItemGroups = await packageReader.GetLibItemsAsync(token);
            var libItems = NuGetFrameworkUtility.GetNearest(libItemGroups, target.Framework, group => group.TargetFramework);
            if (libItems is null) return Enumerable.Empty<string>();
            return libItems.Items;
        }

        async Task<IEnumerable<string>> GetRefItems()
        {
            var refItemGroups = await packageReader.GetReferenceItemsAsync(token);
            var refItems = NuGetFrameworkUtility.GetNearest(refItemGroups, target.Framework, group => group.TargetFramework);
            if (refItems is null) return Enumerable.Empty<string>();
            return refItems.Items;
        }

        async Task<IEnumerable<string>> GetBuildItems()
        {
            var buildItemGroups = await packageReader.GetBuildItemsAsync(token);
            var buildItems = NuGetFrameworkUtility.GetNearest(buildItemGroups, target.Framework, group => group.TargetFramework);
            if (buildItems is null) return Enumerable.Empty<string>();
            return buildItems.Items;
        }
    }

    private async Task<HashSet<string>> DependencyAssemblyNamesForTfm(
        BuildContext context,
        FrameworkTarget target,
        CancellationToken token
    )
    {
        var packageReaders = target.NuGetDependencies
            .Select(dependency => dependency.ToPackageIdentity())
            .Select(identity => context.NuGetPackageDownloadResults[identity].PackageReader);

        var packageAssemblyNames = await Task.WhenAll(
            packageReaders.Select(async reader => await DependencyAssemblyNamesForTfmFromPackage(context, target, reader, token))
        );

        return packageAssemblyNames
            .SelectMany(x => x)
            .ToHashSet();
    }

    public override async Task RunAsync(BuildContext context)
    {
        // Get current build ID and find the corresponding version entry
        var currentBuildId = context.GameAppInfo.Branches["public"].BuildId;
        var currentVersionEntry = context.GameVersions.ContainsKey(currentBuildId) 
            ? context.GameVersions[currentBuildId] 
            : context.GameVersions.Latest();

        if (currentVersionEntry == null)
        {
            context.Log.Error($"No version entry found for build {currentBuildId} and no latest version available");
            return;
        }

        var perFrameworkDependencyAssemblies = await Task.WhenAll(
            currentVersionEntry.FrameworkTargets
                .Select(
                    async target => await DependencyAssemblyNamesForTfm(context, target, CancellationToken.None)
                )
        );

        context.FrameworkTargetDependencyAssemblyNames = currentVersionEntry.FrameworkTargets
            .Zip(perFrameworkDependencyAssemblies)
            .ToDictionary(
                item => item.First.Framework,
                item => item.Second as ISet<string>
            );
    }
}

[TaskName("ProcessAssemblies")]
[IsDependentOn(typeof(SteamDownloadDepotsTask))]
[IsDependentOn(typeof(CacheDependencyAssemblyNamesTask))]
public sealed class ProcessAssembliesTask : AsyncFrostingTaskBase<BuildContext>
{
    private Matcher AssemblyMatcher { get; } = new();
    private Matcher PublicizeMatcher { get; } = new();
    private Dictionary<int, FilePatternMatch[]> DepotAssemblies { get; } = new();
    private Dictionary<int, DirectoryPath> DepotDataDirectories { get; } = new();
    private readonly object _processingTasksLock = new();

    private AssemblyPublicizerOptions StripAndPublicise { get; } = new()
    {
        Target = PublicizeTarget.All,
        Strip = true,
        IncludeOriginalAttributesAttribute = true,
    };

    private AssemblyPublicizerOptions StripOnly { get; } = new()
    {
        Target = PublicizeTarget.None,
        Strip = true,
        IncludeOriginalAttributesAttribute = true,
    };

    private DirectoryPath DataDirectory(BuildContext context, int depotId)
    {
        if (DepotDataDirectories.TryGetValue(depotId, out var dataDirectory)) return dataDirectory;
        var computedResult = ComputeDataDirectory(context, depotId);
        DepotDataDirectories[depotId] = computedResult;
        return computedResult;
    }

    private DirectoryPath ComputeDataDirectory(BuildContext context, int depotId)
    {
        var depotDirectory = context.GameDirectory.Combine("steam").Combine($"depot_{depotId}");

        var windowsExe = Directory.EnumerateFiles(depotDirectory.FullPath, "*.exe")
            .FirstOrDefault(filePath => !Path.GetFileName(filePath).StartsWith("UnityCrashHandler"));
        if (windowsExe != null)
        {
            return depotDirectory.Combine($"{Path.GetFileNameWithoutExtension(windowsExe)}_Data");
        }

        var linuxExe = Directory
            .EnumerateFiles(depotDirectory.FullPath, "*.x86_64")
            .FirstOrDefault(filePath => !Path.GetFileName(filePath).StartsWith("UnityCrashHandler"));
        if (linuxExe != null)
        {
            return depotDirectory.Combine($"{Path.GetFileNameWithoutExtension(linuxExe)}_Data");
        }

        var macOsApp = Directory.EnumerateFiles(depotDirectory.FullPath, "*.app").FirstOrDefault();
        if (macOsApp != null)
        {
            return new DirectoryPath(macOsApp)
                .Combine("Content")
                .Combine("Resources")
                .Combine("Data");
        }

        throw new ArgumentException("Unsupported distribution platform - couldn't find executable/app bundle.");
    }

    private DirectoryPath ManagedDirectory(BuildContext context, int depotId) =>
        DataDirectory(context, depotId).Combine("Managed");

    private DirectoryPath DepotTargetNupkgRefsDirectory(BuildContext context, SteamGameDistributionDepot depot, NuGetFramework framework)
        => context.GameDirectory
            .Combine("nupkgs")
            .Combine($"{context.GameMetadata.NuGet.Name}{depot.PackageSuffix}")
            .Combine("ref")
            .Combine(framework.GetShortFolderName());

    private async Task ProcessAndCopyAssemblyForDepotTarget(BuildContext context, SteamGameDistributionDepot depot, NuGetFramework framework, FilePatternMatch fileMatch)
    {
        var filePath = ManagedDirectory(context, depot.DepotId).CombineWithFilePath(fileMatch.Path);
        var fileName = filePath.GetFilename().FullPath;

        if (fileName.EndsWith("-stubs.dll")) return;

        var processedFilePath = filePath.GetDirectory()
            .CombineWithFilePath($"{filePath.GetFilenameWithoutExtension()}-stubs.dll");

        var dependencyAssemblyNames = context.FrameworkTargetDependencyAssemblyNames[framework];
        if (dependencyAssemblyNames.Contains(fileName)) return;

        bool processingHasStarted;
        Task? processingCompleted;
        TaskCompletionSource? processingCompletedSource = null;

        lock (_processingTasksLock)
        {
            if (File.Exists(processedFilePath.FullPath))
            {
                context.AssemblyProcessingTasks[filePath.FullPath] = Task.CompletedTask;
            }

            processingHasStarted = context.AssemblyProcessingTasks.TryGetValue(filePath.FullPath, out processingCompleted);

            if (!processingHasStarted)
            {
                processingCompletedSource = new TaskCompletionSource();
                processingCompleted = processingCompletedSource.Task;
                context.AssemblyProcessingTasks[filePath.FullPath] = processingCompleted;
            }
        }

        if (!processingHasStarted)
        {
            var shouldPublicise = PublicizeMatcher.Match(fileMatch.Path).HasMatches;
            var options = shouldPublicise ? StripAndPublicise : StripOnly;
            context.Log.Information($"Stripping {(shouldPublicise ? "and publicising " : "")}{depot.DepotId}/{fileName}...");
            AssemblyPublicizer.Publicize(
                filePath.FullPath,
                processedFilePath.FullPath,
                options
            );
            processingCompletedSource!.SetResult();
        }

        await (processingCompleted ?? Task.CompletedTask);

        await using FileStream source = File.Open(processedFilePath.FullPath, FileMode.Open);
        await using FileStream destination = File.Create(DepotTargetNupkgRefsDirectory(context, depot, framework).CombineWithFilePath(fileName).FullPath);
        await source.CopyToAsync(destination);
    }

    private async Task CopyAssembliesForDepotTarget(BuildContext context, SteamGameDistributionDepot depot, NuGetFramework tfm)
    {
        Task ProcessAndCopyAssembly(FilePatternMatch path) => ProcessAndCopyAssemblyForDepotTarget(context, depot, tfm, path);
        context.EnsureDirectoryExists(DepotTargetNupkgRefsDirectory(context, depot, tfm).FullPath);

        await Task.WhenAll(
            DepotAssemblies[depot.DepotId]
                .Select(ProcessAndCopyAssembly)
        );
    }

    private async Task CopyAssembliesForDepot(BuildContext context, SteamGameDistributionDepot depot)
    {
        DepotAssemblies[depot.DepotId] = AssemblyMatcher.Execute(
            new DirectoryInfoWrapper(new DirectoryInfo(ManagedDirectory(context, depot.DepotId).FullPath))
        ).Files.ToArray();

        // Get current build ID and find the corresponding version entry
        var currentBuildId = context.GameAppInfo.Branches["public"].BuildId;
        var currentVersionEntry = context.GameVersions.ContainsKey(currentBuildId) 
            ? context.GameVersions[currentBuildId] 
            : context.GameVersions.Latest();

        if (currentVersionEntry == null)
        {
            context.Log.Error($"No version entry found for build {currentBuildId} and no latest version available");
            return;
        }

        Task CopyAssembliesForTarget(NuGetFramework tfm) => CopyAssembliesForDepotTarget(context, depot, tfm);
        await Task.WhenAll(
            currentVersionEntry.FrameworkTargets
                .Select(target => target.Framework)
                .Select(CopyAssembliesForTarget)
        );
    }

    public override async Task RunAsync(BuildContext context)
    {
        context.AssemblyProcessingTasks = new();

        AssemblyMatcher.AddInclude("*.dll");
        AssemblyMatcher.AddExcludePatterns(context.GameMetadata.ProcessSettings.ExcludeAssemblies);

        PublicizeMatcher.AddIncludePatterns(context.GameMetadata.ProcessSettings.AssembliesToPublicise);

        await Task.WhenAll(
            context.GameMetadata.Steam.DistributionDepots.Values.Select(
                depot => CopyAssembliesForDepot(context, depot)
            )
        );
    }
}

[TaskName("MakePackages")]
[IsDependentOn(typeof(ListDeployedPackageVersionsTask))]
[IsDependentOn(typeof(ProcessAssembliesTask))]
public sealed class MakePackagesTask : AsyncFrostingTaskBase<BuildContext>
{
    private DirectoryPath DepotNupkgSourceDirectoryPath(BuildContext context, SteamGameDistributionDepot depot)
        => context.GameDirectory
            .Combine("nupkgs")
            .Combine($"{context.GameMetadata.NuGet.Name}{depot.PackageSuffix}");

    private FilePath DepotNupkgPackedFilePath(BuildContext context, SteamGameDistributionDepot depot)
        => context.GameDirectory
            .Combine("nupkgs")
            .CombineWithFilePath($"{context.GameMetadata.NuGet.Name}{depot.PackageSuffix}.nupkg");

    private int NextRevisionNumber(IEnumerable<IPackageSearchMetadata> packageVersions, string packageId, string versionBase)
    {
        Regex pattern = new($@"^{Regex.Escape(versionBase)}-{BuildContext.DehumidifierVersionDiscriminatorPrefix}\.(\d+)$", RegexOptions.Compiled);

        try
        {
            return packageVersions
                .Where(version => version.Identity.Id.Equals(packageId))
                .Select(version => version.Identity.Version)
                .Select(version => pattern.Match(version.ToString()))
                .Where(match => match.Success)
                .Select(match => int.Parse(match.Groups[1].Value))
                .Max() + 1;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }

    }

    public async Task MakeDepotPackage(BuildContext context, SteamGameDistributionDepot depot)
    {
        // Get current build ID and find the corresponding version entry
        var currentBuildId = context.GameAppInfo.Branches["public"].BuildId;
        var currentVersionEntry = context.GameVersions.ContainsKey(currentBuildId) 
            ? context.GameVersions[currentBuildId] 
            : context.GameVersions.Latest();

        if (currentVersionEntry == null)
        {
            context.Log.Error($"No version entry found for build {currentBuildId} and no latest version available");
            return;
        }

        var id = $"{context.GameMetadata.NuGet.Name}{depot.PackageSuffix}";
        var allVersions = context.DeployedPackageMetadata
            .Values
            .SelectMany(packageVersions => packageVersions);
        var nextRevision = NextRevisionNumber(allVersions, id, currentVersionEntry.GameVersion);

        ManifestMetadata metadata = new()
        {
            Id = id,
            Version = new NuGetVersion($"{currentVersionEntry.GameVersion}-{BuildContext.DehumidifierVersionDiscriminatorPrefix}.{nextRevision}"),
            Authors = context.GameMetadata.NuGet.Authors ?? ["sod-mod"],
            Description = context.GameMetadata.NuGet.Description
                          + "\n\nGenerated and managed by GameLib Dehumidifier.",
            DependencyGroups = currentVersionEntry.FrameworkTargets.Select(
                target => new PackageDependencyGroup(
                    NuGetFramework.Parse(target.TargetFrameworkMoniker),
                    target.NuGetDependencies.Select(dependency => new PackageDependency(
                        dependency.Name,
                        new VersionRange(new NuGetVersion(dependency.Version))
                    ))
                )
            )
        };

        metadata.SetProjectUrl("https://github.com/sod-mod/ShapeOfDreams.GameLibs");

        ManifestFile[] files = [
            new()
            {
                Source = "ref/**",
                Target = "ref"
            },
            new()
            {
                Source = "README.md",
                Target = "README.md"
            }
        ];

        Manifest nuspec = new(metadata, files);

        var builder = new PackageBuilder();
        builder.Populate(nuspec.Metadata);
        builder.PopulateFiles(DepotNupkgSourceDirectoryPath(context, depot).FullPath, nuspec.Files);

        await using FileStream stream = File.Open(DepotNupkgPackedFilePath(context, depot).FullPath, FileMode.OpenOrCreate);
        builder.Save(stream);
    }

    public override async Task RunAsync(BuildContext context)
    {
        Func<SteamGameDistributionDepot, Task> makeDepotPackage = depot => MakeDepotPackage(context, depot);
        await Task.WhenAll(
            context.GameMetadata.Steam.DistributionDepots.Values.Select(makeDepotPackage)
        );
    }
}

[TaskName("PushNuGetPackages")]
[IsDependentOn(typeof(MakePackagesTask))]
public sealed class PushNuGetTask : FrostingTaskBase<BuildContext>
{
    public override void Run(BuildContext context)
    {
        var nugetPath = context.GameDirectory.Combine("nupkgs");
        var settings = new DotNetNuGetPushSettings
        {
            Source = "https://api.nuget.org/v3/index.json",
            ApiKey = context.NugetApiKey,
            SkipDuplicate = true,
        };
        foreach (var pkg in context.GetFiles(nugetPath.Combine("*.nupkg").FullPath))
            context.DotNetNuGetPush(pkg, settings);
    }
}

[TaskName("UpdateVersionFromDownload")]
[IsDependentOn(typeof(SteamDownloadDepotsTask))]
public sealed class UpdateVersionFromDownloadTask : AsyncFrostingTaskBase<BuildContext>
{
    private async Task<string> ReadVersionFromFile(BuildContext context)
    {
        // Try to find version.txt in downloaded depot directories
        var steamDir = context.GameDirectory.Combine("steam");
        if (Directory.Exists(steamDir.FullPath))
        {
            var depotDirs = Directory.GetDirectories(steamDir.FullPath, "depot_*");
            foreach (var depotDir in depotDirs)
            {
                var depotVersionFile = Path.Combine(depotDir, "version.txt");
                if (File.Exists(depotVersionFile))
                {
                    try
                    {
                        var versionText = await File.ReadAllTextAsync(depotVersionFile);
                        var rawVersion = versionText.Trim();
                        var gameVersion = ExtractVersionNumber(context, rawVersion);
                        context.Log.Information($"Found version.txt in depot {Path.GetFileName(depotDir)} with raw version: {rawVersion}, extracted: {gameVersion}");
                        return gameVersion;
                    }
                    catch (Exception ex)
                    {
                        context.Log.Warning($"Failed to read version.txt from depot {Path.GetFileName(depotDir)}: {ex.Message}");
                    }
                }
            }
        }

        context.Log.Warning("version.txt not found in game root or any depot directory");
        return "";
    }

    private string ExtractVersionNumber(BuildContext context, string rawVersion)
    {
        // Handle formats like "r.1.0.9.9_s" -> "1.0.9.9"
        // Remove common prefixes and suffixes
        var version = rawVersion.Trim();

        // Remove "r." prefix if present
        if (version.StartsWith("r."))
        {
            version = version.Substring(2);
        }

        // Remove "_s" suffix if present
        if (version.EndsWith("_s"))
        {
            version = version.Substring(0, version.Length - 2);
        }

        // Remove other common suffixes like "_beta", "_alpha", etc.
        var suffixes = new[] { "_beta", "_alpha", "_dev", "_test", "_rc", "_pre" };
        foreach (var suffix in suffixes)
        {
            if (version.EndsWith(suffix))
            {
                version = version.Substring(0, version.Length - suffix.Length);
                break;
            }
        }

        // Validate that it looks like a version number (contains dots and numbers)
        if (version.Contains('.') && version.Any(char.IsDigit))
        {
            return version;
        }

        // If no valid version found, return the original string
        context.Log.Warning($"Could not extract valid version from: {rawVersion}");
        return rawVersion;
    }

    private async Task<bool> UpdateVersionEntry(BuildContext context, string gameVersion)
    {
        // Get current build ID from Steam app info instead of using TargetVersion
        var currentBuildId = context.GameAppInfo.Branches["public"].BuildId;
        var versionFilePath = context.GameDirectory.Combine("versions").CombineWithFilePath($"{currentBuildId}.json");

        if (!File.Exists(versionFilePath.FullPath))
        {
            throw new FileNotFoundException($"Version file not found: {versionFilePath.FullPath}");
        }

        // Read existing version entry
        GameVersionEntry versionEntry;
        using (var versionStream = File.OpenRead(versionFilePath.FullPath))
        {
            versionEntry = await JsonSerializer.DeserializeAsync<GameVersionEntry>(versionStream, new JsonSerializerOptions
            {
                Converters = { new ValidatingJsonConverter() }
            });
        }

        if (versionEntry == null)
        {
            throw new InvalidOperationException("Failed to deserialize version entry");
        }

        // Check if game version is already the same
        if (versionEntry.GameVersion == gameVersion)
        {
            context.Log.Information($"Game version is already {gameVersion}, no update needed");
            return false;
        }

        // Update game version
        versionEntry.GameVersion = gameVersion;

        // Write back to file
        using (var writeStream = File.OpenWrite(versionFilePath.FullPath))
        {
            await JsonSerializer.SerializeAsync(writeStream, versionEntry, new JsonSerializerOptions
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                WriteIndented = true,
            });
        }

        context.Log.Information($"Updated version entry with game version: {gameVersion}");
        return true;
    }

    private Task CreateAndMergeVersionUpdatePR(BuildContext context, string gameVersion)
    {
        // Get current build ID from Steam app info instead of using TargetVersion
        var currentBuildId = context.GameAppInfo.Branches["public"].BuildId;
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var branchName = $"{context.GameDirectory.GetDirectoryName()}-version-update-{currentBuildId}-{timestamp}";

        // Create branch
        context.GitCreateBranch(context.RootDirectory, branchName, true);

        // Add changes
        context.GitAdd(context.RootDirectory, context.GameDirectory.CombineWithFilePath("versions"));

        // Commit changes
        context.InferredGitCommit($"Update game version to {gameVersion} for build {currentBuildId}");

        // Push branch
        context.Command(
            new CommandSettings
            {
                ToolName = "git",
                ToolExecutableNames = new[] { "git", "git.exe" },
            },
            new ProcessArgumentBuilder()
                .Append("push")
                .Append("--set-upstream")
                .Append("origin")
                .Append(branchName)
        );

        // Create and merge PR
        context.Command(
            new CommandSettings
            {
                ToolName = "gh",
                ToolExecutableNames = new[] { "gh", "gh.exe" },
            },
            new ProcessArgumentBuilder()
                .Append("pr")
                .Append("create")
                .AppendSwitch("--title", $"\"[{context.GameDirectory.GetDirectoryName()}] Update game version to {gameVersion} - Build {currentBuildId}\"")
                .AppendSwitch(
                    "--body",
                    $"\"Updated game version to {gameVersion} for {context.GameAppInfo.Name} build {currentBuildId}.\n" +
                    $"Version was automatically extracted from version.txt file.\""
                )
                .AppendSwitch("--head", branchName)
        );

        // Auto-merge the PR
        context.Log.Information("Auto-merging version update PR...");
        context.Command(
            new CommandSettings
            {
                ToolName = "gh",
                ToolExecutableNames = new[] { "gh", "gh.exe" },
            },
            new ProcessArgumentBuilder()
                .Append("pr")
                .Append("merge")
                .Append(branchName)
                .Append("--merge")
                .Append("--delete-branch")
        );

        context.Log.Information("Version update PR created and merged automatically.");
        return Task.CompletedTask;
    }

    public override async Task RunAsync(BuildContext context)
    {
        var gameVersion = await ReadVersionFromFile(context);

        if (string.IsNullOrEmpty(gameVersion))
        {
            context.Log.Warning("No version found, skipping update");
            return;
        }

        try
        {
            var wasUpdated = await UpdateVersionEntry(context, gameVersion);
            if (!wasUpdated)
            {
                context.Log.Information("No version update needed, skipping PR creation");
                return;
            }

            await CreateAndMergeVersionUpdatePR(context, gameVersion);
        }
        catch (Exception ex)
        {
            context.Log.Error($"Failed to update version entry: {ex.Message}");
            throw;
        }
    }
}

[TaskName("Default")]
[IsDependentOn(typeof(MakePackagesTask))]
public class DefaultTask : FrostingTask { }