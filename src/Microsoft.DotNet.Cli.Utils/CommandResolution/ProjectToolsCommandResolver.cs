﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.DotNet.InternalAbstractions;
using Microsoft.DotNet.ProjectModel;
using Microsoft.DotNet.Tools.Common;
using Microsoft.Extensions.DependencyModel;
using NuGet.Frameworks;
using NuGet.LibraryModel;
using NuGet.ProjectModel;
using NuGet.Versioning;
using FileFormatException = Microsoft.DotNet.ProjectModel.FileFormatException;

namespace Microsoft.DotNet.Cli.Utils
{
    public class ProjectToolsCommandResolver : ICommandResolver
    {
        private static readonly NuGetFramework s_toolPackageFramework = FrameworkConstants.CommonFrameworks.NetCoreApp10;

        private static readonly CommandResolutionStrategy s_commandResolutionStrategy =
            CommandResolutionStrategy.ProjectToolsPackage;

        private List<string> _allowedCommandExtensions;
        private IPackagedCommandSpecFactory _packagedCommandSpecFactory;

        public ProjectToolsCommandResolver(IPackagedCommandSpecFactory packagedCommandSpecFactory)
        {
            _packagedCommandSpecFactory = packagedCommandSpecFactory;

            _allowedCommandExtensions = new List<string>()
            {
                FileNameSuffixes.DotNet.DynamicLib
            };
        }

        public CommandSpec Resolve(CommandResolverArguments commandResolverArguments)
        {
            if (commandResolverArguments.CommandName == null
                || commandResolverArguments.ProjectDirectory == null)
            {
                return null;
            }

            return ResolveFromProjectTools(
                commandResolverArguments.CommandName,
                commandResolverArguments.CommandArguments.OrEmptyIfNull(),
                commandResolverArguments.ProjectDirectory);
        }

        private CommandSpec ResolveFromProjectTools(
            string commandName,
            IEnumerable<string> args,
            string projectDirectory)
        {
            var lockFile = new LockFileFormat().Read(Path.Combine(projectDirectory, LockFileFormat.LockFileName));
            var tools = lockFile.Tools.Where(t => t.Name.Contains(".NETCoreApp")).SelectMany(t => t.Libraries);

            return ResolveCommandSpecFromAllToolLibraries(
                tools,
                commandName,
                args,
                lockFile);
        }

        private CommandSpec ResolveCommandSpecFromAllToolLibraries(
            IEnumerable<LockFileTargetLibrary> toolsLibraries,
            string commandName,
            IEnumerable<string> args,
            LockFile lockFile)
        {
            foreach (var toolLibrary in toolsLibraries)
            {
                var commandSpec = ResolveCommandSpecFromToolLibrary(toolLibrary, commandName, args, lockFile);

                if (commandSpec != null)
                {
                    return commandSpec;
                }
            }

            return null;
        }

        private CommandSpec ResolveCommandSpecFromToolLibrary(
            LockFileTargetLibrary toolLibraryRange,
            string commandName,
            IEnumerable<string> args,
            LockFile lockFile)
        {
            var nugetPackagesRoot = lockFile.PackageFolders.First().Path;

            var toolLockFile = GetToolLockFile(toolLibraryRange, nugetPackagesRoot);

            var toolLibrary = toolLockFile.Targets
                .FirstOrDefault(
                    t => t.TargetFramework.GetShortFolderName().Equals(s_toolPackageFramework.GetShortFolderName()))
                ?.Libraries.FirstOrDefault(l => l.Name == toolLibraryRange.Name);

            if (toolLibrary == null)
            {
                return null;
            }

            var depsFileRoot = Path.GetDirectoryName(toolLockFile.Path);
            var depsFilePath = GetToolDepsFilePath(toolLibraryRange, toolLockFile, depsFileRoot);

            var normalizedNugetPackagesRoot = PathUtility.EnsureNoTrailingDirectorySeparator(nugetPackagesRoot);

            return _packagedCommandSpecFactory.CreateCommandSpecFromLibrary(
                    toolLibrary,
                    commandName,
                    args,
                    _allowedCommandExtensions,
                    normalizedNugetPackagesRoot,
                    s_commandResolutionStrategy,
                    depsFilePath,
                    null);
        }

        private LockFile GetToolLockFile(
            LockFileTargetLibrary toolLibrary,
            string nugetPackagesRoot)
        {
            var lockFilePath = GetToolLockFilePath(toolLibrary, nugetPackagesRoot);

            if (!File.Exists(lockFilePath))
            {
                return null;
            }

            LockFile lockFile = null;

            try
            {
                lockFile = new LockFileFormat().Read(lockFilePath);
            }
            catch (FileFormatException ex)
            {
                throw ex;
            }

            return lockFile;
        }

        private string GetToolLockFilePath(
            LockFileTargetLibrary toolLibrary,
            string nugetPackagesRoot)
        {
            var toolPathCalculator = new ToolPathCalculator(nugetPackagesRoot);

            return toolPathCalculator.GetBestLockFilePath(
                toolLibrary.Name,
                new VersionRange(toolLibrary.Version),
                s_toolPackageFramework);
        }

        private ProjectContext GetProjectContextFromDirectoryForFirstTarget(string projectRootPath)
        {
            if (projectRootPath == null)
            {
                return null;
            }

            if (!File.Exists(Path.Combine(projectRootPath, Project.FileName)))
            {
                return null;
            }

            var projectContext = ProjectContext.CreateContextForEachTarget(projectRootPath).FirstOrDefault();

            return projectContext;
        }

        private string GetToolDepsFilePath(
            LockFileTargetLibrary toolLibrary,
            LockFile toolLockFile,
            string depsPathRoot)
        {
            var depsJsonPath = Path.Combine(
                depsPathRoot,
                toolLibrary.Name + FileNameSuffixes.DepsJson);

            EnsureToolJsonDepsFileExists(toolLockFile, depsJsonPath);

            return depsJsonPath;
        }

        private void EnsureToolJsonDepsFileExists(
            LockFile toolLockFile,
            string depsPath)
        {
            if (!File.Exists(depsPath))
            {
                GenerateDepsJsonFile(toolLockFile, depsPath);
            }
        }

        // Need to unit test this, so public
        public void GenerateDepsJsonFile(
            LockFile toolLockFile,
            string depsPath)
        {
            Reporter.Verbose.WriteLine($"Generating deps.json at: {depsPath}");

            var projectContext = new ProjectContextBuilder()
                .WithLockFile(toolLockFile)
                .WithTargetFramework(s_toolPackageFramework.ToString())
                .Build();

            var exporter = projectContext.CreateExporter(Constants.DefaultConfiguration);

            var dependencyContext = new DependencyContextBuilder()
                .Build(null,
                    null,
                    exporter.GetAllExports(),
                    true,
                    s_toolPackageFramework,
                    string.Empty);

            var tempDepsFile = Path.GetTempFileName();
            using (var fileStream = File.Open(tempDepsFile, FileMode.Open, FileAccess.Write))
            {
                var dependencyContextWriter = new DependencyContextWriter();

                dependencyContextWriter.Write(dependencyContext, fileStream);
            }

            try
            {
                File.Copy(tempDepsFile, depsPath);
            }
            catch (Exception e)
            {
                Reporter.Verbose.WriteLine($"unable to generate deps.json, it may have been already generated: {e.Message}");
            }
            finally
            {
                try
                {
                    File.Delete(tempDepsFile);
                }
                catch (Exception e2)
                {
                    Reporter.Verbose.WriteLine($"unable to delete temporary deps.json file: {e2.Message}");
                }
            }
        }
    }
}
