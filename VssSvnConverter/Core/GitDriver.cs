using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VssSvnConverter.Core
{
	class GitDriver : IDestinationDriver
	{
		readonly TextWriter _log;
		readonly string _defaultEmailDomain;
		readonly GitExecHelper _gitHelper;

		public GitDriver(string gitExe, string workingCopy, string defaultEmailDomain, TextWriter log)
		{
			_defaultEmailDomain = defaultEmailDomain;
			_log = log;

			_gitHelper = new GitExecHelper(gitExe, workingCopy, log);
			_gitHelper.CheckRepositoryValid();

			CheckWorkingCopyStatus();
		}

		public string WorkingCopy
		{
			get { return _gitHelper.WorkTree; }
		}

		public void CleanupWorkingTree()
		{
			throw new NotImplementedException();
		}

		public void StartRevision()
		{
			CheckWorkingCopyStatus();
		}

		public void AddDirectory(string dir)
		{
			_log.WriteLine("Add dir '{0}'", dir);
			Directory.CreateDirectory(dir);
		}

		public void AddFiles(params string[] files)
		{
			foreach (var chunk in files.Partition(25))
			{
				_gitHelper.Exec("add -f -- " + string.Join(" ", chunk.Select(file => '"' + file + '"')));
			}
		}

		public string GetDiff(string file)
		{
			var r = _gitHelper.Exec(string.Format("diff --unified=0 -- \"{0}\"", file));

			return r.StdOut;
		}

		public void Revert(string file)
		{
			_gitHelper.Exec(string.Format("reset HEAD -- \"{0}\"", file));
			_gitHelper.Exec(string.Format("checkout -- \"{0}\"", file));
		}

		public void CommitRevision(Commit commit)
		{
			List<string> commentParts = commit.Labels.Select(l => l.Key)
				.Select(l => l.StartsWith("(") && l.EndsWith(")") ? l : "{" + l + "}").ToList();
			if (!string.IsNullOrEmpty(commit.Comment))
				commentParts.Add(commit.Comment);
			int count = commit.Files.Count();
			string s = count == 1 ? "" : "s";
			commentParts.Add($"({count} file{s})");
			string author = commit.Author;
			int pos = author.IndexOf('<');
			commentParts.Add(pos < 0 ? author : author.Substring(0, pos).Trim());
			string comment = string.Join(" ", commentParts);
			//Program.LogAndConsole(comment);

			string authorName, authorEmail;
			int pos2 = pos >= 0 ? author.IndexOf('>', pos + 1) : -1;
			if (pos2 > 0)
			{
				authorName = author.Substring(0, pos).Trim();
				authorEmail = author.Substring(pos + 1, pos2 - pos - 1).Trim();
			}
			else
			{
				int at = author.IndexOf('@');
				authorName = at != -1 ? author.Substring(0, at) : author; // Strip @domain from name
				authorEmail = at == -1 ? author + _defaultEmailDomain : author; // Add @domain to mail
			}

			var r = _gitHelper.ExecCommit(comment, authorName, authorEmail, commit.At);

			if (commit.Labels.Count == 0)
				return;

			var createdTags = new List<string>();
			foreach (string label in commit.Labels.Keys)
			{
				string tag = Commit.MakeTag(label);
				try
				{
					_gitHelper.Exec($"tag \"{tag}\"");
					createdTags.Add(tag);
				}
				catch (Exception e)
				{
					Program.LogAndConsole($"Importing commit {commit.At:yyyy-MM-dd HH:mm:ss} by {commit.Author}");
					Program.LogAndConsole($"Error adding tag '{tag}' (for label '{label}'):\n" + e.Message);
					foreach (string createdTag in createdTags)
						_gitHelper.Exec($"tag -d \"{createdTag}\"", noValidate: true);
					if (r.ExitCode == 0)
						_gitHelper.Exec("reset --hard HEAD^1", noValidate: true);
					throw;
				}
			}
		}

		public static void Create(string gitExe, string repoDir)
		{
			if (Directory.Exists(repoDir))
			{
				foreach (var file in Directory.GetFiles(repoDir, "*.*", SearchOption.AllDirectories))
				{
					if (Program.Exit)
						throw new Stop();

					File.SetAttributes(file, FileAttributes.Normal);
					File.Delete(file);
				}

				foreach (var dir in Directory.GetDirectories(repoDir))
				{
					if (Program.Exit)
						throw new Stop();

					Directory.Delete(dir, true);
				}
			}

			Directory.CreateDirectory(repoDir);

			new GitExecHelper(gitExe, repoDir, Console.Out).Exec("init");
		}

		void CheckWorkingCopyStatus()
		{
			var r = _gitHelper.Exec("status --porcelain");

			if (r.StdOut.Split("\r\n".ToCharArray(), StringSplitOptions.RemoveEmptyEntries).Length > 0)
				throw new ApplicationException("Working tree does should be clean. Status say:\n" + r.StdOut);

			r.ForStdError(s => { throw new ApplicationException("Status say in stderr:\n" + s); });
		}
	}
}
