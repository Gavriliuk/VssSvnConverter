using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;
using SourceSafeTypeLib;
using vsslib;
using System.Text.RegularExpressions;
using VssSvnConverter.Core;

namespace VssSvnConverter
{
	class CacheBuilder
	{
		public const string DataFileName = "4-cached-versions-list.txt";
		const string LogFileName = "log-4-cached-versions-list.txt";
		const string ErrorsFileName = "log-4-errors-list.txt";
		const string OnlyLastVersionFileName = "log-4-only-last-versions-list.txt";
		const string VersionsCountFileName = "log-4-versions-count-list.txt";
		const string UndoneVersionsCountFileName = "log-4-undone-versions-count-list.txt";

		IVSSDatabase _db;
		VssFileCache _cache;
		StreamWriter _log;
		string _tempDir;

		readonly Options _options;

		public CacheBuilder(Options opts)
		{
			_options = opts;
		}

		public List<FileRevision> Load()
		{
			return new VssVersionsBuilder().Load(DataFileName);
		}

		public void RemoveCachedErrors()
		{
			using (_cache = new VssFileCache(_options.CacheDir, _options.SourceSafeIni))
			{
				_cache.DropAllErrors();
			}
		}

		public void BuildStats(List<FileRevision> list)
		{
			// dump info about versions count per file
			using (var versionsCountLog = File.CreateText(VersionsCountFileName))
			{
				var listG = list
					.GroupBy(f => f.FileId)
					.Select(g => new { Count = g.Count(), Id = g.Key })
					.OrderByDescending(x => x.Count)
					.ToList()
				;

				listG.ForEach(g =>
				{
					if (Program.Exit)
						throw new Stop();

					versionsCountLog.WriteLine("{0,6} {1}", g.Count, FileRevision.GetFile(g.Id));
				});
			}

			// reduce pinned files to single revision
			list = list
				.GroupBy(f => f.FileId)
				.Select(g => {
					if (UseLatestOnly(FileRevision.GetFile(g.Key)))
						return g.Take(1).ToArray();
					return g.ToArray();
				})
				.SelectMany(x => x)
				.ToList()
			;

			using (var cache = new VssFileCache(_options.CacheDir, _options.SourceSafeIni))
			using (var errLog = File.CreateText(ErrorsFileName))
			using (var onlyLastVersionsLog = File.CreateText(OnlyLastVersionFileName))
			{
				errLog.AutoFlush = true;
				onlyLastVersionsLog.AutoFlush = true;

				// undone list
				using (var log = File.CreateText(UndoneVersionsCountFileName))
				{
					var listG = list
						.Where(v => cache.GetFileInfo(v.FileSpec, v.VssVersion) == null)
						.GroupBy(f => f.FileId)
						.Select(g => new { Count = g.Count(), Id = g.Key })
						.OrderByDescending(x => x.Count)
						.ToList()
					;

					listG.ForEach(g =>
					{
						if (Program.Exit)
							throw new Stop();

						log.WriteLine("{0,6} {1}", g.Count, FileRevision.GetFile(g.Id));
					});
				}

				var cached = 0;
				var errors = 0;
				var onlyLastVersions = 0;
				var notCached = 0;

				var onlyLastVersionSpecs = new HashSet<string>();

				foreach (var file in list)
				{
					if (Program.Exit)
						throw new Stop();

					if (cache.GetFilePath(file.FileSpec, file.VssVersion) != null)
					{
						cached++;
					}
					else
					{
						var err = cache.GetFileError(file.FileSpec, file.VssVersion);
						if (!string.IsNullOrWhiteSpace(err))
						{
							if (err == "not-retained")
							{
								if (!onlyLastVersionSpecs.Contains(file.FileSpec))
								{
									onlyLastVersions++;
									onlyLastVersionsLog.WriteLine("{0}", file.FileSpec);
									onlyLastVersionSpecs.Add(file.FileSpec);
								}
							}
							else
							{
								errors++;
								errLog.WriteLine("{0}@{1}\n\t{2}", file.FileSpec, file.VssVersion, err);
							}
							cached++;
						}
						else
						{
							notCached++;
						}
					}
				}

				Console.WriteLine("Cached: {0} (Errors: {1})  Not Cached: {2}", cached, errors, notCached);
				Console.WriteLine("Only Last Version: {0}", onlyLastVersions);
				Console.WriteLine("Not Cached: {0:0.00}%", 100.0 * notCached / list.Count);
				Console.WriteLine();
			}
		}

		public void Build(List<FileRevision> versions, Action<float> progress = null)
		{
			var sw = Stopwatch.StartNew();

			_tempDir = Path.Combine(Environment.CurrentDirectory, "vss-temp");
			Directory.CreateDirectory(_tempDir);

			_db = _options.DB.Value;
			var originalVersions = versions.ToList();

			using(_cache = new VssFileCache(_options.CacheDir, _db.SrcSafeIni))
			{
				// filterout cached versions
				if (!_options.Force)
				{
					Console.WriteLine("Skip already cached versions...");

					var cached = 0;
					var notRetained = 0;
					var errors = 0;

					versions.Clear();
					foreach (FileRevision file in originalVersions)
					{
						if (Program.Exit)
							throw new Stop();

						if (_cache.GetFilePath(file.FileSpec, file.VssVersion) != null)
						{
							cached++;
							continue;
						}

						if (!string.IsNullOrWhiteSpace(_cache.GetFileError(file.FileSpec, file.VssVersion)))
						{
							var err = _cache.GetFileError(file.FileSpec, file.VssVersion);

							if (err == "not-retained")
								notRetained++;
							else
								errors++;

							continue;
						}

						versions.Add(file);
					}

					Console.WriteLine("Cached(good): {0}", cached);
					Console.WriteLine("Cached(errors): {0}", errors);
					Console.WriteLine("Cached(not retained version): {0}", notRetained);
					Console.WriteLine("Not Cached: {0}", versions.Count);
				}
				Console.WriteLine();

				// sort versions
				versions = versions
					.GroupBy(f => f.FileId)
					// order by versions count. posible you do not want to convert all versions for some autogenerated files
					.OrderBy(g => g.Count())
					// start retrieveing from recent (high versions) to ancient (version 1)
					.SelectMany(g => g.OrderByDescending(v => v.VssVersion))
					.ToList()
				;

				Console.WriteLine("Building version cache to {0}", _options.CacheDir);

				int findex = 0, vindex = 0, lastProgressPrc = 0;
				bool useLatestOnly = _options.LatestOnly.Any() || _options.LatestOnlyRx.Any();

				using (_log = File.CreateText(LogFileName))
				{
					_log.AutoFlush = true;

					// cache
					List<IGrouping<int, FileRevision>> fileGroups = versions.GroupBy(v => v.FileId).ToList();
					foreach (IGrouping<int, FileRevision> fileGroup in fileGroups)
					{
						if (Program.Exit)
							throw new Stop();

						int progressPrc = 100 * vindex / originalVersions.Count;
						if (progressPrc > lastProgressPrc)
						{
							if (progress != null)
								progress((float)vindex / originalVersions.Count);
							lastProgressPrc = progressPrc;
						}
						findex++;

						string fileSpec = FileRevision.GetFile(fileGroup.Key);
						bool useOnce = useLatestOnly && UseLatestOnly(fileSpec);

						foreach (FileRevision fr in fileGroup)
						{
							if (Program.Exit)
								throw new Stop();

							GetFromVss(fr);
							vindex++;

							if (vindex % 1000 == 0)
								Console.WriteLine("Built {0} versions for {1} files ({2}%). Time: {3}", vindex, findex, lastProgressPrc, sw.Elapsed);

							if (useOnce)
								break;
						}
					}
				}

				if (vindex % 1000 != 0)
					Console.WriteLine("Total built {0} versions for {1} files. Time: {2}", vindex, findex, sw.Elapsed);

				// build cached versions list
				BuildCachedVersionsList(originalVersions);
			}

			sw.Stop();

			Console.WriteLine();
			Console.WriteLine("Building cache complete. Take {0}", sw.Elapsed);
		}

		void BuildCachedVersionsList(List<FileRevision> versions)
		{
			var sw = Stopwatch.StartNew();

			Console.WriteLine("Building cached versions to {0}", DataFileName);

			int findex = 0, vindex = 0, lastProgressPrc = 0;

			bool useLatestOnly = _options.LatestOnly.Any() || _options.LatestOnlyRx.Any();

			using (StreamWriter wr = File.CreateText(DataFileName))
			{
				List<IGrouping<int, FileRevision>> fileGroups = versions.GroupBy(v => v.FileId).ToList();
				foreach (IGrouping<int, FileRevision> fileGroup in fileGroups)
				{
					if (Program.Exit)
						throw new Stop();

					int progressPrc = 100 * findex / fileGroups.Count;
					if (progressPrc > lastProgressPrc)
						lastProgressPrc = progressPrc;
					findex++;

					// get in ascending order
					List<FileRevision> fileVersions = fileGroup.OrderBy(f => f.VssVersion).ToList();

					List<int> brokenVersions = new List<int>();
					List<int> notRetainedVersions = new List<int>();
					Dictionary<int, string> otherErrors = new Dictionary<int, string>();

					string fileSpec = FileRevision.GetFile(fileGroup.Key);
					if (useLatestOnly && UseLatestOnly(fileSpec))
					{
						// reduce file versions to latest only
						fileVersions = fileVersions.Skip(fileVersions.Count - 1).ToList();
					}

					foreach (FileRevision file in fileVersions)
					{
						if (Program.Exit)
							throw new Stop();

						vcslib.FileCache.CacheEntry inf = _cache.GetFileInfo(fileSpec, file.VssVersion);

						if (inf == null)
							throw new ApplicationException(string.Format("Not in cache, but should be: {0}@{1}", file.FileSpec, file.VssVersion));

						if (inf.ContentPath != null)
						{
							if (brokenVersions.Count > 0 || notRetainedVersions.Count > 0 || otherErrors.Count > 0)
							{
								var commentPlus = string.Format("\nBalme for '{0}' can be incorrect, because:", file.FileSpec);
								if (brokenVersions.Count > 0)
								{
									commentPlus += "\n\tbroken vss versions: " +
													string.Join(", ", brokenVersions.Select(v => v.ToString(CultureInfo.InvariantCulture)).ToArray());
								}
								if (notRetainedVersions.Count > 0)
								{
									commentPlus += "\n\tnot retained versions: " +
													string.Join(", ", notRetainedVersions.Select(v => v.ToString(CultureInfo.InvariantCulture)).ToArray());
								}
								if (otherErrors.Count > 0)
								{
									foreach (var otherErrorsGroup in otherErrors.GroupBy(kvp => kvp.Value))
									{
										var revs = otherErrorsGroup.Select(kvp => kvp.Key.ToString(CultureInfo.InvariantCulture)).ToArray();
										commentPlus += string.Format("\n\tError in VSS versions {0}: '{1}'", string.Join(", ", revs),
											otherErrorsGroup.Key);
									}
								}

								file.Comment += commentPlus;
							}

							wr.WriteLine("Ver:{0}	Spec:{1}	Phys:{2}	Author:{3}	At:{4}	DT:{5}	Comment:{6}",
								file.VssVersion,
								file.FileSpec,
								file.Physical,
								file.User,
								file.At.Ticks,
								file.At,
								file.Comment.Replace('\n', '\u0001')
							);
							vindex++;

							notRetainedVersions.Clear();
							brokenVersions.Clear();
							otherErrors.Clear();
						}
						else if (inf.Notes == "not-retained")
							notRetainedVersions.Add(file.VssVersion);
						else if (inf.Notes == "broken-revision")
							brokenVersions.Add(file.VssVersion);
						else if (!string.IsNullOrWhiteSpace(inf.Notes))
							otherErrors[file.VssVersion] = inf.Notes;
					}

					if (brokenVersions.Count > 0 || notRetainedVersions.Count > 0 || otherErrors.Count > 0)
						throw new ApplicationException(string.Format("Absent content for latest file version: {0}", fileGroup.Key));
				}
			}

			Console.WriteLine("Built {0} cached versions for {1} files. Time: {2}", vindex, findex, sw.Elapsed);

			sw.Stop();
		}

		bool UseLatestOnly(string spec)
		{
			return _options.LatestOnly.Contains(spec) || _options.LatestOnlyRx.Any(rx => rx.IsMatch(spec));
		}

		void GetFromVss(FileRevision fr)
		{
			// clean destination
			foreach (var tempFile in Directory.GetFiles(_tempDir, "*", SearchOption.AllDirectories))
			{
				try
				{
					File.SetAttributes(tempFile, FileAttributes.Normal);
					File.Delete(tempFile);
				}
				catch (Exception ex)
				{
					Console.WriteLine("\nCan't remove temp file: " + tempFile + "\n" + ex.Message);
				}
			}

			try
			{
				var vssItem = _db.VSSItem[fr.FileSpec];

				// move to correct version
				if (vssItem.VersionNumber != fr.VssVersion)
					vssItem = vssItem.Version[fr.VssVersion];

				var dstFileName = Path.GetFileName(fr.FileSpec.TrimStart('$', '/', '\\'));

				var path = Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + "-" + dstFileName);

				try
				{
					vssItem.Get(path, (int)VSSFlags.VSSFLAG_FORCEDIRNO | (int)VSSFlags.VSSFLAG_USERRONO | (int)VSSFlags.VSSFLAG_REPREPLACE);
				}
				catch(Exception ex)
				{
					if (string.IsNullOrWhiteSpace(_options.SSPath))
						throw;

					// special case when physical file not correspond to
					var m = Regex.Match(ex.Message, "File ['\"](?<phys>[^'\"]+)['\"] not found");
					if (!m.Success)
						throw;

					if (m.Groups["phys"].Value == vssItem.Physical)
						throw;

					Console.WriteLine("\nPhysical file mismatch. Try get with ss.exe");

					path = new SSExeHelper(_options.SSPath, _log).Get(fr.FileSpec, fr.VssVersion, _tempDir);
					if (path == null)
					{
						Console.WriteLine("Get with ss.exe failed");
						throw;
					}
				}

				// in force mode check if file already in cache and coincidence by hash
				if(_options.Force)
				{
					var ce = _cache.GetFileInfo(fr.FileSpec, fr.VssVersion);
					if(ce != null)
					{
						string hash;

						using(var s = new FileStream(path, FileMode.Open, FileAccess.Read))
							hash = Convert.ToBase64String(_hashAlgo.ComputeHash(s));

						if(hash != ce.Sha1Hash)
						{
							_log.WriteLine("!!! Cache contains different content for: " + fr.FileSpec);
							_log.WriteLine("{0} != {1}", hash, ce.Sha1Hash);
							_cache.AddFile(fr.FileSpec, fr.VssVersion, path, false);
						}
						return;
					}
				}
				_cache.AddFile(fr.FileSpec, fr.VssVersion, path, false);
			}
			catch (Exception ex)
			{
				if (ex.Message.Contains("does not retain old versions of itself"))
				{
					Console.WriteLine("{0} hasn't retain version {1}.", fr.FileSpec, fr.VssVersion);

					_cache.AddError(fr.FileSpec, fr.VssVersion, "not-retained");

					return;
				}

				// last revision shall always present
				// known error
				if (ex.Message.Contains("SourceSafe was unable to finish writing a file.  Check your available disk space, and ask the administrator to analyze your SourceSafe database."))
				{
					Console.Error.WriteLine("\nAbsent file revision: {0}@{1}", fr.FileSpec, fr.VssVersion);

					_cache.AddError(fr.FileSpec, fr.VssVersion, "broken-revision");

					return;
				}

				_cache.AddError(fr.FileSpec, fr.VssVersion, ex.Message);

				UnrecognizedError(fr, ex);
			}
		}

		void UnrecognizedError(FileRevision file, Exception ex = null)
		{
			_log.WriteLine("UNRECOGNIZED ERROR: {0}", file.FileSpec);
			Console.Error.WriteLine("\n!!! Unrecognized error. See logs.\n{0}@{1}", file.FileSpec, file.VssVersion);
			if(ex != null)
			{
				_log.WriteLine(ex.ToString());
				Console.Error.WriteLine(" ERROR: {0}", ex.Message);
			}
		}

		readonly SHA1Managed _hashAlgo = new SHA1Managed();
	}
}
