using SourceSafeTypeLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using vsslib;
using VssSvnConverter.Core;

namespace VssSvnConverter
{
	class ImportListBuilder
	{
		const string DataFileRootTypes = "0-roots.txt";
		const string AllFilesList = "1-all-files.txt";
		const string DataExtsFileName = "1-files-by-ext.txt";
		const string DataSizesFileName = "1-files-by-size.txt";
		const string ExcludedFilesList = "1-files-to-ignore.txt";
		const string FilesList = "1-files-to-import.txt";

		List<Tuple<string, int>> _files;

		public void Build(Options opts)
		{
			_files = new List<Tuple<string, int>>();

			Program.LogAndConsole($"Writing file '{DataFileRootTypes}' (all VSS roots from config)");
			using (var rootTypes = File.CreateText(DataFileRootTypes))
			{
				rootTypes.AutoFlush = true;

				foreach (var root in opts.Config["import-root"])
				{
					Program.LogAndConsole($"VSS Root: {root}");

					var rootItem = opts.DB.Value.VSSItem[root].Normalize(opts.DB.Value);

					rootTypes.WriteLine($"{rootItem.Spec}\t{ (rootItem.Type == 0 ? "d" : "f") }");

					WalkItem(rootItem);
					if (_files.Count % 1000 != 0)
						Program.LogAndConsole($"Found {_files.Count,6} files");
					if (Program.Exit)
						throw new Stop();
				}
			}
			Program.LogAndConsole($"{new FileInfo(DataFileRootTypes).Length} bytes written to file '{DataFileRootTypes}'\n");

			Program.LogAndConsole($"Writing file '{AllFilesList}' (all files from VSS)");
			File.WriteAllLines(AllFilesList, _files.Select(t => string.Format("{0}\t{1}", t.Item1, t.Item2)).ToArray());
			Program.LogAndConsole($"{new FileInfo(AllFilesList).Length} bytes written to file '{AllFilesList}'\n");

			FilterFiles(opts);
		}

		static List<Tuple<string, int>> LoadFrom(string path)
		{
			Program.LogAndConsole($"Reading file '{path}'");
			var lines = File.ReadAllLines(path);
			var result = lines.Select(l => 
			{
				var ll = l.Split('\t');
				if (ll.Length != 2 || string.IsNullOrEmpty(ll[0]) || !Int32.TryParse(ll[1], out var size) || size < 0)
					throw new Exception($"Invalid line: '{l}'");
				return Tuple.Create(ll[0], size);
			}
			).ToList();
			Program.LogAndConsole($"{lines.Length} lines read from file '{path}'\n", result.Count, path);
			return result;
		}

		public List<Tuple<string, int>> Load()
		{
			return LoadFrom(FilesList);
		}

		// spec -> isdir
		public Dictionary<string, bool> LoadRootTypes()
		{
			return File
				.ReadAllLines(DataFileRootTypes)
				.Where(l => !string.IsNullOrWhiteSpace(l))
				.Select(l => l.Trim().Split('\t'))
				.ToDictionary(ar => ar[0], ar => ar[1] == "d")
			;
		}

		public void FilterFiles(Options opts)
		{
			var files = LoadFrom(AllFilesList);

			var isInclude = opts.IncludePredicate;

			var excluded = files.Where(t => !isInclude(t.Item1)).ToList();
			files = files.Where(t => isInclude(t.Item1)).ToList();

			// write included & excluded

			Program.LogAndConsole($"Writing file '{FilesList}' (files included to import)");
			File.WriteAllLines(FilesList, files.Select(t => string.Format("{0}\t{1}", t.Item1, t.Item2)).ToArray());
			Program.LogAndConsole($"{new FileInfo(FilesList).Length} bytes written to file '{FilesList}'\n");

			Program.LogAndConsole($"Writing file '{ExcludedFilesList}' (files excluded from import)");
			File.WriteAllLines(ExcludedFilesList, excluded.Select(t => string.Format("{0}\t{1}", t.Item1, t.Item2)).ToArray());
			Program.LogAndConsole($"{new FileInfo(ExcludedFilesList).Length} bytes written to file '{ExcludedFilesList}'\n");

			// calc stats

			// filter by prefix
			if (opts.Prefix != null)
			{
				files = files
					.Where(t => t.Item1.Replace('\\', '/').StartsWith(opts.Prefix, StringComparison.OrdinalIgnoreCase))
					.ToList()
				;
			}

			// filter by filter
			if (opts.FilterRx != null)
			{
				files = files
					.Where(t => opts.FilterRx.IsMatch(t.Item1.Replace('\\', '/')))
					.ToList()
				;
			}

			// build extensions map
			var exts = files
				.Select(t => Path.GetExtension(t.Item1))
				.Select(e => e.ToLowerInvariant())
				.GroupBy(e => e)
				.Select(g => $"{g.Key}({g.Count()}) ");
			Program.LogAndConsole("Files extensions: {0}\n", string.Join(" ", exts));
			/*files
				.Select(t => Path.GetExtension(t.Item1))
				.Select(e => e.ToLowerInvariant())
				.GroupBy(e => e)
				.ToList()
				.ForEach(g => Console.Write("{0}({1}) ", g.Key, g.Count()))
			;*/

			// dump extensions map
			Program.LogAndConsole($"Writing file '{DataExtsFileName}' (file stats by extensions)");
			using (var map = File.CreateText(DataExtsFileName))
			{
				// overview
				map.WriteLine("== Overview ==");
				map.WriteLine($"<all>    : Count: {files.Count,5}, Size: {files.Sum(f => (double)f.Item2) / 1024.0,10:0.00} Kb");

				files
					.Select(t => new { Ext = (Path.GetExtension(t.Item1) ?? "").ToLowerInvariant(), Size = t.Item2 })
					.GroupBy(x => x.Ext)
					.OrderBy(g => g.Sum(f => f.Size))
					//.OrderBy(g => g.Key)
					.ToList()
					.ForEach(g => map.WriteLine($"{g.Key,-9}: Count: {g.Count(),5}, Size: {g.Sum(f => f.Size) / 1024.0,10:0.00} Kb, Avg size: {g.Sum(f => f.Size) / 1024.0 / g.Count(),7:0.00} Kb"))
				;

				map.WriteLine();
				map.WriteLine();
				map.WriteLine("== Detailed ==");

				files
					.GroupBy(t => (Path.GetExtension(t.Item1) ?? "").ToLowerInvariant())
					.ToList()
					.ForEach(g =>
					{
						if (Program.Exit)
							throw new Stop();

						map.WriteLine($"{g.Key,-9}({g.Count(),5}):", g.Key, g.Count());

						foreach (var f in g.OrderByDescending(ff => ff.Item2))
						{
							if (Program.Exit)
								throw new Stop();

							map.WriteLine($"{f.Item2,10} {f.Item1}");
						}
						map.WriteLine();
					})
				;
			}
			Program.LogAndConsole($"{new FileInfo(DataExtsFileName).Length} bytes written to file '{DataExtsFileName}'\n");

			// dump files by size
			Program.LogAndConsole($"Writing file '{DataSizesFileName}' (files ordered by size, descending)");
			using (var map = File.CreateText(DataSizesFileName))
			{
				files
					.Select(t => new { Spec = t.Item1, Size = t.Item2 })
					.OrderByDescending(inf => inf.Size)
					.ToList()
					.ForEach(inf =>
					{
						if (Program.Exit)
							throw new Stop();

						map.WriteLine($"{inf.Size / 1024.0,10:0.0} KiB	{inf.Spec}");
					})
				;
			}
			Program.LogAndConsole($"{new FileInfo(DataSizesFileName).Length} bytes written to file '{DataSizesFileName}'\n");
		}

		void WalkItem(IVSSItem item)
		{
			if (item.Type == 1)
			{
				_files.Add(Tuple.Create(item.Spec, item.Size));
				if (_files.Count % 1000 == 0)
					Program.LogAndConsole($"Found {_files.Count} files");
			}
			else
			{
				WalkItems(item.Items);
			}
		}

		void WalkItems(IVSSItems items)
		{
			foreach (IVSSItem item in items)
			{
				if (Program.Exit)
					throw new Stop();

				WalkItem(item);
			}
		}
	}
}
