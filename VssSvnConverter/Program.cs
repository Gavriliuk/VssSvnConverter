using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using VssSvnConverter.Core;

namespace VssSvnConverter
{
	class Stop : ApplicationException
	{
		public Stop() : base("User terminated") {}
	}

	class Program
	{
		static Options _opts;
		static bool _exit;
		static bool _ask = true;
		static string _logFileName = Path.Combine(Directory.GetCurrentDirectory(), string.Format("{0}-{1:yyMMdd-HHmmss}.log",
			Path.GetFileNameWithoutExtension(Assembly.GetExecutingAssembly().Location), DateTime.Now));

		public static bool Exit => _exit;

		public static void WriteToLog(string message = null)
		{
			File.AppendAllText(_logFileName, message + Environment.NewLine);
		}

		public static void WriteToLogWithTimestamp(string message = null)
		{
			WriteToLog(string.IsNullOrEmpty(message) ? null : string.Format("{0:yyMMdd-HHmmss}: {1}", DateTime.Now, message));
		}

		public static void LogAndConsole(string format = null, params object[] args)
		{
			string message = string.IsNullOrEmpty(format) ? null : string.Format(format, args);
			WriteToLogWithTimestamp(message);
			Console.WriteLine(message);
		}

		public static void LogError(string format, params object[] args)
		{
			string message = "ERROR: " + string.Format(format, args);
			WriteToLogWithTimestamp(message);
			Console.Error.WriteLine(message);
		}

		static Int32 Main(string[] args)
		{
			_opts = new Options(args);

			Application.ApplicationExit += new EventHandler((object sender, EventArgs e) => { _exit = true; });

			Int32 exitCode = 0;
			try
			{
				if (args.Length == 0)
				{
					args = new [] { "ui" };
				}

				if (args.Any(a => a.StartsWith("/help")) || args.Any(a => a.StartsWith("-h")) || args.Any(a => a.StartsWith("--help")))
				{
					ShowHelp();
					return -1;
				}

				var verbs = args
					.Where(a => !a.StartsWith("-"))
					.Select(a => a.ToLowerInvariant())
					.SelectMany(a => {
						if (a== "all")
							return new[] { "build-list", "build-versions", "build-cache", "build-commits", "build-wc", "import-new" };

						return Enumerable.Repeat(a, 1);
					})
					.ToList()
				;

				if (verbs.Count == 0)
				{
					ShowHelp();
					return -1;
				}

				var unkVerb = verbs.FirstOrDefault(v => v != "test" && v != "ui" && v != "build-list" && v != "build-list-stats" && v != "build-versions" && v != "build-links" && v != "build-cache" && v != "build-commits" && v != "build-wc" && v != "import" && v != "import-new" && v != "git-fast-import" && v != "build-scripts");
				if (unkVerb != null)
				{
					ShowHelp(unkVerb);
					return -1;
				}

				if (verbs.Count > 1)
				{
					LogAndConsole("Stages: " + string.Join(", ", verbs) + "\n");
				}

				verbs.ForEach(x => ProcessStage(x, true));
			}
			catch (ApplicationException ex)
			{
				LogError(ex.Message);
				exitCode = 1;
			}
			catch (Exception ex)
			{
				LogError(ex.ToString());
				exitCode = 1;
			}

			if (_ask && !_opts.Ask || exitCode != 0)
			{
				Console.WriteLine("\nPress any key...");
				Console.ReadKey();
			}

			return exitCode;
		}

		public static string GetConfigPath()
		{
			return Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "VssSvnConverter.conf");
		}

		public static void ProcessStage(string verb, bool noPrompt, Action<float> progress = null)
		{
			LogAndConsole("*** Stage: " + verb + " ***\n");

			// read config
			_opts.ReadConfig(GetConfigPath());

			switch (verb)
			{
				case "test":
					LogAndConsole("Test OK");
					break;

				case "ui":
					_ask = false;
					Application.EnableVisualStyles();
					Application.SetCompatibleTextRenderingDefault(false);
					Application.Run(new SimpleUI());
					break;

				case "build-list":
					new ImportListBuilder().Build(_opts);
					LogAndConsole("Next: build-versions");
					break;

				case "build-list-stats":
					new ImportListBuilder().FilterFiles(_opts);
					LogAndConsole("Next: build-versions");
					break;

				case "build-versions":
					new VssVersionsBuilder().Build(_opts, new ImportListBuilder().Load(), progress);
					LogAndConsole("Next: build-cache");
					break;

				case "build-links":
					new LinksBuilder().Build(_opts, new ImportListBuilder().Load());
					LogAndConsole("Next: build-cache");
					break;

				case "build-cache":
					new CacheBuilder(_opts).Build(new VssVersionsBuilder().Load(), progress);
					LogAndConsole("Next: build-commits");
					break;

				case "build-cache-stats":
					new CacheBuilder(_opts).BuildStats(new VssVersionsBuilder().Load());
					LogAndConsole("Next: build-commits");
					break;

				case "build-cache-clear-errors":
					new CacheBuilder(_opts).RemoveCachedErrors();
					LogAndConsole("Next: build-commits");
					break;

				case "build-commits":
					new CommitsBuilder().Build(_opts, new CacheBuilder(_opts).Load());
					LogAndConsole("Next: build-wc");
					break;

				case "build-wc":
					new WcBuilder().Build(_opts, noPrompt);
					LogAndConsole("Next: import");
					break;

				case "git-fast-import":
					new GitFastImportFrontend().Create(_opts, CommitsBuilder.Load());
					break;

				case "import-new":
					new Importer().Import(_opts, CommitsBuilder.Load(), true, noPrompt, progress);
					break;

				case "import":
					new Importer().Import(_opts, CommitsBuilder.Load(), false, noPrompt, progress);
					break;

				case "build-scripts":
					new ScriptsBuilder().Build(_opts, new ImportListBuilder().Load(), new ImportListBuilder().LoadRootTypes());
					break;

				case "try-censors":
					var censors = Importer.LoadCensors(_opts);

					string workTree;
					if (_opts.ImportDriver == "svn")
						workTree = _opts.SvnWorkTreeDir;
					else if (_opts.ImportDriver == "git")
						workTree = _opts.GitRepoDir;
					else if (_opts.ImportDriver == "tfs")
						workTree = _opts.TfsWorkTreeDir;
					else
						throw new Exception("Unknown driver: " + _opts.ImportDriver);

					// make copy of file because it can be hard link to cache
					var curpath = new string[1];
					Action<bool> prepareForEdit = b => {
						var p = curpath[0];
						File.Delete(p);
					};

					foreach (var dir in Directory.EnumerateDirectories(workTree))
					{
						var fn = Path.GetFileName(dir);
						if (fn == ".git" || fn == "$tf" || fn == ".svn")
							continue;

						foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
						{
							curpath[0] = file;
							Importer.DoCensoring(workTree, file, censors, prepareForEdit);
						}
					}

					break;

				default:
					throw new ApplicationException("Unknown stage: " + verb);
			}

			if (_opts.Ask)
			{
				Console.WriteLine("\nPress any key...");
				Console.ReadKey();
			}

			Console.WriteLine();
		}

		public static int ProcessStart(string file, string args = null)
		{
			var psi = new ProcessStartInfo
			{
				FileName = file,
				Arguments = args,
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true
			};

			WriteToLogWithTimestamp(string.Format("START: {0} {1}", psi.FileName, psi.Arguments));

			using (var process = new Process())
			{
				process.StartInfo = psi;

				process.OutputDataReceived += (sender, e) =>
				{
					if (e.Data != null)
						WriteToLogWithTimestamp(e.Data);
				};

				process.ErrorDataReceived += (sender, e) =>
				{
					if (e.Data != null)
						LogError(e.Data);
				};

				process.Start();

				process.BeginOutputReadLine();
				process.BeginErrorReadLine();

				process.WaitForExit();

				LogAndConsole();

				return process.ExitCode;
			}
		}

		private static void ShowHelp(string unkVerb = null)
		{
			if (unkVerb != null)
				Console.WriteLine("Unknown verb: {0}\n", unkVerb);

			Console.WriteLine(@"Usage: VssSvnConvert stage [options]
where
	stage - conversion stage:
		ui - show simple UI with all available stages
		all - perform all stages. With 5 second timeout between.
		build-list - build list of files for import. After building, it can be edited by hand to remove *.exe for example
		build-list-stats - build statistic for list of import
		build-versions - build list of all versions of selected files
		build-links - build list of linked files
		build-cache - get all required versions to local cache
		build-commits - build list of commits:. Also, can be edited by hand for edit user names, for examle. DateTime in ticks, UTC.
		build-wc - Checkout specified URL.
		import - import commits to SVN working copy
		build-scripts - generate some useful scripts
		git-fast-import - generate datafile which can be imported with command git fast-import. Use it instead of import.

	each stage suppose, that previous stage results was already build and available.

Options for
	build-list-stats:
		--prefix=$/Project/xxxx - calculate statistic only for files with specified prefix
		--filter=Project/[^/]+$ - calculate statistic only for files with specified regex

Setup config VssSvnConvert.conf before run converter.

notes:
	!!! SVN repositiory should allow change revision properties. This need for set correct user and date per commit.

example:
	VssSvnConvert all
");
		}
	}
}
