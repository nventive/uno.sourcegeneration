using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Uno.SourceGeneratorTasks;
using Uno.SourceGeneratorTasks.Helpers;

namespace Uno.SourceGeneration.Host
{
	public class AssemblyResolver
	{
		private static bool _additionalAssembliesPreloaded;

		public static IDisposable RegisterAssemblyLoader(BuildEnvironment environment)
		{
			// Force assembly loader to consider siblings, when running in a separate appdomain.
			Assembly localResolve(AssemblyName e)
			{
				if (e.Name == "Mono.Runtime")
				{
					// Roslyn 2.0 and later checks for the presence of the Mono runtime
					// through this check.
					return null;
				}

				var assembly = new AssemblyName(e.Name);
				var basePath = Path.GetDirectoryName(new Uri(typeof(Program).Assembly.CodeBase).LocalPath);

				typeof(AssemblyResolver).Log().Debug($"Searching for [{assembly}] from [{basePath}]");

				// Ignore resource assemblies for now, we'll have to adjust this
				// when adding globalization.
				if (assembly.Name.EndsWith(".resources"))
				{
					return null;
				}

				TryLoadAdditionalAssemblies(environment);

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
						typeof(AssemblyResolver).Log().Debug($"Selecting first occurrence of assembly [{e.Name}] which can be found at [{duplicates.Select(d => d.CodeBase).JoinBy("; ")}]");
					}

					return loadedAsm[0];
				}
				else if (assembly.Version != null && loadedAsm.FirstOrDefault()?.GetName().Version == assembly.Version)
				{
					return loadedAsm[0];
				}

				Assembly LoadAssembly(string filePath)
				{
					if (File.Exists(filePath))
					{
						try
						{
#if NETCOREAPP
							var output = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(filePath);
#else
							var output = Assembly.LoadFrom(filePath);
#endif

							typeof(AssemblyResolver).Log().Debug($"Loaded [{output.GetName()}] from [{output.CodeBase}]");

							return output;
						}
						catch (Exception ex)
						{
							typeof(AssemblyResolver).Log().Debug($"Failed to load [{assembly}] from [{filePath}]", ex);
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
					Path.Combine(environment.MSBuildBinPath, assembly.Name + ".dll"),
				};

				return paths
					.Select(LoadAssembly)
					.Where(p => p != null)
					.FirstOrDefault();
			}

#if NET472
			Assembly appDomainResolve(object s, ResolveEventArgs e)
				=> localResolve(new AssemblyName(e.Name));

			AppDomain.CurrentDomain.AssemblyResolve += appDomainResolve;
			AppDomain.CurrentDomain.TypeResolve += appDomainResolve;

			return new Disposable(() => {
				AppDomain.CurrentDomain.AssemblyResolve -= appDomainResolve;
				AppDomain.CurrentDomain.TypeResolve -= appDomainResolve;
			});
#elif NETCOREAPP
			var ctx = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(typeof(Program).Assembly);

			Assembly contextResolve(object s, AssemblyName e)
				=> localResolve(e);

			ctx.Resolving += contextResolve;

			return new Disposable(() => ctx.Resolving -= contextResolve);
#endif
		}
		
		private static void TryLoadAdditionalAssemblies(BuildEnvironment environment)
		{
			if (!_additionalAssembliesPreloaded)
			{
				_additionalAssembliesPreloaded = true;

				foreach (var assemblyPath in environment.AdditionalAssemblies ?? new string[0])
				{
					try
					{
						var assembly = Assembly.LoadFrom(assemblyPath);
						typeof(AssemblyResolver).Log().Debug($"Preloaded additional assembly [{assembly.FullName}] from [{assemblyPath}]");
					}
					catch (Exception e)
					{
						typeof(AssemblyResolver).Log().Debug($"Failed to load additional assembly from [{assemblyPath}]", e);
					}
				}
			}
		}

		private class Disposable : IDisposable
		{
			private readonly Action _action;

			public Disposable(Action action) => _action = action;

			public void Dispose() => _action();
		}
	}
}
