// ******************************************************************
// Copyright � 2015-2018 nventive inc. All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//
// ******************************************************************
using Microsoft.Build.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Runtime.Serialization;
using Uno.SourceGeneration.Helpers;
using System.Threading;
using Uno.SourceGeneration.Host.Helpers;
using Uno.SourceGeneration.Host.GenerationClient;
using Uno.SourceGeneration.Host.Messages;
using Uno.SourceGeneratorTasks.Helpers;
using System.Runtime.InteropServices;
using Uno.SourceGeneration.Host;

[assembly: CommitHashAttribute("<developer build>")]

namespace Uno.SourceGeneratorTasks
{
	public class SourceGenerationTask_v0 : Microsoft.Build.Utilities.Task
	{
		[Required]
		public string ProjectFile { get; set; }

		[Required]
		public string Platform { get; set; }

		[Required]
		public string Configuration { get; set; }

		public string TargetFramework { get; set; }

		public string VisualStudioVersion { get; set; }

		public string TargetFrameworkRootPath { get; set; }

		/// <summary>
		/// Capture the generation host standard output for debug purposes.
		/// (used when <see cref="UseGenerationController"/> is set to false)
		/// </summary>
		public string CaptureGenerationHostOutput { get; set; }

		/// <summary>
		/// Enables the use of the Generation Controller mode.
		/// </summary>
		public string UseGenerationController { get; set; } = bool.TrueString;

		/// <summary>
		/// Enables the use of the Generation Host mode.
		/// </summary>
		public string UseGenerationHost { get; set; } = bool.TrueString;

		/// <summary>
		/// Provides a list of assemblies to be loaded in the SourceGenerator
		/// secondary app domains. This is a backward compatibility feature related
		/// to the use of external libraries in previous versions of the SourceGeneration task.
		/// </summary>
		public string[] AdditionalAssemblies { get; set; }

		[Required]
		public string[] SourceGenerators { get; set; }

		public string OutputPath { get; set; }

		public string BinLogOutputPath { get; set; }

		public bool BinLogEnabled { get; set; }

		public string SharedGenerationId { get; set; }

		public bool LaunchDebugger { get; set; }

		[Required]
		public Microsoft.Build.Framework.ITaskItem[] ReferencePath { get; set; }

		[Required]
		public Microsoft.Build.Framework.ITaskItem[] AdditionalProperties { get; set; }

		[Output]
		public string[] GenereratedFiles { get; set; }

		private CancellationTokenSource _sharedCompileCts;

		public override bool Execute()
		{
			string lockFile = null;

			Log.LogMessage(MessageImportance.Low, $"Running generation in {Process.GetCurrentProcess().Id}/{Process.GetCurrentProcess().ProcessName}");

			if (LaunchDebugger)
			{
				Debugger.Launch();
			}

			try
			{
				lockFile = Path.Combine(OutputPath, "unoGenerator.lock");

				if (File.Exists(lockFile))
				{
					// This may happen during the initial load of the project. At this point
					// there is no need to re-generate the files.
					return true;
				}

				if (SupportsGenerationController)
				{
					GenerateWithHostController();
				}
				else if(SupportsGenerationHost)
				{
					GenerateWithHost();
				}
				else
				{
					throw new Exception("In-process generation is not supported");
				}

				return true;
			}
			catch (Exception e)
			{
				var aggregate = e as AggregateException;

				if (aggregate != null)
				{
					this.Log.LogError(string.Join(", ", aggregate.InnerExceptions.Select(ie => ie.Message)));
				}
				else
				{
					this.Log.LogError(e.Message);
				}

				this.Log.LogMessage(e.ToString());

				return false;
			}
			finally
			{
				if (File.Exists(lockFile))
				{
					File.Delete(lockFile);
				}
			}
		}

		public bool SupportsGenerationController
			=> (bool.TryParse(UseGenerationController, out var result) && result)
			&& !RuntimeHelpers.IsNetCore
			&& (
				!RuntimeHelpers.IsMono
				|| RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
			);

		public bool SupportsGenerationHost
			=> (bool.TryParse(UseGenerationHost, out var result) && result)
			&& (
				// MacOS with MSBuild 16.0+ or Linux
				IsMonoMSBuildCompatible

				// .NET Core
				|| RuntimeHelpers.IsNetCore

				// Desktop Windows
				|| (!RuntimeHelpers.IsNetCore && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			);

		private void GenerateWithHostController()
		{
			Log.LogMessage(MessageImportance.Low, "Using host controller generation mode");

			using (_sharedCompileCts = new CancellationTokenSource())
			{
				var responseFile = Path.GetTempFileName();
				var outputFile = Path.GetTempFileName();
				var binlogFile = Path.GetTempFileName() + ".binlog";

				try
				{ 
					var buildEnvironment = CreateBuildEnvironment();

					using (var responseStream = File.OpenWrite(responseFile))
					{
						var serializer = new DataContractSerializer(typeof(BuildEnvironment));
						serializer.WriteObject(responseStream, buildEnvironment);
					}

					// Note: using ToolArguments here (the property) since
					// commandLineCommands (the parameter) may have been mucked with
					// (to support using the dotnet cli)
					var responseTask = GenerationServerConnection.RunServerGeneration(
						GenerateServerId(buildEnvironment),
						new List<string> { responseFile, outputFile, binlogFile },
						new GenerationsPathsInfo(
							GetHostPath(),
							Path.GetDirectoryName(ProjectFile), // This is required by many msbuild tasks, particularly when using globbing patterns
							Path.GetTempPath()
						),
						keepAlive: null,
						cancellationToken: _sharedCompileCts.Token);

					responseTask.Wait(_sharedCompileCts.Token);

					BinaryLoggerReplayHelper.Replay(BuildEngine, binlogFile, Log);

					if (responseTask.Result.Type == GenerationResponse.ResponseType.Completed)
					{
						GenereratedFiles = File.ReadAllText(outputFile).Split(';');
					}
					else
					{
						throw new InvalidOperationException($"Generation failed, error code {responseTask.Result.Type}");
					}
				}
				finally
				{
					File.Delete(responseFile);
					File.Delete(outputFile);
					File.Delete(binlogFile);
				}
			}
		}

		private string GenerateServerId(BuildEnvironment buildEnvironment)
		{
			return GenerationServerConnection.GetPipeNameForPathOpt(
				string.Concat(
					GetHostPath(),
					Process.GetCurrentProcess().Id.ToString()
				)
			);
		}

		private void GenerateWithHost()
		{
			Log.LogMessage(MessageImportance.Low, $"Using single-use host generation mode");

			var captureHostOutput = false;
			if (!bool.TryParse(this.CaptureGenerationHostOutput, out captureHostOutput))
			{
#if DEBUG
				captureHostOutput = true; // default to true in DEBUG
#else
				captureHostOutput = false; // default to false in RELEASE
#endif
			}

			var hostPath = GetHostPath();

			var responseFile = Path.GetTempFileName();
			var outputFile = Path.GetTempFileName();
			var binlogFile = Path.GetTempFileName() + ".binlog";

			try
			{
				using (var responseStream = File.OpenWrite(responseFile))
				{
					var serializer = new DataContractSerializer(typeof(BuildEnvironment));
					serializer.WriteObject(responseStream, CreateBuildEnvironment());
				}

				ProcessStartInfo buildInfo()
				{
					if (RuntimeHelpers.IsNetCore)
					{
						var hostBinPath = Path.Combine(hostPath, "Uno.SourceGeneration.Host.dll");
						string arguments = $"\"{hostBinPath}\" \"{responseFile}\" \"{outputFile}\" \"{binlogFile}\"";
						var pi = new ProcessStartInfo("dotnet", arguments)
						{
							UseShellExecute = false,
							CreateNoWindow = true,
							WindowStyle = ProcessWindowStyle.Hidden,
						};

						return pi;
					}
					else
					{
						var hostBinPath = Path.Combine(hostPath, "Uno.SourceGeneration.Host.exe");
						string arguments = $"\"{responseFile}\" \"{outputFile}\" \"{binlogFile}\"";

						var pi = new ProcessStartInfo(hostBinPath, arguments)
						{
							UseShellExecute = false,
							CreateNoWindow = true,
							WindowStyle = ProcessWindowStyle.Hidden,
						};

						return pi;
					}
				}

				using (var process = new Process())
				{
					var startInfo = buildInfo();

					if (LaunchDebugger)
					{
						startInfo.Arguments += " -debuggerlaunch";
					}

					if (captureHostOutput)
					{
						startInfo.Arguments += " -console";
						startInfo.RedirectStandardOutput = true;
						startInfo.RedirectStandardError = true;

						process.StartInfo = startInfo;
						process.Start();

						var output = process.StandardOutput.ReadToEnd();
						var error = process.StandardError.ReadToEnd();
						process.WaitForExit();

						Log.LogMessage(
							$"Executing {startInfo.FileName} {startInfo.Arguments}:\n" +
							$"result: {process.ExitCode}\n" +
							$"\n---begin host output---\n{output}\n" +
							$"---begin host ERROR output---\n{error}\n" +
							"---end host output---\n");
					}
					else
					{
						process.StartInfo = startInfo;
						process.Start();
						process.WaitForExit();
					}

					BinaryLoggerReplayHelper.Replay(BuildEngine, binlogFile, Log);

					if (process.ExitCode == 0)
					{
						GenereratedFiles = File.ReadAllText(outputFile).Split(';');
					}
					else
					{
						throw new InvalidOperationException($"Generation failed, error code {process.ExitCode}");
					}
				}
			}
			finally
			{
				File.Delete(responseFile);
				File.Delete(outputFile);
				File.Delete(binlogFile);
			}
		}


		private string GetHostPath()
		{
			var currentPath = Path.GetDirectoryName(new Uri(GetType().Assembly.CodeBase).LocalPath);
			var hostPlatform = RuntimeHelpers.IsNetCore ? GetNetCoreHostVersion() : "net472";
			var installedPath = Path.Combine(currentPath, "..", "..", "host", hostPlatform);
#if DEBUG
			var configuration = "Debug";
#else
			var configuration = "Release";
#endif

			var devPath = Path.Combine(currentPath, "..", "..", "..", "..", "Uno.SourceGeneration.Host", "bin", configuration, hostPlatform);

			if (Directory.Exists(devPath))
			{
				return devPath;
			}
			else if (Directory.Exists(installedPath))
			{
				return installedPath;
			}
			else
			{
				throw new InvalidOperationException($"Unable to find Uno.SourceGeneration.Host.dll (in {devPath} or {installedPath})");
			}
		}

		private static string GetNetCoreHostVersion()
			// .NET Core 3.1.3
			// .NET 5.0.0-preview.6.20305.6
			=> RuntimeInformation.FrameworkDescription.StartsWith(".NET Core") ? "netcoreapp3.1" : "net" + RuntimeInformation.FrameworkDescription.Replace(".NET ", "")[0];

		public bool IsMonoMSBuildCompatible =>
			// Starting from vs16.0 the following errors does not happen. Below this version, we continue to use
			// the current process to run the generators.
			// 
			// System.TypeInitializationException: The type initializer for 'Microsoft.Build.Collections.MSBuildNameIgnoreCaseComparer' threw an exception. ---> System.EntryPointNotFoundException: GetSystemInfo
			//   at(wrapper managed-to-native) Microsoft.Build.Shared.NativeMethodsShared.GetSystemInfo(Microsoft.Build.Shared.NativeMethodsShared/SYSTEM_INFO&)
			//   at Microsoft.Build.Shared.NativeMethodsShared+SystemInformationData..ctor ()[0x00023] in <61115f75067146fab35b10183e6ee379>:0 
			//   at Microsoft.Build.Shared.NativeMethodsShared.get_SystemInformation ()[0x0001e] in <61115f75067146fab35b10183e6ee379>:0 
			//   at Microsoft.Build.Shared.NativeMethodsShared.get_ProcessorArchitecture ()[0x00000] in <61115f75067146fab35b10183e6ee379>:0 
			//   at Microsoft.Build.Collections.MSBuildNameIgnoreCaseComparer..cctor ()[0x00010] in <61115f75067146fab35b10183e6ee379>:0 
			//    --- End of inner exception stack trace ---
			//   at Microsoft.Build.Collections.PropertyDictionary`1[T]..ctor ()[0x00006] in <61115f75067146fab35b10183e6ee379>:0 
			//   at Microsoft.Build.Evaluation.ProjectCollection..ctor (System.Collections.Generic.IDictionary`2[TKey, TValue] globalProperties, System.Collections.Generic.IEnumerable`1[T] loggers, System.Collections.Generic.IEnumerable`1[T] remoteLoggers, Microsoft.Build.Evaluation.ToolsetDefinitionLocations toolsetDefinitionLocations, System.Int32 maxNodeCount, System.Boolean onlyLogCriticalEvents) [0x00112] in <61115f75067146fab35b10183e6ee379>:0 
			//   at Microsoft.Build.Evaluation.ProjectCollection..ctor(System.Collections.Generic.IDictionary`2[TKey, TValue] globalProperties, System.Collections.Generic.IEnumerable`1[T] loggers, Microsoft.Build.Evaluation.ToolsetDefinitionLocations toolsetDefinitionLocations) [0x00000] in <61115f75067146fab35b10183e6ee379>:0 
			//   at Microsoft.Build.Evaluation.ProjectCollection..ctor(System.Collections.Generic.IDictionary`2[TKey, TValue] globalProperties) [0x00000] in <61115f75067146fab35b10183e6ee379>:0 
			//   at Microsoft.Build.Evaluation.ProjectCollection..ctor() [0x00000] in <61115f75067146fab35b10183e6ee379>:0 
			//   at Uno.SourceGeneration.Host.ProjectLoader.LoadProjectDetails(Uno.SourceGeneratorTasks.BuildEnvironment environment) [0x00216] in <b845ad5dce324939bc8243d198321524>:0 
			//   at Uno.SourceGeneration.Host.SourceGeneratorHost.Generate() [0x00014] in <b845ad5dce324939bc8243d198321524>:0 

			string.Compare(FileVersionInfo.GetVersionInfo(new Uri(typeof(Microsoft.Build.Utilities.Task).Assembly.Location).LocalPath).FileVersion, "16.0") >= 0
			|| RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

		private BuildEnvironment CreateBuildEnvironment()
			=> new BuildEnvironment
			{
				Configuration = Configuration,
				Platform = Platform,
				ProjectFile = ProjectFile,
				OutputPath = EnsureRootedPath(ProjectFile, OutputPath),
				TargetFramework = TargetFramework,
				VisualStudioVersion = VisualStudioVersion,
				TargetFrameworkRootPath = TargetFrameworkRootPath,
				BinLogOutputPath = EnsureRootedPath(ProjectFile, BinLogOutputPath),
				BinLogEnabled = BinLogEnabled,
				MSBuildBinPath = Path.GetDirectoryName(new Uri(typeof(Microsoft.Build.Logging.ConsoleLogger).Assembly.CodeBase).LocalPath),
				AdditionalAssemblies = AdditionalAssemblies,
				SourceGenerators = SourceGenerators,
				ReferencePath = ReferencePath.Select(r => r.ItemSpec).ToArray(),
				AdditionalProperties = AdditionalProperties
					.ToDictionary(v => v.ItemSpec, v => v.GetMetadata("Value")),
			};

		private string EnsureRootedPath(string projectFile, string targetPath) =>
			Path.IsPathRooted(targetPath)
			? targetPath
			: Path.Combine(Path.GetDirectoryName(projectFile), targetPath);
	}
}
