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
using Microsoft.Build.Utilities;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Permissions;
using System.Text;
using System.Threading.Tasks;
using Uno.SourceGeneration.Host;
using Uno.SourceGeneratorTasks.Helpers;
using Uno.SourceGeneratorTasks.Logger;
using System.Diagnostics;
using System.Globalization;

namespace Uno.SourceGeneratorTasks
{
	public class RemoteSourceGeneratorEngine : MarshalByRefObject
	{
		private readonly RemoteLoggerProvider _remoteLoggerProvider = new RemoteLoggerProvider();
		private bool _additionalAssembliesLoaded;
		private bool _initialized;

		public RemoteSourceGeneratorEngine()
		{
		}

		public string MSBuildBasePath { get; set; }

		public string[] AdditionalAssemblies { get; set; }

		public void Initialize()
		{
			if (!_initialized)
			{
				_initialized = true;

				LogExtensionPoint.AmbientLoggerFactory.AddProvider(_remoteLoggerProvider);
				LogExtensionPoint.AmbientLoggerFactory.AddDebug();

				// Apply the workaround before registsering assembly loader to avoid
				// invalid lookups.
				ApplyCacheFolderMSBuildWorkaround();

				ApplyVS4MacWorkarounds();

				RegisterAssemblyLoader();

                AppDomain.CurrentDomain.DomainUnload += (s, e) =>
                {
                    try
                    {
                        this.Log().Debug($"Unloading domain ({AppDomain.CurrentDomain.FriendlyName}");
                    }
                    catch (Exception)
                    {
                    }
                };
			}
		}

		public override object InitializeLifetimeService()
		{
			// Keep this object alive infinitely, it will be deleted along with the 
			// host msbuild.exe process.
			return null;
		}

		internal string[] Generate(RemotableLogger2 logger, BuildEnvironment environment)
		{
			if (!_initialized)
			{
				throw new InvalidOperationException("Initialize must be called before calling Generate");
			}

			_remoteLoggerProvider.TaskLog = logger;

			return new SourceGeneratorEngine(
				environment: environment
#if IS_BUILD_HOST
				, assemblyReferenceProvider: Uno.SourceGeneration.Host.Server.DesktopGenerationServerHost.SharedAssemblyReferenceProvider
#else
				, null
#endif
			).Generate();
		}

		private void RegisterAssemblyLoader()
		{
			// Force assembly loader to consider siblings, when running in a separate appdomain.

			ResolveEventHandler localResolve = (s, e) =>
			{
				if (e.Name == "Mono.Runtime")
				{
					// Roslyn 2.0 and later checks for the presence of the Mono runtime
					// through this check.
					return null;
				}

				var assembly = new AssemblyName(e.Name);
				var basePath = Path.GetDirectoryName(new Uri(this.GetType().Assembly.CodeBase).LocalPath);

				this.Log().Debug($"Searching for [{assembly}] from [{basePath}]");

				// Ignore resource assemblies for now, we'll have to adjust this
				// when adding globalization.
				if (assembly.Name.EndsWith(".resources"))
				{
					return null;
				}

				TryLoadAdditionalAssemblies();

				// Lookup for the highest version matching assembly in the current app domain.
				// There may be an existing one that already matches, even though the 
				// fusion loader did not find an exact match.
				var loadedAsm = (
									from asm in AppDomain.CurrentDomain.GetAssemblies()
									where asm.GetName().Name == assembly.Name
									orderby asm.GetName().Version descending
									select asm
								).ToArray();

				if (loadedAsm.Length > 1)
				{
					var duplicates = loadedAsm
						.Where(a => a.GetName().Version == loadedAsm[0].GetName().Version)
						.ToArray();

					if (duplicates.Length != 0)
					{
						this.Log().Warn($"Selecting first occurrence of assembly [{e.Name}] which can be found at [{duplicates.Select(d => d.CodeBase).JoinBy("; ")}]");
					}

					return loadedAsm[0];
				}
				else if (loadedAsm.Length == 1)
				{
					return loadedAsm[0];
				}

				Assembly LoadAssembly(string filePath)
				{
					if (File.Exists(filePath))
					{
						try
						{
							var output = Assembly.LoadFrom(filePath);

							this.Log().Debug($"Loaded [{output.GetName()}] from [{output.CodeBase}]");

							return output;
						}
						catch (Exception ex)
						{
							this.Log().Debug($"Failed to load [{assembly}] from [{filePath}]", ex);
							return null;
						}
					}
					else
					{
						return null;
					}
				}

				var paths = new[] {
					Path.Combine(basePath, assembly.Name + ".dll"),
					Path.Combine(MSBuildBasePath, assembly.Name + ".dll"),
				};

				return paths
					.Select(LoadAssembly)
					.Where(p => p != null)
					.FirstOrDefault();
			};

			AppDomain.CurrentDomain.AssemblyResolve += localResolve;
			AppDomain.CurrentDomain.TypeResolve += localResolve;
		}

		private void TryLoadAdditionalAssemblies()
		{
			if (!_additionalAssembliesLoaded)
			{
				_additionalAssembliesLoaded = true;

				foreach (var assemblyPath in AdditionalAssemblies ?? new string[0])
				{
					try
					{
						var assembly = Assembly.LoadFrom(assemblyPath);
						this.Log().Debug($"Preloaded additional assembly [{assembly.FullName}] from [{assemblyPath}]");
					}
					catch (Exception e)
					{
						this.Log().Debug($"Failed to load additional assembly from [{assemblyPath}]", e);
					}
				}
			}
		}

		private static void ApplyVS4MacWorkarounds()
		{
			if (IsMono)
			{
				// This is a workaround for the loading of the VS4Mac cached version 
				// of this assembly when running the generation task from inside VS4Mac.
				// This will load the assembly located in the source generator binary folder.
				// This is not required for the CLI, though.
				Assembly.Load("System.Reflection.Metadata");
			}
		}

		private void ApplyCacheFolderMSBuildWorkaround()
		{
			// This is a workaround for a race condition that can be created when the generation tasks
			// are run inside an AppDomain.
			// This field https://github.com/Microsoft/msbuild/blob/d8d43bca5bfe28e62c22211ddd1c5578cf3e37f8/src/Build/BackEnd/Components/Scheduler/Scheduler.cs#L1525
			// is static per app domain, and creates a file that may be discarded in cases of concurrency when the same ID is being used twice, and the following error can happen:
			//
			// UNHANDLED EXCEPTIONS FROM PROCESS 15024:
			// System.IO.FileNotFoundException: Could not find file 'C:\Users\build-svc-defpool\AppData\Local\Temp\MSBuild15024\Configuration191.cache'.
			// File name: 'C:\Users\build-svc-defpool\AppData\Local\Temp\MSBuild15024\Configuration191.cache'
			//    at System.IO.__Error.WinIOError(Int32 errorCode, String maybeFullPath)
			//    at System.IO.FileStream.Init(String path, FileMode mode, FileAccess access, Int32 rights, Boolean useRights, FileShare share, Int32 bufferSize, FileOptions options, SECURITY_ATTRIBUTES secAttrs, String msgPath, Boolean bFromProxy, Boolean useLongPath, Boolean checkHost)
			//    at System.IO.FileStream..ctor(String path, FileMode mode, FileAccess access, FileShare share)
			//    at Microsoft.Build.BackEnd.BuildRequestConfiguration.GetConfigurationTranslator(TranslationDirection direction)
			//    at Microsoft.Build.BackEnd.BuildRequestConfiguration.RetrieveFromCache()
			//    at Microsoft.Build.BackEnd.BuildRequestEngine.ActivateBuildRequest(BuildRequestEntry entry)
			//    at Microsoft.Build.BackEnd.BuildRequestEngine.<>c__DisplayClass39_0.<SubmitBuildRequest>b__0()
			//    at Microsoft.Build.BackEnd.BuildRequestEngine.<>c__DisplayClass67_0.<QueueAction>b__0()
			//
			//
			// The only way to avoid this, considering only one generation task can run per process, and per app domain, is to change the msbuild cache path specifically
			// for the source generation app domain.
			//
			// This field https://github.com/Microsoft/msbuild/blob/0591c15d6c638cad38091fbe625dde968f86748d/src/Shared/FileUtilities.cs#L44
			// is being forcibly set to a non-conflicting value before any msbuild code is run.
			//
			// This code will be removed when this issue is resolved: https://github.com/nventive/Uno.SourceGeneration/issues/33

			try
			{
				var path = Path.Combine(MSBuildBasePath, "Microsoft.Build.dll");

				var asm = Assembly.LoadFrom(path);

				if (asm.GetType("Microsoft.Build.Shared.FileUtilities", false) is Type fileUtilitiesType)
				{
					if (fileUtilitiesType.GetField("cacheDirectory", BindingFlags.NonPublic | BindingFlags.Static) is FieldInfo field)
					{
						var existingcacheDirectory = field.GetValue(null);

						if (existingcacheDirectory == null)
						{
							var cacheDirectory = Path.Combine(Path.GetTempPath(), String.Format(CultureInfo.CurrentUICulture, "MSBuild{0}-SourceGeneration", Process.GetCurrentProcess().Id));

							field.SetValue(null, cacheDirectory);

							System.Console.WriteLine("Applied MSBuild cache path workaround for VS15.9 and below");
						}
						else
						{
							System.Console.WriteLine($"Failed to override internal msbuild cache directory, cacheDirectory is already set to [{existingcacheDirectory}]");
						}
					}
					else
					{
						System.Console.WriteLine($"Failed to override internal msbuild cache directory, could not find cacheDirectory field");
					}
				}
				else
				{
					System.Console.WriteLine($"Failed to override internal msbuild cache directory, could not find Microsoft.Build.Shared.FileUtilities");
				}
			}
			catch (Exception e)
			{
				System.Console.WriteLine($"Failed to override internal msbuild cache directory {e.Message}");
			}
		}


		/// <summary>
		/// Allows for the remote client to determine if the server is available.
		/// </summary>
		/// <returns></returns>
		internal bool Ping() => true;

		private static bool IsMono => Type.GetType("Mono.Runtime") != null;
	}
}
