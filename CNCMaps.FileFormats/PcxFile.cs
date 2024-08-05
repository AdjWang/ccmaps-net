using System.Drawing;
using System.IO;
using CNCMaps.FileFormats.VirtualFileSystem;

namespace CNCMaps.FileFormats {
	public class PcxFile : VirtualFile {
		public Bitmap Image { get => m_ImagePcx.PcxImage; }

		public PcxFile(Stream baseStream, string filename, int baseOffset, int fileSize, bool isBuffered = true)
			: base(baseStream, filename, baseOffset, fileSize, isBuffered) {
		}

		public void Initialize() {
			byte[] fileContent = Read((int)Length);
			m_ImagePcx = new Encodings.ImagePcx(fileContent);
		}

		private Encodings.ImagePcx m_ImagePcx { get; set; }
	}
}
