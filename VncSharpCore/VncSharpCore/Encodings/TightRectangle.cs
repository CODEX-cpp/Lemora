// Codifica Tight (7) del protocollo RFB, scritta per Lemora. Riferimenti: specifica RFB / TightVNC "Tight Encoding", TigerVNC TightDecoder.
// Un rettangolo Tight puo' essere: tinta unita (Fill), JPEG, oppure pixel compressi con zlib dopo un filtro (copia, palette, gradiente).
// I 4 flussi zlib restano vivi tra un rettangolo e l'altro (TightState) e il server li azzera con i bit bassi del byte di controllo.

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using VncSharpCore.zlib.NET;

namespace VncSharpCore.Encodings
{
	/// <summary>I quattro flussi zlib di Tight, che durano per tutta la connessione.</summary>
	public sealed class TightState
	{
		private readonly MemoryStream[] outs = new MemoryStream[4];
		private readonly ZOutputStream[] zs = new ZOutputStream[4];

		/// <summary>il server chiede di ripartire con un flusso zlib nuovo</summary>
		public void Reset(int i) { zs[i] = null; outs[i] = null; }

		/// <summary>decomprime <paramref name="data"/> (tutti i byte compressi del rettangolo) e restituisce esattamente <paramref name="expected"/> byte</summary>
		public byte[] Inflate(int i, byte[] data, int expected)
		{
			if (zs[i] == null) { outs[i] = new MemoryStream(); zs[i] = new ZOutputStream(outs[i]); }
			outs[i].SetLength(0);
			zs[i].Write(data, 0, data.Length);
			if (outs[i].Length != expected)
				throw new VncProtocolException("Tight: dati decompressi " + outs[i].Length + " byte invece di " + expected + " (flusso " + i + ")");
			return outs[i].ToArray();
		}
	}

	public sealed class TightRectangle : EncodedRectangle
	{
		public TightRectangle(RfbProtocol rfb, Framebuffer framebuffer, Rectangle rectangle)
			: base(rfb, framebuffer, rectangle, RfbProtocol.TIGHT_ENCODING)
		{
		}

		private BinaryReader R { get { return rfb.Reader; } }

		/// <summary>con pixel a 32 bit e 8 bit per colore (profondita' 24) Tight manda 3 byte R,G,B invece di 4</summary>
		private bool ThreeByteColour()
		{
			var f = framebuffer;
			return f.BitsPerPixel == 32 && f.RedMax == 255 && f.GreenMax == 255 && f.BlueMax == 255
				&& (f.RedShift == 0 || f.RedShift == 8 || f.RedShift == 16)
				&& (f.GreenShift == 0 || f.GreenShift == 8 || f.GreenShift == 16)
				&& (f.BlueShift == 0 || f.BlueShift == 8 || f.BlueShift == 16);
		}

		private int PixelSize { get { return ThreeByteColour() ? 3 : framebuffer.BitsPerPixel / 8; } }

		private static byte[] ReadExactly(BinaryReader r, int count)
		{
			if (count == 0) return new byte[0];
			var b = r.ReadBytes(count);
			if (b.Length != count) throw new VncProtocolException("Tight: connessione chiusa a meta' di un rettangolo");
			return b;
		}

		/// <summary>da byte nel formato pixel del server a un intero GDI+ (0xFFRRGGBB)</summary>
		private int ToArgb(byte[] d, int off)
		{
			var f = framebuffer;
			if (ThreeByteColour()) return 0xFF << 24 | d[off] << 16 | d[off + 1] << 8 | d[off + 2];
			if (f.BitsPerPixel == 32)
			{
				var p = (uint)d[off] | (uint)d[off + 1] << 8 | (uint)d[off + 2] << 16 | (uint)d[off + 3] << 24;
				return 0xFF << 24 | (int)((p >> f.RedShift) & (uint)f.RedMax) << 16 | (int)((p >> f.GreenShift) & (uint)f.GreenMax) << 8 | (int)((p >> f.BlueShift) & (uint)f.BlueMax);
			}
			if (f.BitsPerPixel == 16)
			{
				var p = (uint)d[off] | (uint)d[off + 1] << 8;
				var r = (int)((p >> f.RedShift) & (uint)f.RedMax) * 255 / f.RedMax;
				var g = (int)((p >> f.GreenShift) & (uint)f.GreenMax) * 255 / f.GreenMax;
				var b = (int)((p >> f.BlueShift) & (uint)f.BlueMax) * 255 / f.BlueMax;
				return 0xFF << 24 | r << 16 | g << 8 | b;
			}
			throw new VncProtocolException("Tight: formato pixel a " + f.BitsPerPixel + " bit non supportato");
		}

		private int ReadPixelValue()
		{
			var b = ReadExactly(R, PixelSize);
			return ToArgb(b, 0);
		}

		/// <summary>lunghezza "compatta": 1-3 byte, 7 bit per byte, il bit alto dice che ne segue un altro</summary>
		private int ReadCompactLength()
		{
			int len = R.ReadByte();
			if ((len & 0x80) != 0)
			{
				int b = R.ReadByte();
				len = (len & 0x7F) | (b & 0x7F) << 7;
				if ((b & 0x80) != 0)
				{
					b = R.ReadByte();
					len |= b << 14;
				}
			}
			return len;
		}

		public override void Decode()
		{
			int control = R.ReadByte();
			for (var i = 0; i < 4; i++)
				if ((control >> i & 1) != 0) rfb.TightState.Reset(i);

			var kind = control >> 4;
			if (kind == 8)            // Fill: tutto il rettangolo di un solo colore
			{
				FillRectangle(rectangle, ReadPixelValue());
				return;
			}
			if (kind == 9)            // JPEG
			{
				var jpg = ReadExactly(R, ReadCompactLength());
				DecodeJpeg(jpg);
				return;
			}
			if (kind > 8) throw new VncProtocolException("Tight: tipo di rettangolo " + kind + " non supportato");

			// compressione "di base": bit 4-5 = flusso zlib, bit 6 = c'e' un byte di filtro
			var stream = kind & 3;
			var filter = 0;
			if ((kind & 4) != 0) filter = R.ReadByte();

			int w = rectangle.Width, h = rectangle.Height;
			int[] palette = null;
			int rowBytes;
			switch (filter)
			{
				case 0:
					rowBytes = w * PixelSize;
					break;
				case 1:
					var n = R.ReadByte() + 1;
					palette = new int[n];
					for (var k = 0; k < n; k++) palette[k] = ReadPixelValue();
					rowBytes = n == 2 ? (w + 7) / 8 : w;
					break;
				case 2:
					if (!ThreeByteColour()) throw new VncProtocolException("Tight: filtro gradiente senza colori a 24 bit");
					rowBytes = w * 3;
					break;
				default:
					throw new VncProtocolException("Tight: filtro " + filter + " sconosciuto");
			}

			var dataSize = rowBytes * h;
			byte[] data;
			if (dataSize < 12) data = ReadExactly(R, dataSize);     // sotto i 12 byte non si comprime
			else
			{
				var compressed = ReadExactly(R, ReadCompactLength());
				data = rfb.TightState.Inflate(stream, compressed, dataSize);
			}

			if (filter == 0) CopyFilter(data, w, h);
			else if (filter == 1) PaletteFilter(data, palette, w, h, rowBytes);
			else GradientFilter(data, w, h);
		}

		private void CopyFilter(byte[] d, int w, int h)
		{
			var ps = PixelSize;
			var o = 0;
			for (var y = 0; y < h; y++)
				for (var x = 0; x < w; x++, o += ps)
					framebuffer[y * w + x] = ToArgb(d, o);
		}

		private void PaletteFilter(byte[] d, int[] palette, int w, int h, int rowBytes)
		{
			for (var y = 0; y < h; y++)
			{
				for (var x = 0; x < w; x++)
				{
					int idx = palette.Length == 2
						? d[y * rowBytes + (x >> 3)] >> (7 - (x & 7)) & 1
						: d[y * rowBytes + x];
					if (idx >= palette.Length) throw new VncProtocolException("Tight: indice di palette fuori limite");
					framebuffer[y * w + x] = palette[idx];
				}
			}
		}

		/// <summary>ogni canale e' la differenza dalla previsione sinistra + sopra - sopra-sinistra (limitata a 0..255)</summary>
		private void GradientFilter(byte[] d, int w, int h)
		{
			var prev = new int[w * 3];
			var cur = new int[w * 3];
			for (var y = 0; y < h; y++)
			{
				for (var x = 0; x < w; x++)
				{
					for (var c = 0; c < 3; c++)
					{
						var left = x > 0 ? cur[(x - 1) * 3 + c] : 0;
						var up = y > 0 ? prev[x * 3 + c] : 0;
						var upLeft = x > 0 && y > 0 ? prev[(x - 1) * 3 + c] : 0;
						var pred = left + up - upLeft;
						if (pred < 0) pred = 0; else if (pred > 255) pred = 255;
						cur[x * 3 + c] = (pred + d[(y * w + x) * 3 + c]) & 0xFF;
					}
					framebuffer[y * w + x] = 0xFF << 24 | cur[x * 3] << 16 | cur[x * 3 + 1] << 8 | cur[x * 3 + 2];
				}
				var t = prev; prev = cur; cur = t;
			}
		}

		private void DecodeJpeg(byte[] jpg)
		{
			int w = rectangle.Width, h = rectangle.Height;
			using (var ms = new MemoryStream(jpg))
			using (var bmp = new Bitmap(ms))
			{
				if (bmp.Width != w || bmp.Height != h)
					throw new VncProtocolException("Tight: il JPEG e' " + bmp.Width + "x" + bmp.Height + " invece di " + w + "x" + h);
				var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
				try
				{
					var row = new int[w];
					for (var y = 0; y < h; y++)
					{
						Marshal.Copy(bd.Scan0 + y * bd.Stride, row, 0, w);
						for (var x = 0; x < w; x++) framebuffer[y * w + x] = row[x] | unchecked((int)0xFF000000);
					}
				}
				finally { bmp.UnlockBits(bd); }
			}
		}
	}
}
