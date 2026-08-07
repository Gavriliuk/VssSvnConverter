using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Diagnostics;
using SourceSafeTypeLib;
using vsslib;
using VssSvnConverter.Core;
using vcslib;

namespace VssSvnConverter
{
	class FileRevision
	{
		static readonly List<string> Files = new List<string>();
		static readonly List<string> Users = new List<string>();

		static readonly Dictionary<string, int> FileIds = new Dictionary<string, int>();
		static readonly Dictionary<string, int> UserIds = new Dictionary<string, int>();

		public int FileId;
		public int UserId;
		public int OriginalUserId;

		public string FileSpec
		{
			get => Files[FileId];
			set => FileId = GetFileId(value);
		}

		public string User
		{
			get => Users[UserId];
			set => UserId = GetUserId(value);
		}

		public string OriginalUser
		{
			get => Users[OriginalUserId];
			set => OriginalUserId = GetUserId(value);
		}

		public DateTime At;
		public int VssVersion;
		public string Comment;
		public string Physical;

		public static int FileCount => Files.Count;
		public static int UserCount => Users.Count;

		public static string GetFile(int fileId) => Files[fileId];
		public static string GetUser(int userId) => Users[userId];
		public static int GetFileId(string file) => GetId(file, Files, FileIds);
		public static int GetUserId(string user) => GetId(user, Users, UserIds);

		public static int GetId(string value, List<string> list, Dictionary<string, int> dict)
		{
			var pairs = dict.Where(x => x.Key == value);
			if (pairs.Count() > 0)
				return pairs.First().Value;

			var result = list.Count;
			list.Add(value);
			dict[value] = result;
			return result;
		}

		public static void Clear()
		{
			Files.Clear();
			Users.Clear();
			FileIds.Clear();
			UserIds.Clear();
		}
	}

	class CommitLabels
	{
		const string DataFileName = "2-labels.txt";
		Dictionary<string, long> _all = new Dictionary<string, long>();

		public int Count => _all.Count;
		public Dictionary<string, long>.KeyCollection Texts => _all.Keys;
		public Dictionary<string, long>.ValueCollection Times => _all.Values;


		public void Clear()
		{
			_all.Clear();
		}

		public void Sort()
		{
			_all = _all.OrderBy(l => l.Value).ToDictionary(l => l.Key, l => l.Value);
		}

		public void Add(string label, DateTime at)
		{
			if (_all.TryGetValue(label, out long ticks))
			{
				if (at.Ticks != ticks)
					throw new Exception($"Duplicated label {ticks}, {at.Ticks}, {label}");
			}
			else
			{
				_all.Add(label, at.Ticks);
			}
		}

		public void Save()
		{
			Program.LogAndConsole($"Writing file '{DataFileName}' (VSS labels)");
			File.WriteAllLines(DataFileName, _all.Select(l => $"{l.Value}\t{l.Key}"));
			Program.LogAndConsole($"{new FileInfo(DataFileName).Length} bytes written to file '{DataFileName}'\n", new FileInfo(DataFileName).Length, DataFileName);
		}

		public void Load()
		{
			_all.Clear();

			if (!File.Exists(DataFileName))
				return;

			int lineCount = 0;
			Program.LogAndConsole($"Reading file '{DataFileName}'");
			using (StreamReader r = File.OpenText(DataFileName))
			{
				string line;
				while ((line = r.ReadLine()) != null)
				{
					lineCount++;
					string[] arr = line.Split('\t');
					Debug.Assert(arr.Length == 2);
					_all.Add(arr[1], long.Parse(arr[0]));
				}
			}
			Program.LogAndConsole($"{lineCount} lines read from file '{DataFileName}'\n", lineCount, DataFileName);
		}

		public static CommitLabels LoadNew()
		{
			CommitLabels result = new CommitLabels();
			result.Load();
			return result;
		}
	}

	class VssVersionsBuilder
	{
		const string DataFileName = "2-raw-versions.txt";
		const string LogFileName = "2-raw-versions.log";

		readonly Regex _versionRx = new Regex(@"^Ver:(?<ver>[0-9]+)\tSpec:(?<spec>[^\t]+)\tPhys:(?<phys>[^\t]+)\tAuthor:(?<user>[^\t]+)\tAt:(?<at>[0-9]+)\tDT:(?<dt>[^\t]+)\tComment:(?<comment>.*)$");

		public List<FileRevision> Load(string file = DataFileName, bool writeToConsole = true)
		{
			if (writeToConsole)
				Program.LogAndConsole("Loading versions from file '{0}'", file);

			var list = new List<FileRevision>();
			int lineCount = 0;
			using (var r = File.OpenText(file))
			{
				string line;
				while ((line = r.ReadLine()) != null)
				{
					lineCount++;
					var m = _versionRx.Match(line);
					if (!m.Success)
						continue;

					var v = new FileRevision {
						At = new DateTime(long.Parse(m.Groups["at"].Value), DateTimeKind.Utc),
						User = m.Groups["user"].Value,
						FileSpec = m.Groups["spec"].Value,
						VssVersion = int.Parse(m.Groups["ver"].Value),
						Physical = m.Groups["phys"].Value,
						Comment = m.Groups["comment"].Value.Replace('\u0001', '\n')
					};

					list.Add(v);
					if (writeToConsole && list.Count % 10000 == 0)
						Program.LogAndConsole("Loaded {0,8} versions for {1,6} files, {2,2} users", list.Count, FileRevision.FileCount, FileRevision.UserCount);
				}
			}

			if (writeToConsole && list.Count % 10000 != 0)
				Program.LogAndConsole("Loaded {0,8} versions for {1,6} files, {2,2} users", list.Count, FileRevision.FileCount, FileRevision.UserCount);

			if (writeToConsole)
				Program.LogAndConsole("{0} lines read from file '{1}'\n", lineCount, file);

			return list;
		}

		public void Build(Options opts, IList<Tuple<string, int>> files, Action<float> progress = null)
		{
			var stopWatch = new Stopwatch();
			stopWatch.Start();

			CommitLabels labels = CommitLabels.LoadNew();

			int findex = 0, vindex = 0, lastProgressPrc = 0;
			var cacheDir = opts.CacheDir + "-revs";
			int fileCount = FileCache.GetEntryCount(cacheDir);

			Program.LogAndConsole($"Using {fileCount} files from cache dir '{cacheDir}'");
			Program.LogAndConsole($"Writing file '{DataFileName}' (raw version list)");
			Program.LogAndConsole($"Writing file '{LogFileName}' (raw versions log)");

			using (var cache = new VssFileCache(cacheDir, opts.SourceSafeIni))
			using (var wr = File.CreateText(DataFileName))
			using (var log = File.CreateText(LogFileName))
			{
				log.AutoFlush = true;

				var db = opts.DB.Value;

				foreach (string spec in files.Select(t => t.Item1))
				{
					if (Program.Exit)
						throw new Stop();

					if (findex > 0 && findex % 100 == 0)
						Program.LogAndConsole($"Built {vindex,8} versions for {findex,6} files ({lastProgressPrc,3}%). Time: {stopWatch.Elapsed}");

					int progressPrc = 100 * findex / files.Count;
					if (progressPrc > lastProgressPrc)
					{
						if (progress != null)
							progress((float)findex / files.Count);
						lastProgressPrc = progressPrc;
					}
					findex++;

					try
					{
						IVSSItem item = db.VSSItem[spec];
						int head = item.VersionNumber;

						string cachedData = cache.GetFilePath(spec, head);
						if (cachedData != null)
						{
							List<FileRevision> cachedItemRevisions = Load(cachedData, false);
							if (cachedItemRevisions.Count > 0)
							{
								Save(wr, cachedItemRevisions);
								vindex += cachedItemRevisions.Count;
							}
							// next file
							continue;
						}

						bool latestOnly = IsLatestOnly(opts, spec);

						List<FileRevision> itemRevisions = new List<FileRevision>();
						foreach (IVSSVersion ver in item.Versions)
						{
							if (Program.Exit)
								throw new Stop();

							string action = ver.Action;
							if (action.StartsWith("Branched "))
								continue;

							DateTime at = ver.Date.ToUniversalTime();
							if (action.StartsWith("Labeled "))
							{
								labels.Add(action.Substring(9, action.Length - 10), at);
								continue;
							}

							vindex++;

							if (!action.StartsWith("Checked in ") &&
								!action.StartsWith("Created ") &&
								!action.StartsWith("Archived ") &&
								!action.StartsWith("Rollback to"))
							{
								log.WriteLine("Unknown action: " + ver.Action);
							}

							var user = ver.Username.ToLowerInvariant().Replace('.', ' ');

							var fileVersionInfo = new FileRevision {
								FileSpec = item.Spec,
								At = ver.Date.ToUniversalTime(),
								Comment = ver.Comment,
								VssVersion = ver.VersionNumber,
								User = user
							};
							try
							{
								// can throw exception, but it is not critical
								fileVersionInfo.Physical = ver.VSSItem.Physical;
							}
							catch (Exception ex)
							{
								Program.LogError("Get Physical: " + ex.Message);
								log.WriteLine($"ERROR: Get Physical: {spec}");
								log.WriteLine(ex.ToString());
								fileVersionInfo.Physical = "_UNKNOWN_";
							}
							itemRevisions.Add(fileVersionInfo);

							if (latestOnly)
								break;
						}

						if (itemRevisions.Count > 0)
						{
							// some time date of items wrong, but versions - correct.
							// sort items in correct order and fix dates
							itemRevisions = itemRevisions.OrderBy(i => i.VssVersion).ToList();

							// fix time. make time of each next item greater than all previous
							var notEarlierThan = itemRevisions[0].At;
							for (int i = 1; i < itemRevisions.Count; i++)
							{
								if (itemRevisions[i].At < notEarlierThan)
								{
									itemRevisions[i].At = notEarlierThan + TimeSpan.FromMilliseconds(1);
									itemRevisions[i].Comment += "\n! Time was fixed during VSS -> SVN conversion. Time can be incorrect !\n";
									itemRevisions[i].Comment = itemRevisions[i].Comment.Trim();
								}

								notEarlierThan = itemRevisions[i].At;
							}

							Save(wr, itemRevisions);
							vindex += itemRevisions.Count;
						}

						var tempFile = Path.GetTempFileName();
						try
						{
							using (var sw = new StreamWriter(tempFile, false, Encoding.UTF8))
								Save(sw, itemRevisions);

							cache.AddFile(spec, head, tempFile, false);
						}
						finally
						{
							if (File.Exists(tempFile))
								File.Delete(tempFile);
						}
					}
					catch (Exception ex)
					{
						log.WriteLine($"ERROR: {spec}\n{ex}");
						Program.LogError($"{spec}\n{ex.Message}");
					}
				}
			}

			labels.Sort();
			labels.Save();

			stopWatch.Stop();
			fileCount = FileCache.GetEntryCount(cacheDir);

			Program.LogAndConsole("Building version list complete");
			Program.LogAndConsole($"Built {vindex,8} versions for {findex,6} files (100%). Time: {stopWatch.Elapsed}");
			Program.LogAndConsole($"{fileCount} files stored in cache dir '{cacheDir}'");
			Program.LogAndConsole($"{new FileInfo(DataFileName).Length} bytes written to file '{DataFileName}'");
			Program.LogAndConsole($"{new FileInfo(LogFileName).Length} bytes written to file '{LogFileName}'\n");
		}

		bool IsLatestOnly(Options opts, string spec)
		{
			return opts.LatestOnly.Contains(spec) || opts.LatestOnlyRx.Any(rx => rx.IsMatch(spec));
		}

		static void Save(TextWriter wr, IEnumerable<FileRevision> r)
		{
			foreach (var rev in r)
			{
				wr.WriteLine($"Ver:{rev.VssVersion}\tSpec:{rev.FileSpec}\tPhys:{rev.Physical}\tAuthor:{rev.User}\tAt:{rev.At.Ticks}\tDT:{rev.At}\tComment:{rev.Comment.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\n', '\u0001')}");
			}
		}
	}
}
