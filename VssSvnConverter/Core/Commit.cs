using System;
using System.Collections.Generic;

namespace VssSvnConverter.Core
{
	class Commit
	{
		readonly Dictionary<int, FileRevisionLite> _filesMap = new Dictionary<int, FileRevisionLite>();

		public DateTime At;
		public int AuthorId;
		public string Comment;
		public string Author
		{
			get => FileRevision.GetUser(AuthorId);
			set => AuthorId = FileRevision.GetUserId(value);
		}

		public IEnumerable<FileRevisionLite> Files => _filesMap.Values;
		public Dictionary<string, long> Labels = new Dictionary<string, long>();

		public void AddRevision(FileRevisionLite rev)
		{
			FileRevisionLite existing;
			if (!_filesMap.TryGetValue(rev.FileId, out existing) || existing.VssVersion < rev.VssVersion)
			{
				// add or update
				_filesMap[rev.FileId] = rev;
			}
		}

		public void AddLabel(string label, long ticks)
		{
			if (Labels.TryGetValue(label, out long oldTicks))
			{
				if (oldTicks == ticks)
					throw new Exception($"Commit at {At.Ticks}: duplicated label {ticks}, {label}");
				throw new Exception($"Commit at {At.Ticks}: duplicated label {oldTicks}, {ticks}, {label}");
			}
			else
			{
				Labels.Add(label, ticks);
			}
		}

		public static string MakeTag(string label)
		{
			if (label.StartsWith("(") && label.EndsWith(")"))
				return MakeTag(label.Substring(1, label.Length - 2).Trim());
			return label
				.Replace(' ', '_')
				.Replace('~', '-')
				.Replace('*', '+')
				.Replace('?', '!')
				.Replace('"', '\'')
				.Replace('[', '(')
				.Replace(']', ')');
		}
	}
}