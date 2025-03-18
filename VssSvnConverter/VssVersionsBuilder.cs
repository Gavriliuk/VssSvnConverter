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

	class VssVersionsBuilder
	{
		const string DataFileName = "2-raw-versions-list.txt";
		const string LogFileName = "log-2-raw-versions-list.txt";

		readonly Regex _versionRx = new Regex(@"^Ver:(?<ver>[0-9]+)\tSpec:(?<spec>[^\t]+)\tPhys:(?<phys>[^\t]+)\tAuthor:(?<user>[^\t]+)\tAt:(?<at>[0-9]+)\tDT:(?<dt>[^\t]+)\tComment:(?<comment>.*)$");

		public List<FileRevision> Load(string file = DataFileName)
		{
			bool writeToConsole = file == DataFileName || file == CacheBuilder.DataFileName;

			if (writeToConsole)
				Console.WriteLine("Loading versions from {0}", file);

			var list = new List<FileRevision>();
			using(var r = File.OpenText(file))
			{
				string line;
				while((line = r.ReadLine()) != null)
				{
					var m = _versionRx.Match(line);
					if(!m.Success)
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
						Console.WriteLine("Loaded {0} versions for {1} files, {2} users", list.Count, FileRevision.FileCount, FileRevision.UserCount);
				}
			}

			if (writeToConsole && list.Count % 10000 != 0)
				Console.WriteLine("Loaded {0} versions for {1} files, {2} users", list.Count, FileRevision.FileCount, FileRevision.UserCount);

			return list;
		}

		public void Build(Options opts, IList<Tuple<string, int>> files, Action<float> progress = null)
		{
			var stopWatch = new Stopwatch();
			stopWatch.Start();

			Console.WriteLine("Building version list to {0}", DataFileName);

			int findex = 0, vindex = 0, lastProgressPrc = 0;

			using (var cache = new VssFileCache(opts.CacheDir + "-revs", opts.SourceSafeIni))
			using(var wr = File.CreateText(DataFileName))
			using(var log = File.CreateText(LogFileName))
			{
				log.AutoFlush = true;

				var db = opts.DB.Value;

				foreach (string spec in files.Select(t => t.Item1))
				{
					if (Program.Exit)
						throw new Stop();

					if (findex > 0 && findex % 100 == 0)
						Console.WriteLine("Built {0} versions for {1} files ({2}%). Time: {3}", vindex, findex, lastProgressPrc, stopWatch.Elapsed);

					int progressPrc = 100 * findex / files.Count;
					if (progressPrc > lastProgressPrc)
					{
						if (progress != null)
							progress((float)findex / files.Count);
						lastProgressPrc = progressPrc;
					}
					findex++;

					try{
						IVSSItem item = db.VSSItem[spec];
						int head = item.VersionNumber;

						string cachedData = cache.GetFilePath(spec, head);
						if (cachedData != null)
						{
							List<FileRevision> cachedItemRevisions = Load(cachedData);
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
							if (action.StartsWith("Labeled ") ||
								action.StartsWith("Branched "))
								continue;

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
								Console.WriteLine("ERROR: Get Physical: " + ex.Message);
								log.WriteLine("ERROR: Get Physical: {0}", spec);
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
					catch(Exception ex)
					{
						Console.WriteLine("ERROR: {0}", spec);
						log.WriteLine("ERROR: {0}", spec);
						log.WriteLine(ex.ToString());
					}
				}
			}

			stopWatch.Stop();
			Console.WriteLine("Building version list complete. Built {0} versions for {1} files. Time: {2}", vindex, findex, stopWatch.Elapsed);
		}

		bool IsLatestOnly(Options opts, string spec)
		{
			return opts.LatestOnly.Contains(spec) || opts.LatestOnlyRx.Any(rx => rx.IsMatch(spec));
		}

		static void Save(TextWriter wr, IEnumerable<FileRevision> r)
		{
			foreach (var rev in r)
			{
				wr.WriteLine("Ver:{0}	Spec:{1}	Phys:{2}	Author:{3}	At:{4}	DT:{5}	Comment:{6}",
					rev.VssVersion, rev.FileSpec, rev.Physical, rev.User, rev.At.Ticks, rev.At,
					rev.Comment.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\n', '\u0001'));
			}
		}
	}
}
