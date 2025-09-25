using System;

namespace VssSvnConverter.Core
{
	class FileRevisionLite
	{
		public DateTime At;
		public int FileId;
		public string FileSpec
		{
			get => FileRevision.GetFile(FileId);
			set => FileId = FileRevision.GetFileId(value);
		}
		public int VssVersion;
	}
}