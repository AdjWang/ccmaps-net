using System;

namespace CNCMaps.FileFormats.VirtualFileSystem {
	public interface IArchive : IDisposable {
		string FileName { get; }
		bool ContainsFile(string filename);
		VirtualFile OpenFile(string filename, FileFormat format, CacheMethod m = CacheMethod.Default);
	}
}
