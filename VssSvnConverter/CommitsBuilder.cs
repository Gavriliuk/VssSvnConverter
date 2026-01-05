using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using VssSvnConverter.Core;
using System.Diagnostics;

namespace VssSvnConverter
{
	class CommitsBuilder
	{
		const string DataFileName = "5-commits-list.txt";
		const string CommitLogFileName = "5-commits-log.txt";

		Options _opts;

		readonly HashSet<string> _notMappedAuthors = new HashSet<string>();

		public static List<Commit> Load()
		{
			Commit commit = null;
			var commits = new List<Commit>();
			var commitRx = new Regex(@"^Commit:(?<at>[0-9]+)\t\tAuthor:(?<user>.+)\t\tComment:(?<comment>.*)$");
			var labelRx = new Regex(@"^Label: (?<at>[0-9]+)\t\t(?<label>.*)$");
			Console.WriteLine("Loading commits from {0}", DataFileName);
			int fcount = 0, lcount = 0;
			using (var r = File.OpenText(DataFileName))
			{
				string line;
				while ((line = r.ReadLine()) != null)
				{
					if (Program.Exit)
						throw new Stop();

					if (line.StartsWith("Commit:"))
					{
						Match m = commitRx.Match(line);
						if (!m.Success)
							throw new Exception("Can not parse line: " + line);

						if (commits.Count > 0 && commits.Count % 1000 == 0)
							Console.WriteLine($"Loaded {commits.Count} commits, {fcount} files, {lcount} labels");

						commit = new Commit {
							At = new DateTime(long.Parse(m.Groups["at"].Value), DateTimeKind.Utc),
							Author = m.Groups["user"].Value,
							Comment = DeserializeMultilineText(m.Groups["comment"].Value)
						};

						commits.Add(commit);
					}
					else if (line.StartsWith("Label: "))
					{
						Debug.Assert(commit != null);

						Match m = labelRx.Match(line);
						if (!m.Success)
							throw new Exception("Can not parse line: " + line);

						commit.AddLabel(m.Groups["label"].Value, long.Parse(m.Groups["at"].Value));
						lcount++;
					}
					else if (line.StartsWith("\t"))
					{
						Debug.Assert(commit != null);

						var arr = line.Substring(1).Split(':');
						Debug.Assert(arr.Length == 3);

						commit.AddRevision(new FileRevisionLite {
							FileSpec = arr[2],
							VssVersion = int.Parse(arr[0]),
							At = new DateTime(long.Parse(arr[1]), DateTimeKind.Utc)
						});
						fcount++;
					}
					else
					{
						throw new Exception("Can not parse line: " + line);
					}
				}

				if (commits.Count % 1000 != 0)
					Console.WriteLine($"Loaded {commits.Count} commits, {fcount} files, {lcount} labels");
			}
			return commits;
		}

		public void Build(Options opts, List<FileRevision> versions)
		{
			_opts = opts;

			_notMappedAuthors.Clear();

			if (File.Exists(DataFileName))
				File.Delete(DataFileName);

			// perform mapping vss user -> author
			MapAuthors(versions);

			List<Commit> commits = SliceToCommits(versions);

			// save
			using (var wr = File.CreateText(DataFileName))
			{
				foreach (var c in commits)
				{
					if (Program.Exit)
						throw new Stop();

					wr.WriteLine("Commit:{0}\t\tAuthor:{1}\t\tComment:{2}", c.At.Ticks, c.Author, SerializeMultilineText(c.Comment));
					c.Labels.ToList().ForEach(label => wr.WriteLine($"Label: {label.Value}\t\t{label.Key}"));
					c.Files.ToList().ForEach(f => {
						Debug.Assert(f.At.Kind == DateTimeKind.Utc);
						wr.WriteLine("\t{0}:{1}:{2}", f.VssVersion, f.At.Ticks, f.FileSpec);
					});
				}
			}

			// save commits info (log like)
			using (var wr = File.CreateText(CommitLogFileName))
			{
				foreach (var c in commits)
				{
					if (Program.Exit)
						throw new Stop();

					wr.WriteLine($"{c.At.Ticks} {c.At:yyyy-MM-dd HH:mm:ss} {c.Author}");
					var comment = string.Join("\n", c.Comment.Split('\n').Select(x => "\t" + x)).Trim();
					if (!string.IsNullOrWhiteSpace(comment))
						wr.WriteLine("\t" + comment);
				}
			}

			Console.WriteLine("{0} commits produced.", commits.Count);

			if (_notMappedAuthors.Count > 0)
			{
				Console.WriteLine("Not mapped users:");
				_notMappedAuthors.ToList().ForEach(u => Console.WriteLine($"{u} = ?"));

				if (_opts.UserMappingStrict)
				{
					throw new ApplicationException("Stop execution.");
				}
			}
			Console.WriteLine("Build commits list complete. Check " + DataFileName);
		}

		void MapAuthors(IEnumerable<FileRevision> revs)
		{
			var mapping = LoadUserMappings("authors") ?? new Dictionary<string, string>();

			foreach (var rev in revs)
			{
				if (Program.Exit)
					throw new Stop();

				var user = rev.User.ToLowerInvariant();
				if (mapping.TryGetValue(user, out string author))
				{
					rev.OriginalUserId = rev.UserId;
					rev.User = author;
				}
				else
				{
					_notMappedAuthors.Add(user);
				}
			}
		}

		Dictionary<string, string> LoadUserMappings(string configKey)
		{
			Dictionary<string, string> mapping = null;

			foreach (var mappingFile in _opts.Config[configKey])
			{
				mapping = mapping ?? new Dictionary<string, string>();
				foreach (var line in File.ReadAllLines(mappingFile).Where(l => !string.IsNullOrWhiteSpace(l)))
				{
					if (Program.Exit)
						throw new Stop();

					var ind = line.IndexOf('=');

					if (ind == -1)
						throw new Exception("Invalid user mapping file: " + mappingFile);

					var from = line.Substring(0, ind).Trim().ToLowerInvariant();
					var to = line.Substring(ind + 1).Trim();

					if (mapping.ContainsKey(from))
						throw new Exception("Invalid user mapping file: " + mappingFile + "; Duplicate entry: " + from);

					mapping[from] = to;
				}
			}
			return mapping;
		}

		List<Commit> SliceToCommits(List<FileRevision> revs)
		{
			List<Commit> result = new List<Commit>();

			if (revs.Count == 0)
				return result;

			revs = revs
				.OrderBy(r => r.At)
				.ThenBy(r => r.VssVersion)
				.ToList()
			;

			CommitLabels labels = CommitLabels.LoadNew();
			labels.Sort();

			string[] labelTexts = labels.Texts.ToArray();
			long[] labelTimes = labels.Times.ToArray();
			int labelCount = labels.Count;

			Commit commit = null;
			int labelId = 0;

			Console.WriteLine($"Building commits from {revs.Count} revisions");
			foreach (FileRevision rev in revs)
			{
				if (Program.Exit)
					throw new Stop();

				// Creating the first commit?
				if (commit == null)
				{
					commit = new Commit { At = rev.At, AuthorId = rev.UserId, Comment = rev.Comment };
					commit.AddRevision(new FileRevisionLite { At = rev.At, FileId = rev.FileId, VssVersion = rev.VssVersion });
					continue;
				}

				// Current revision should come to the same commit?
				if ((string.IsNullOrEmpty(commit.Comment) || rev.Comment == commit.Comment) &&
					rev.UserId == commit.AuthorId &&
					rev.At - commit.At <= _opts.SilencioSpan &&
					(labelId >= labelCount || labelTimes[labelId] >= rev.At.Ticks))
				{
					commit.At = rev.At;
					commit.AddRevision(new FileRevisionLite { At = rev.At, FileId = rev.FileId, VssVersion = rev.VssVersion });
					continue;
				}

				// Current revision should come to the next commit
				while (labelId < labelCount && labelTimes[labelId] < rev.At.Ticks)
				{
					commit.AddLabel(labelTexts[labelId], labelTimes[labelId]);
					labelId++;
				}

				result.Add(commit);

				commit = new Commit { At = rev.At, AuthorId = rev.UserId, Comment = rev.Comment };
				commit.AddRevision(new FileRevisionLite { At = rev.At, FileId = rev.FileId, VssVersion = rev.VssVersion });
			}

			if (commit != null)
			{
				while (labelId < labelCount)
				{
					commit.AddLabel(labelTexts[labelId], labelTimes[labelId]);
					labelId++;
				}

				result.Add(commit);
			}

			return result;
		}

		static string SerializeMultilineText(string text)
		{
			if (string.IsNullOrWhiteSpace(text))
				return string.Empty;

			return text.Replace('\n', '\x01').Replace("\r", "");
		}

		static string DeserializeMultilineText(string line)
		{
			return line.Replace('\x01', '\n');
		}
	}
}
