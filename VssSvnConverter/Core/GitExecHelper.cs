using System;
using System.Collections.Generic;
using System.IO;

namespace VssSvnConverter.Core
{
	class GitExecHelper
	{
		public readonly string WorkTree;
		public readonly string GitDir;

		readonly ExecHelper _execHelper;
		readonly Dictionary<string, string> _envVars;

		public GitExecHelper(string gitExe, string workingCopy, TextWriter log)
		{
			_execHelper = new ExecHelper(gitExe, log, false);
			WorkTree = workingCopy;
			GitDir = Path.Combine(WorkTree, ".git");

			_envVars = new Dictionary<string, string>
			{
				{ "GIT_DIR", GitDir },
				{ "GIT_WORK_TREE", WorkTree }
			};
		}

		public void CheckRepositoryValid()
		{
			if (!Directory.Exists(WorkTree))
				throw new ApplicationException("Work tree does not exists: " + WorkTree);

			if (!Directory.Exists(GitDir))
				throw new ApplicationException("Git dir not found: " + GitDir);
		}

		public ExecHelper.ExecResult Exec(string args, bool noValidate = false)
		{
			var r = _execHelper.Exec(args, _envVars);
			if (!noValidate)
				ExecHelper.ValidateResult(r, args);
			return r;
		}

		public ExecHelper.ExecResult ExecCommit(string comment, string authorName, string authorEmail, DateTime time)
		{
			string commitMessageFile = Path.Combine(GitDir, "IMPORT_COMMIT_MESSAGE");
			File.WriteAllText(commitMessageFile, comment);

			string args = $"commit --all --allow-empty-message --file=\"{commitMessageFile}\"";

			var date = time.ToString("o");
			var envVars = new Dictionary<string, string>(_envVars)
			{
				["GIT_AUTHOR_NAME"] = authorName,
				["GIT_AUTHOR_EMAIL"] = authorEmail,
				["GIT_AUTHOR_DATE"] = date,
				["GIT_COMMITTER_NAME"] = authorName,
				["GIT_COMMITTER_EMAIL"] = authorEmail,
				["GIT_COMMITTER_DATE"] = date
			};

			var r = _execHelper.Exec(args, envVars);

			// nothing to commit - ok valid
			if (r.ExitCode == 1 &&
				(r.StdOut.Contains("nothing to commit") ||
				 r.StdErr.Contains("nothing to commit")))
				return r;

			ExecHelper.ValidateResult(r, args);

			return r;
		}
	}
}
