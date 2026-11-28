using System.IO;

namespace VncSharpCore
{
	/// <summary>
	/// MemoryStream with a cap: the decompressed data of a rectangle cannot exceed what the rectangle can use.
	/// Without a cap a hostile VNC server sends a few compressed KB that become GB in memory ("zlib bomb").
	/// </summary>
	public sealed class CappedMemoryStream : MemoryStream
	{
		public long Cap { get; set; } = long.MaxValue;

		public override void Write(byte[] buffer, int offset, int count)
		{
			if (Position + count > Cap) throw new VncProtocolException($"decompressed data over the maximum for the rectangle ({Cap} bytes): invalid server");
			base.Write(buffer, offset, count);
		}

		public override void WriteByte(byte value)
		{
			if (Position + 1 > Cap) throw new VncProtocolException($"decompressed data over the maximum for the rectangle ({Cap} bytes): invalid server");
			base.WriteByte(value);
		}
	}
}
