using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Uno.SourceGeneration.Host.Helpers;
using Uno.SourceGeneration.Host.Server;
using Uno.SourceGeneratorTasks.Helpers;

[assembly: CommitHashAttribute("<developer build>")]

namespace Uno.SourceGeneration.Host
{
	partial class Program
	{
		static int Main(string[] args)
		{
			if (args.Any(a => a.StartsWith("-debuggerlaunch")))
			{
				Debugger.Launch();
			}

			if (args.Any(a => a.StartsWith("-pipename:")))
			{
				return RunGenerationServer(args);
			}
			else
			{
				return RunSingleUseGeneration(args);
			}
		}

		private static int RunGenerationServer(string[] args)
		{
			LogExtensionPoint.AmbientLoggerFactory.AddProvider(new ConsoleLoggerProvider((t, l) => true, true));
			return new DesktopGenerationServerController(new System.Collections.Specialized.NameValueCollection()).Run(args);
		}
	}
}
