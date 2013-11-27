﻿namespace GitFlowVersionTask
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using GitFlowVersion;
    using Microsoft.Build.Framework;
    using Microsoft.Build.Utilities;
    using Logger = GitFlowVersion.Logger;


    public class UpdateAssemblyInfo : Task
    {

        public bool SignAssembly { get; set; }

        [Required]
        public string SolutionDirectory { get; set; }

        [Required]
        public string ProjectFile { get; set; }

        [Required]
        public ITaskItem[] CompileFiles { get; set; }

        [Output]
        public string AssemblyInfoTempFilePath { get; set; }


        public override bool Execute()
        {
            try
            {
                TempFileTracker.DeleteTempFiles();

                Logger.WriteInfo = this.LogInfo;

                //TODO: TeamCity.IsRunningInBuildAgent is leaking implementation details should abstract it
                bool isRunningOnATeamCityBuildAgent = TeamCity.IsRunningInBuildAgent();

                var gitDirectory = GitDirFinder.TreeWalkForGitDir(SolutionDirectory);
                if (string.IsNullOrEmpty(gitDirectory))
                {
                    if (isRunningOnATeamCityBuildAgent) //fail the build if we're on a TC build agent
                    {
                        // ReSharper disable once StringLiteralTypo
                        this.LogError("Failed to find .git directory on agent. Please make sure agent checkout mode is enabled for you VCS roots - http://confluence.jetbrains.com/display/TCD8/VCS+Checkout+Mode");
                        return false;
                    }

                    var message = string.Format("No .git directory found in solution path '{0}'. This means the assembly may not be versioned correctly. To fix this warning either clone the repository using git or remove the `GitFlowVersion.Fody` nuget package. To temporarily work around this issue add a AssemblyInfo.cs with an appropriate `AssemblyVersionAttribute`.", SolutionDirectory);
                    this.LogWarning(message);

                    return true;
                }
                foreach (var compileFile in GetInvalidFiles())
                {
                    this.LogError("File contains assembly version attributes with conflict with the attributes generated by GitFlowVersion", compileFile);
                    return false;
                }

                if (isRunningOnATeamCityBuildAgent)
                {
                    TeamCity.NormalizeGitDirectory(gitDirectory);
                }

                var versionAndBranch = VersionCache.GetVersion(gitDirectory);

                WriteTeamCityParameters(versionAndBranch);
                CreateTempAssemblyInfo(versionAndBranch);

                return true;
            }
            catch (ErrorException errorException)
            {
                this.LogError(errorException.Message);
                return false;
            }
            catch (Exception exception)
            {
                this.LogError("Error occurred: " + exception);
                return false;
            }
            finally
            {
                Logger.Reset();
            }
        }

        void WriteTeamCityParameters(VersionAndBranch versionAndBranch)
        {
            foreach (var buildParameters in TeamCity.GenerateBuildLogOutput(versionAndBranch))
            {
                this.LogWarning(buildParameters);
            }
        }

        bool FileContainsVersionAttribute(string compileFile)
        {
            var combine = Path.Combine(Path.GetDirectoryName(ProjectFile), compileFile);
            var allText = File.ReadAllText(combine);
            return allText.Contains("AssemblyVersion") ||
                   allText.Contains("AssemblyFileVersion") ||
                   allText.Contains("AssemblyInformationalVersion");
        }

        void CreateTempAssemblyInfo(VersionAndBranch versionAndBranch)
        {
            var assemblyInfoBuilder = new AssemblyInfoBuilder
                                      {
                                          VersionAndBranch = versionAndBranch,
                                          SignAssembly = SignAssembly
                                      };
            var assemblyInfo = assemblyInfoBuilder.GetAssemblyInfoText();

            var tempFileName = string.Format("AssemblyInfo_{0}_{1}.cs", Path.GetFileNameWithoutExtension(ProjectFile), Path.GetRandomFileName());
            AssemblyInfoTempFilePath = Path.Combine(TempFileTracker.TempPath, tempFileName);
            File.WriteAllText(AssemblyInfoTempFilePath, assemblyInfo);
        }


        IEnumerable<string> GetInvalidFiles()
        {
            return CompileFiles.Select(x => x.ItemSpec)
                               .Where(compileFile => compileFile.Contains("AssemblyInfo"))
                               .Where(FileContainsVersionAttribute);
        }

    }
}