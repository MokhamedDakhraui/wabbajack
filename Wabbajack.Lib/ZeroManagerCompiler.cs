﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wabbajack.Common;
using Wabbajack.Lib.CompilationSteps;
using Wabbajack.Lib.Validation;

namespace Wabbajack.Lib
{
    public class ZeroManagerCompiler : ACompiler
    {
        public ZeroManagerCompiler(AbsolutePath sourcePath, AbsolutePath downloadsFolder, Game compilngGame, string listName) : base(1, sourcePath, downloadsFolder, compilngGame)
        {
            SourcePath = sourcePath;
            DownloadsFolder = downloadsFolder;
            CompilingGame = compilngGame;
            ListName = listName;
            VFSCacheName = $"vfs_cache_{sourcePath.ToString().StringSha256Hex()}".RelativeTo(Consts.LocalAppDataPath);
        }

        public string ListName { get; set; }
        public Game CompilingGame { get; set; }
        public AbsolutePath DownloadsFolder { get; set; }

        protected override async Task<bool> _Begin(CancellationToken cancel)
        {
            await Metrics.Send("begin_compiling", ListName);
            if (cancel.IsCancellationRequested) return false;
            Queue.SetActiveThreadsObservable(ConstructDynamicNumThreads(await RecommendQueueSize()));
            UpdateTracker.Reset();
            UpdateTracker.NextStep("Gathering information");
            
                       Utils.Log($"VFS File Location: {VFSCacheName}");

            if (cancel.IsCancellationRequested) return false;
            
            if (VFSCacheName.Exists) 
                await VFS.IntegrateFromFile(VFSCacheName);

            List<AbsolutePath> roots;
            roots = new List<AbsolutePath>
            {
                SourcePath, GamePath, DownloadsFolder
            };

            UpdateTracker.NextStep("Indexing folders");

            if (cancel.IsCancellationRequested) return false;
            await VFS.AddRoots(roots);
            await VFS.WriteToFile(VFSCacheName);
            
            if (cancel.IsCancellationRequested) return false;
            UpdateTracker.NextStep("Cleaning output folder");
            await ModListOutputFolder.DeleteDirectory();
            
            if (cancel.IsCancellationRequested) return false;
            UpdateTracker.NextStep("Inferring metas for game file downloads");
            await InferMetas(DownloadsFolder);
            

            if (cancel.IsCancellationRequested) return false;
            UpdateTracker.NextStep("Reindexing downloads after meta inferring");
            await VFS.AddRoot(DownloadsFolder);
            await VFS.WriteToFile(VFSCacheName);

            if (cancel.IsCancellationRequested) return false;
            UpdateTracker.NextStep("Pre-validating Archives");
            

            // Find all Downloads
            IndexedArchives = (await DownloadsFolder.EnumerateFiles()
                .Where(f => f.WithExtension(Consts.MetaFileExtension).Exists)
                .PMap(Queue, async f => new IndexedArchive(VFS.Index.ByRootPath[f])
                {
                    Name = (string)f.FileName,
                    IniData = f.WithExtension(Consts.MetaFileExtension).LoadIniFile(),
                    Meta = await f.WithExtension(Consts.MetaFileExtension).ReadAllTextAsync()
                })).ToList();

            
            UpdateTracker.NextStep("Finding Install Files");
            ModListOutputFolder.CreateDirectory();

            var sourceFiles = SourcePath.EnumerateFiles()
                .Where(p => p.IsFile)
                .Select(p =>
                {
                    if (!VFS.Index.ByRootPath.ContainsKey(p))
                        Utils.Log($"WELL THERE'S YOUR PROBLEM: {p} {VFS.Index.ByRootPath.Count}");
                    
                    return new RawSourceFile(VFS.Index.ByRootPath[p], p.RelativeTo(SourcePath));
                });

            IndexedFiles = IndexedArchives.SelectMany(f => f.File.ThisAndAllChildren)
                .OrderBy(f => f.NestingFactor)
                .GroupBy(f => f.Hash)
                .ToDictionary(f => f.Key, f => f.AsEnumerable());

            AllFiles.SetTo(sourceFiles
                .DistinctBy(f => f.Path));

            Info($"Found {AllFiles.Count} files to build into mod list");


            if (cancel.IsCancellationRequested) return false;
            UpdateTracker.NextStep("Loading INIs");

            ArchivesByFullPath = IndexedArchives.ToDictionary(a => a.File.AbsoluteName);

            if (cancel.IsCancellationRequested) return false;
            var stack = MakeStack();
            UpdateTracker.NextStep("Running Compilation Stack");
            var results = await AllFiles.PMap(Queue, UpdateTracker, f => RunStack(stack, f));

            var noMatch = results.OfType<NoMatch>().ToArray();
            PrintNoMatches(noMatch);
            if (CheckForNoMatchExit(noMatch)) return false;

            InstallDirectives.SetTo(results.Where(i => !(i is IgnoredDirectly)));

            UpdateTracker.NextStep("Building Patches");
            await BuildPatches();
            
            UpdateTracker.NextStep("Gathering Archives");
            await GatherArchives();
            
            UpdateTracker.NextStep("Including Archive Metadata");
            await IncludeArchiveMetadata();

            UpdateTracker.NextStep("Gathering Metadata");
            await GatherMetaData();

            ModList = new ModList
            {
                GameType = CompilingGame,
                WabbajackVersion = WabbajackVersion,
                Archives = SelectedArchives.ToList(),
                ModManager = ModManager.MO2,
                Directives = InstallDirectives,
                Name = ModListName!,
                Author = ModListAuthor ?? "",
                Description = ModListDescription ?? "",
                Readme = ModlistReadme ?? "",
                Image = ModListImage != default ? ModListImage.FileName : default,
                Website = !string.IsNullOrWhiteSpace(ModListWebsite) ? new Uri(ModListWebsite) : null,
                Version = ModlistVersion ?? new Version(1,0,0,0),
                IsNSFW = ModlistIsNSFW
            };

            UpdateTracker.NextStep("Running Validation");

            await ValidateModlist.RunValidation(ModList);
            UpdateTracker.NextStep("Generating Report");

            GenerateManifest();

            UpdateTracker.NextStep("Exporting Modlist");
            await ExportModList();

            ResetMembers();

            UpdateTracker.NextStep("Done Building Modlist");



            return true;
        }

        private void ResetMembers()
        {
            AllFiles = new List<RawSourceFile>();
            InstallDirectives = new List<Directive>();
            SelectedArchives = new List<Archive>();
        }

        public override AbsolutePath VFSCacheName { get; }
        public override ModManager ModManager { get; }
        public override AbsolutePath GamePath { get; }
        public override AbsolutePath ModListOutputFile { get; }
        public override IEnumerable<ICompilationStep> GetStack()
        {
            throw new System.NotImplementedException();
        }

        public override IEnumerable<ICompilationStep> MakeStack()
        {
            Utils.Log("Generating compilation stack");
            return new List<ICompilationStep>
            {
                new IgnoreInFolder(this, DownloadsFolder),
                new DirectMatch(this),
                new DropAll(this)
            };        
        }
    }
}