﻿// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using FluentAssertions;
using Microsoft.DotNet.Cli.Utils;
using Microsoft.Extensions.DependencyModel;
using Microsoft.NET.TestFramework;
using Microsoft.NET.TestFramework.Assertions;
using Microsoft.NET.TestFramework.Commands;
using Microsoft.NET.TestFramework.ProjectConstruction;
using Newtonsoft.Json.Linq;
using NuGet.Common;
using NuGet.Frameworks;
using NuGet.ProjectModel;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Xunit;
using Xunit.Abstractions;
using Microsoft.NET.Build.Tasks;
using NuGet.Versioning;

namespace Microsoft.NET.Build.Tests
{
    public class GivenThatWeWantToUseFrameworkReferenceIn2x : SdkTest
    {
        private const string AspNetProgramSource = @"
using System;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;

public class Program
{
    public static void Main(string[] args)
    {
        WebHost.CreateDefaultBuilder(args).Build().Run();
    }
}
";

        public GivenThatWeWantToUseFrameworkReferenceIn2x(ITestOutputHelper log) : base(log)
        {
        }

        [Theory]
        //  TargetFramework, FrameworkReference, ExpectedPackageVersion
        [InlineData("netcoreapp2.1", "Microsoft.AspNetCore.App", "2.1.1")]
        [InlineData("netcoreapp2.1", "Microsoft.AspNetCore.ALl", "2.1.1")]
        [InlineData("netcoreapp2.2", "Microsoft.AspNetCore.App", "2.2.0")]
        [InlineData("netcoreapp2.2", "Microsoft.AspNetCore.All", "2.2.0")]
        public void It_targets_a_known_runtime_framework_name(
            string targetFramework,
            string frameworkReferenceName,
            string expectedPackageVersion)
        {
            string testIdentifier = "SharedRuntimeTargeting_" + string.Join("_", targetFramework, frameworkReferenceName);

            var testProject = new TestProject
            {
                Name = "FrameworkTargetTest",
                TargetFrameworks = targetFramework,
                IsSdkProject = true,
                IsExe = true,
                FrameworkReferences =
                {
                    new TestFrameworkReference(frameworkReferenceName)
                }
            };

            testProject.SourceFiles["Program.cs"] = AspNetProgramSource;

            var testAsset = _testAssetsManager.CreateTestProject(testProject, testIdentifier);

            var restoreCommand = testAsset.GetRestoreCommand(Log, relativePath: testIdentifier);
            restoreCommand.Execute()
                .Should().Pass()
                .And
                .NotHaveStdOutContaining("warning");

            var buildCommand = new BuildCommand(Log, Path.Combine(testAsset.TestRoot, testProject.Name));

            buildCommand
                .Execute()
                .Should()
                .Pass();

            var outputDirectory = buildCommand.GetOutputDirectory(targetFramework);

            string runtimeConfigFile = Path.Combine(outputDirectory.FullName, testProject.Name + ".runtimeconfig.json");
            string runtimeConfigContents = File.ReadAllText(runtimeConfigFile);
            JObject runtimeConfig = JObject.Parse(runtimeConfigContents);

            string actualRuntimeFrameworkName = ((JValue)runtimeConfig["runtimeOptions"]["framework"]["name"]).Value<string>();
            actualRuntimeFrameworkName.Should().Be(frameworkReferenceName);

            string actualRuntimeFrameworkVersion = ((JValue)runtimeConfig["runtimeOptions"]["framework"]["version"]).Value<string>();
            actualRuntimeFrameworkVersion.Should().Be(expectedPackageVersion);

            string projectAssetsJsonPath = Path.Combine(buildCommand.ProjectRootPath, "obj", "project.assets.json");
            LockFile lockFile = LockFileUtilities.GetLockFile(projectAssetsJsonPath, NullLogger.Instance);

            var target = lockFile.GetTarget(NuGetFramework.Parse(targetFramework), null);
            var packageLibrary = target.Libraries.Single(l => l.Name == frameworkReferenceName);
            packageLibrary.Version.ToString().Should().Be(expectedPackageVersion);

            target.Libraries.Should().Contain(l => l.Name == "Microsoft.NETCore.App");
        }

        [Theory]
        [InlineData("Microsoft.AspNetCore.App")]
        [InlineData("Microsoft.AspNetCore.All")]
        public void It_warns_when_explicit_aspnet_package_ref_exists(string packageId)
        {
            const string testProjectName = "AspNetCoreWithExplicitRef";
            var project = new TestProject
            {
                Name = testProjectName,
                TargetFrameworks = "netcoreapp2.1",
                IsSdkProject = true,
                PackageReferences =
                {
                    new TestPackageReference(packageId, "2.1.0")
                }
            };

            var testAsset = _testAssetsManager
                .CreateTestProject(project)
                .Restore(Log, project.Name);

            string projectAssetsJsonPath = Path.Combine(
                testAsset.Path,
                project.Name,
                "obj",
                "project.assets.json");

            var restoreCommand =
                testAsset.GetRestoreCommand(Log, relativePath: testProjectName);
            restoreCommand.Execute()
                .Should().Pass()
                .And
                .HaveStdOutContaining("warning NETSDK1071:")
                .And
                .HaveStdOutContaining(testProjectName + ".csproj");

            LockFile lockFile = LockFileUtilities.GetLockFile(
                projectAssetsJsonPath,
                NullLogger.Instance);

            var target =
                lockFile.GetTarget(NuGetFramework.Parse(".NETCoreApp,Version=v2.1"), null);
            var metapackageLibrary =
                target.Libraries.Single(l => l.Name == packageId);
            metapackageLibrary.Version.ToString().Should().Be("2.1.0");
        }

        [Fact]
        public void It_fails_when_unknown_framework_reference_is_used()
        {
            TestProject project = new TestProject()
            {
                Name = "UnknownFrameworkRefence",
                TargetFrameworks = "netcoreapp2.1",
                IsSdkProject = true,
                FrameworkReferences =
                {
                    new TestFrameworkReference("Banana.App")
                }
            };

            var testAsset = _testAssetsManager.CreateTestProject(project);
            var restoreCommand = testAsset.GetRestoreCommand(Log, project.Name);

            restoreCommand.Execute()
                .Should()
                .Fail()
                .And
                .HaveStdOutContaining("error NETSDK1072:");
        }

        [Fact]
        public void It_generates_deps_file_for_aspnet_app()
        {
            TestProject project = new TestProject()
            {
                Name = "AspNetCore21App",
                TargetFrameworks = "netcoreapp2.1",
                IsExe = true,
                IsSdkProject = true,
                FrameworkReferences =
                {
                    new TestFrameworkReference("Microsoft.AspNetCore.App")
                }
            };

            project.SourceFiles["Program.cs"] = AspNetProgramSource;

            var testAsset = _testAssetsManager.CreateTestProject(project)
                .Restore(Log, project.Name);

            string projectFolder = Path.Combine(testAsset.Path, project.Name);

            var buildCommand = new BuildCommand(Log, projectFolder);

            buildCommand
                .Execute()
                .Should()
                .Pass();

            string outputFolder = buildCommand.GetOutputDirectory(project.TargetFrameworks).FullName;

            using (var depsJsonFileStream = File.OpenRead(Path.Combine(outputFolder, $"{project.Name}.deps.json")))
            {
                var dependencyContext = new DependencyContextJsonReader().Read(depsJsonFileStream);
                dependencyContext.Should()
                    .OnlyHaveRuntimeAssemblies("", project.Name)
                    .And
                    .HaveNoDuplicateRuntimeAssemblies("")
                    .And
                    .HaveNoDuplicateNativeAssets(""); ;
            }
        }
    }
}
