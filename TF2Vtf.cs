using System;
using System.Collections.Generic;
using IO = System.IO;
using Sandbox;

internal static class TF2Vtf
{
	public static Texture LoadFromStream(IO.Stream stream, string debugName = "")
	{
		try
		{
			using var br = new IO.BinaryReader(stream);
			var magic = br.ReadBytes(4);
			if (magic[0] != 'V' || magic[1] != 'T' || magic[2] != 'F' || magic[3] != 0)
			{
				string magicStr = System.Text.Encoding.ASCII.GetString(magic, 0, 3);
				Log.Warning($"[tf2 vtf] Bad magic for '{debugName}': expected 'VTF\\0', got '{magicStr}' + 0x{magic[3]:X2}");
				return Texture.White;
			}

			int major = br.ReadInt32();
			int minor = br.ReadInt32();
			int headerSize = br.ReadInt32();
			int width = br.ReadUInt16();
			int height = br.ReadUInt16();
			int flags = br.ReadInt32();
			ushort frames = br.ReadUInt16();
			ushort firstFrame = br.ReadUInt16();
			br.ReadBytes(4); // padding
			float reflectivityX = br.ReadSingle();
			float reflectivityY = br.ReadSingle();
			float reflectivityZ = br.ReadSingle();
			br.ReadBytes(4); // padding
			float bumpScale = br.ReadSingle();
			int highResImageFormat = br.ReadInt32();
			byte mipCount = br.ReadByte();
			int lowResImageFormat = br.ReadInt32();
			byte lowResWidth = br.ReadByte();
			byte lowResHeight = br.ReadByte();

			Log.Info($"[tf2 vtf] {debugName} v{major}.{minor} {width}x{height} fmt={(VtfFormat)highResImageFormat} mips={mipCount}");

			if (width <= 0 || height <= 0)
			{
				Log.Warning($"[tf2 vtf] Invalid size {width}x{height} for '{debugName}'");
				return Texture.White;
			}

			// High-res image data starts after header + low-res thumbnail
			long highResOffset = headerSize;
			int lowResBytes = CalcImageDataSize((VtfFormat)lowResImageFormat, lowResWidth, lowResHeight);
			highResOffset += lowResBytes;

			// Skip smaller mipmaps to get to the largest (stored smallest -> largest)
			int mips = Math.Max(1, (int)mipCount);
			for (int i = mips - 1; i >= 1; i--)
			{
				int mw = Math.Max(1, width >> i);
				int mh = Math.Max(1, height >> i);
				highResOffset += CalcImageDataSize((VtfFormat)highResImageFormat, mw, mh);
			}

			stream.Seek(highResOffset, IO.SeekOrigin.Begin);
			byte[] rgba = DecodeVTF(br, width, height, (VtfFormat)highResImageFormat);
			if (rgba == null)
			{
				Log.Warning($"[tf2 vtf] Unsupported format {(VtfFormat)highResImageFormat} for '{debugName}'");
				return Texture.White;
			}

			if (rgba == null || rgba.Length != width * height * 4)
			{
				Log.Warning($"[tf2 vtf] Invalid RGBA data for '{debugName}': expected {width * height * 4} bytes, got {rgba?.Length ?? 0}");
				return Texture.White;
			}

			var tex = Texture.Create(width, height).WithData(rgba).Finish();
			Log.Info($"[tf2 vtf] Successfully created texture {width}x{height} format={(VtfFormat)highResImageFormat} for '{debugName}'");
			return tex;
		}
		catch (Exception ex)
		{
			Log.Warning($"[tf2 vtf] Load error for '{debugName}': {ex.Message}");
			return Texture.White;
		}
	}

	private static byte[] DecodeVTF(IO.BinaryReader br, int width, int height, VtfFormat format)
	{
		int pixelCount = width * height;
		switch (format)
		{
			case VtfFormat.RGBA8888:
				return br.ReadBytes(pixelCount * 4);

			case VtfFormat.ABGR8888:
			{
				var src = br.ReadBytes(pixelCount * 4);
				var dst = new byte[pixelCount * 4];
				for (int i = 0, j = 0; i < src.Length; i += 4, j += 4)
				{
					byte a = src[i + 0];
					byte b = src[i + 1];
					byte g = src[i + 2];
					byte r = src[i + 3];
					dst[j + 0] = r; dst[j + 1] = g; dst[j + 2] = b; dst[j + 3] = a;
				}
				return dst;
			}

			case VtfFormat.BGRA8888:
			{
				var src = br.ReadBytes(pixelCount * 4);
				var dst = new byte[pixelCount * 4];
				for (int i = 0, j = 0; i < src.Length; i += 4, j += 4)
				{
					byte b = src[i + 0];
					byte g = src[i + 1];
					byte r = src[i + 2];
					byte a = src[i + 3];
					dst[j + 0] = r; dst[j + 1] = g; dst[j + 2] = b; dst[j + 3] = a;
				}
				return dst;
			}

			case VtfFormat.ARGB8888:
			{
				var src = br.ReadBytes(pixelCount * 4);
				var dst = new byte[pixelCount * 4];
				for (int i = 0, j = 0; i < src.Length; i += 4, j += 4)
				{
					byte a = src[i + 0];
					byte r = src[i + 1];
					byte g = src[i + 2];
					byte b = src[i + 3];
					dst[j + 0] = r; dst[j + 1] = g; dst[j + 2] = b; dst[j + 3] = a;
				}
				return dst;
			}

			case VtfFormat.BGR888:
			{
				var src = br.ReadBytes(pixelCount * 3);
				var dst = new byte[pixelCount * 4];
				for (int i = 0, j = 0; i < src.Length; i += 3, j += 4)
				{
					byte b = src[i + 0];
					byte g = src[i + 1];
					byte r = src[i + 2];
					dst[j + 0] = r; dst[j + 1] = g; dst[j + 2] = b; dst[j + 3] = 255;
				}
				return dst;
			}

			case VtfFormat.RGB888:
			{
				var src = br.ReadBytes(pixelCount * 3);
				var dst = new byte[pixelCount * 4];
				for (int i = 0, j = 0; i < src.Length; i += 3, j += 4)
				{
					byte r = src[i + 0];
					byte g = src[i + 1];
					byte b = src[i + 2];
					dst[j + 0] = r; dst[j + 1] = g; dst[j + 2] = b; dst[j + 3] = 255;
				}
				return dst;
			}

			case VtfFormat.DXT1:
				return DecompressDxt1(br, width, height);
			case VtfFormat.DXT1_ONEBITALPHA:
				return DecompressDxt1(br, width, height);
			case VtfFormat.DXT3:
				return DecompressDxt3(br, width, height);
			case VtfFormat.DXT5:
				return DecompressDxt5(br, width, height);

			default:
				return null;
		}
	}

	private static int CalcImageDataSize(VtfFormat format, int width, int height)
	{
		if (width <= 0 || height <= 0) return 0;
		switch (format)
		{
			case VtfFormat.RGBA8888:
			case VtfFormat.ABGR8888:
			case VtfFormat.BGRA8888:
			case VtfFormat.ARGB8888:
				return width * height * 4;
			case VtfFormat.RGB888:
			case VtfFormat.BGR888:
				return width * height * 3;
			case VtfFormat.DXT1:
			case VtfFormat.DXT1_ONEBITALPHA:
			{
				int blocksX = (width + 3) / 4;
				int blocksY = (height + 3) / 4;
				return blocksX * blocksY * 8;
			}
			case VtfFormat.DXT3:
			case VtfFormat.DXT5:
			{
				int blocksX = (width + 3) / 4;
				int blocksY = (height + 3) / 4;
				return blocksX * blocksY * 16;
			}
			default:
				return 0;
		}
	}

	// Simplified DXT1 decompression (basic implementation)
	private static byte[] DecompressDxt1(IO.BinaryReader br, int width, int height)
	{
		int blocksX = (width + 3) / 4;
		int blocksY = (height + 3) / 4;
		var outRgba = new byte[width * height * 4];

		for (int by = 0; by < blocksY; by++)
		{
			for (int bx = 0; bx < blocksX; bx++)
			{
				ushort c0 = br.ReadUInt16();
				ushort c1 = br.ReadUInt16();
				uint indices = br.ReadUInt32();

				var colors = new (byte r, byte g, byte b, byte a)[4];
				colors[0] = Convert565(c0, 255);
				colors[1] = Convert565(c1, 255);

				if (c0 > c1)
				{
					colors[2] = Lerp(colors[0], colors[1], 2, 3);
					colors[3] = Lerp(colors[0], colors[1], 1, 3);
				}
				else
				{
					colors[2] = ((byte)((colors[0].r + colors[1].r) / 2), (byte)((colors[0].g + colors[1].g) / 2), (byte)((colors[0].b + colors[1].b) / 2), 255);
					colors[3] = (0, 0, 0, 0);
				}

				WriteBlock(outRgba, width, bx * 4, by * 4, colors, indices);
			}
		}
		return outRgba;
	}

	// Proper DXT3 decompression
	private static byte[] DecompressDxt3(IO.BinaryReader br, int width, int height)
	{
		int blocksX = (width + 3) / 4;
		int blocksY = (height + 3) / 4;
		var outRgba = new byte[width * height * 4];

		for (int by = 0; by < blocksY; by++)
		{
			for (int bx = 0; bx < blocksX; bx++)
			{
				// DXT3 block structure: 8 bytes explicit alpha + 8 bytes color
				
				// Read explicit alpha block (8 bytes = 64 bits, 4 bits per pixel)
				ulong alphaData = br.ReadUInt64();
				
				// Read color block (8 bytes, same as DXT1)
				ushort c0 = br.ReadUInt16();
				ushort c1 = br.ReadUInt16();
				uint colorIndices = br.ReadUInt32();

				var colors = new (byte r, byte g, byte b)[4];
				colors[0] = Convert565ToRgb(c0);
				colors[1] = Convert565ToRgb(c1);
				colors[2] = LerpRgb(colors[0], colors[1], 2, 3);
				colors[3] = LerpRgb(colors[0], colors[1], 1, 3);

				// Write the 4x4 block
				for (int row = 0; row < 4; row++)
				{
					for (int col = 0; col < 4; col++)
					{
						int px = bx * 4 + col;
						int py = by * 4 + row;
						if (px >= width || py >= height) continue;

						int pixelIndex = row * 4 + col;
						int colorIdx = (int)((colorIndices >> (2 * pixelIndex)) & 0x3);
						
						// Extract 4-bit alpha value and scale to 8-bit
						int alphaIdx = pixelIndex * 4;
						byte alpha = (byte)((alphaData >> alphaIdx) & 0xF);
						alpha = (byte)(alpha * 255 / 15); // Scale 4-bit to 8-bit

						int dstIdx = (py * width + px) * 4;
						outRgba[dstIdx + 0] = colors[colorIdx].r;
						outRgba[dstIdx + 1] = colors[colorIdx].g;
						outRgba[dstIdx + 2] = colors[colorIdx].b;
						outRgba[dstIdx + 3] = alpha;
					}
				}
			}
		}
		return outRgba;
	}

	// Proper DXT5 decompression
	private static byte[] DecompressDxt5(IO.BinaryReader br, int width, int height)
	{
		int blocksX = (width + 3) / 4;
		int blocksY = (height + 3) / 4;
		var outRgba = new byte[width * height * 4];

		for (int by = 0; by < blocksY; by++)
		{
			for (int bx = 0; bx < blocksX; bx++)
			{
				// DXT5 block structure: 8 bytes alpha + 8 bytes color
				
				// Read alpha block (8 bytes)
				byte alpha0 = br.ReadByte();
				byte alpha1 = br.ReadByte();
				
				// Read 6 bytes of alpha indices (48 bits total, 3 bits per pixel)
				ulong alphaIndices = 0;
				for (int i = 0; i < 6; i++)
				{
					alphaIndices |= ((ulong)br.ReadByte()) << (i * 8);
				}
				
				// Calculate alpha values
				var alphas = new byte[8];
				alphas[0] = alpha0;
				alphas[1] = alpha1;
				
				if (alpha0 > alpha1)
				{
					// 8-alpha block: interpolate 6 additional alpha values
					for (int i = 2; i < 8; i++)
					{
						alphas[i] = (byte)((alpha0 * (8 - i) + alpha1 * (i - 1)) / 7);
					}
				}
				else
				{
					// 6-alpha block: interpolate 4 additional alpha values, plus 0 and 255
					for (int i = 2; i < 6; i++)
					{
						alphas[i] = (byte)((alpha0 * (6 - i) + alpha1 * (i - 1)) / 5);
					}
					alphas[6] = 0;
					alphas[7] = 255;
				}
				
				// Read color block (8 bytes, same as DXT1)
				ushort c0 = br.ReadUInt16();
				ushort c1 = br.ReadUInt16();
				uint colorIndices = br.ReadUInt32();

				var colors = new (byte r, byte g, byte b)[4];
				colors[0] = Convert565ToRgb(c0);
				colors[1] = Convert565ToRgb(c1);
				colors[2] = LerpRgb(colors[0], colors[1], 2, 3);
				colors[3] = LerpRgb(colors[0], colors[1], 1, 3);

				// Write the 4x4 block
				for (int row = 0; row < 4; row++)
				{
					for (int col = 0; col < 4; col++)
					{
						int px = bx * 4 + col;
						int py = by * 4 + row;
						if (px >= width || py >= height) continue;

						int pixelIndex = row * 4 + col;
						int colorIdx = (int)((colorIndices >> (2 * pixelIndex)) & 0x3);
						int alphaIdx = (int)((alphaIndices >> (3 * pixelIndex)) & 0x7);

						int dstIdx = (py * width + px) * 4;
						outRgba[dstIdx + 0] = colors[colorIdx].r;
						outRgba[dstIdx + 1] = colors[colorIdx].g;
						outRgba[dstIdx + 2] = colors[colorIdx].b;
						outRgba[dstIdx + 3] = alphas[alphaIdx];
					}
				}
			}
		}
		return outRgba;
	}

	private static (byte r, byte g, byte b, byte a) Convert565(ushort c, byte alpha)
	{
		byte r = (byte)(((c >> 11) & 0x1F) * 255 / 31);
		byte g = (byte)(((c >> 5) & 0x3F) * 255 / 63);
		byte b = (byte)((c & 0x1F) * 255 / 31);
		return (r, g, b, alpha);
	}

	private static (byte r, byte g, byte b) Convert565ToRgb(ushort c)
	{
		byte r = (byte)(((c >> 11) & 0x1F) * 255 / 31);
		byte g = (byte)(((c >> 5) & 0x3F) * 255 / 63);
		byte b = (byte)((c & 0x1F) * 255 / 31);
		return (r, g, b);
	}

	private static (byte r, byte g, byte b) LerpRgb((byte r, byte g, byte b) a, (byte r, byte g, byte b) b, int na, int d)
	{
		return (
			(byte)((a.r * na + b.r * (d - na)) / d),
			(byte)((a.g * na + b.g * (d - na)) / d),
			(byte)((a.b * na + b.b * (d - na)) / d));
	}

	private static (byte r, byte g, byte b, byte a) Lerp((byte r, byte g, byte b, byte a) a, (byte r, byte g, byte b, byte a) b, int na, int d)
	{
		return (
			(byte)((a.r * na + b.r * (d - na)) / d),
			(byte)((a.g * na + b.g * (d - na)) / d),
			(byte)((a.b * na + b.b * (d - na)) / d),
			255);
	}

	private static void WriteBlock(byte[] dst, int width, int x, int y, (byte r, byte g, byte b, byte a)[] colors, uint indices)
	{
		int heightPx = Math.Max(1, dst.Length / (4 * Math.Max(1, width)));
		for (int row = 0; row < 4; row++)
		{
			for (int col = 0; col < 4; col++)
			{
				int idx = (int)((indices >> (2 * (row * 4 + col))) & 0x3);
				int px = x + col;
				int py = y + row;
				if (px >= width || py >= heightPx) continue;
				int di = (py * width + px) * 4;
				dst[di + 0] = colors[idx].r;
				dst[di + 1] = colors[idx].g;
				dst[di + 2] = colors[idx].b;
				dst[di + 3] = colors[idx].a;
			}
		}
	}

	private enum VtfFormat
	{
		RGBA8888 = 0,
		ABGR8888 = 1,
		RGB888 = 2,
		BGR888 = 3,
		RGB565 = 4,
		I8 = 5,
		IA88 = 6,
		P8 = 7,
		A8 = 8,
		RGB888_BLUESCREEN = 9,
		BGR888_BLUESCREEN = 10,
		ARGB8888 = 11,
		BGRA8888 = 12,
		DXT1 = 13,
		DXT3 = 14,
		DXT5 = 15,
		BGRX8888 = 16,
		BGR565 = 17,
		BGRX5551 = 18,
		BGRA4444 = 19,
		DXT1_ONEBITALPHA = 20,
		BGRA5551 = 21,
		UV88 = 22,
		UVWQ8888 = 23,
		RGBA16161616F = 24,
		RGBA16161616 = 25,
		UVLX8888 = 26
	}
}
