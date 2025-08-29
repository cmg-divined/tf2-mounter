using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

public class VpkEntry
{
	public string FileName { get; set; }
	public string DirectoryPath { get; set; }
	public string Extension { get; set; }
	public uint CRC32 { get; set; }
	public ushort PreloadBytes { get; set; }
	public ushort ArchiveIndex { get; set; }
	public uint EntryOffset { get; set; }
	public uint EntryLength { get; set; }
	public byte[] PreloadData { get; set; }

	public string GetFullPath()
	{
		if (string.IsNullOrEmpty(DirectoryPath))
		{
			return $"{FileName}.{Extension}";
		}
		return $"{DirectoryPath}/{FileName}.{Extension}";
	}
}

public class VpkPackage : IDisposable
{
	private readonly string _vpkPath;
	private readonly List<VpkEntry> _entries = new();
	private readonly Dictionary<string, List<VpkEntry>> _entriesByExtension = new();
	private bool _disposed = false;

	public IReadOnlyList<VpkEntry> AllEntries => _entries;
	public IReadOnlyDictionary<string, List<VpkEntry>> Entries => _entriesByExtension;

	public VpkPackage(string vpkPath)
	{
		_vpkPath = vpkPath;
		Read();
	}

	private void Read()
	{
		using var stream = new FileStream(_vpkPath, FileMode.Open, FileAccess.Read, FileShare.Read);
		using var reader = new BinaryReader(stream);

		// Read VPK header
		var signature = reader.ReadUInt32();
		if (signature != 0x55AA1234)
		{
			throw new InvalidDataException($"Invalid VPK signature: 0x{signature:X8}");
		}

		var version = reader.ReadUInt32();
		var treeSize = reader.ReadUInt32();

		if (version == 2)
		{
			// VPK version 2 has additional header fields
			var fileDataSectionSize = reader.ReadUInt32();
			var archiveMD5SectionSize = reader.ReadUInt32();
			var otherMD5SectionSize = reader.ReadUInt32();
			var signatureSectionSize = reader.ReadUInt32();
		}

		var treeStartOffset = stream.Position;
		var treeEndOffset = treeStartOffset + treeSize;

		// Log.Info($"VPK {Path.GetFileName(_vpkPath)}: Version {version}, TreeSize {treeSize}, TreeEnd {treeEndOffset}");

		// Parse directory tree following VPK specification exactly
		var extensionCount = 0;
		while (stream.Position < treeEndOffset)
		{
			var extension = ReadNullTerminatedString(reader, treeEndOffset);
			if (string.IsNullOrEmpty(extension))
				break; // End of all extensions

			extensionCount++;
			// Log.Info($"VPK {Path.GetFileName(_vpkPath)}: Extension #{extensionCount}: '{extension}'");

			var directoryCount = 0;
			while (stream.Position < treeEndOffset)
			{
				var directoryPath = ReadNullTerminatedString(reader, treeEndOffset);
				if (string.IsNullOrEmpty(directoryPath))
					break; // End of directories for this extension

				directoryCount++;
				
				// Handle special case where directory is a single space (root directory)
				if (directoryPath == " ")
					directoryPath = "";

				// Log.Info($"VPK {Path.GetFileName(_vpkPath)}: Directory #{directoryCount}: '{directoryPath}' for extension '{extension}'");

				var fileCount = 0;
				while (stream.Position < treeEndOffset)
				{
					var fileName = ReadNullTerminatedString(reader, treeEndOffset);
					if (string.IsNullOrEmpty(fileName))
						break; // End of files for this directory

					fileCount++;

					// Ensure we don't read past the tree (18 bytes for entry data + terminator)
					if (stream.Position + 18 > treeEndOffset)
					{
						Log.Warning($"VPK {Path.GetFileName(_vpkPath)}: Hit tree boundary while reading file entry");
						break;
					}

					var entry = new VpkEntry
					{
						FileName = fileName,
						DirectoryPath = directoryPath,
						Extension = extension,
						CRC32 = reader.ReadUInt32(),
						PreloadBytes = reader.ReadUInt16(),
						ArchiveIndex = reader.ReadUInt16(),
						EntryOffset = reader.ReadUInt32(),
						EntryLength = reader.ReadUInt32()
					};

					// Check for terminator (should be 0xFFFF)
					var terminator = reader.ReadUInt16();
					if (terminator != 0xFFFF)
					{
						Log.Warning($"VPK {Path.GetFileName(_vpkPath)}: Expected terminator 0xFFFF, got 0x{terminator:X4}");
					}

					// Read preload data if any
					if (entry.PreloadBytes > 0)
					{
						if (stream.Position + entry.PreloadBytes > treeEndOffset)
						{
							Log.Warning($"VPK {Path.GetFileName(_vpkPath)}: Preload data extends past tree boundary");
							break;
						}
						entry.PreloadData = reader.ReadBytes(entry.PreloadBytes);
					}

					_entries.Add(entry);

					// Add to extension dictionary
					if (!_entriesByExtension.ContainsKey(extension))
						_entriesByExtension[extension] = new List<VpkEntry>();
					_entriesByExtension[extension].Add(entry);

					// if (fileCount <= 3) // Log first few files
					// {
					//	Log.Info($"VPK {Path.GetFileName(_vpkPath)}: File #{fileCount}: '{entry.GetFullPath()}'");
					// }
				}
				
				// Log.Info($"VPK {Path.GetFileName(_vpkPath)}: Directory '{directoryPath}' contained {fileCount} files");
			}
			
			// Log.Info($"VPK {Path.GetFileName(_vpkPath)}: Extension '{extension}' contained {directoryCount} directories");
		}

		Log.Info($"VPK {Path.GetFileName(_vpkPath)}: Loaded {_entries.Count} entries across {_entriesByExtension.Count} extensions");
	}

	private string ReadNullTerminatedString(BinaryReader reader, long maxPosition)
	{
		var bytes = new List<byte>();
		var startPos = reader.BaseStream.Position;

		try
		{
			while (reader.BaseStream.Position < maxPosition)
			{
				var b = reader.ReadByte();
				if (b == 0)
					break;

				// Allow printable ASCII characters and some common control chars
				if (b >= 32 && b <= 126) // Standard printable ASCII
				{
					bytes.Add(b);
				}
				else if (b == 9 || b == 10 || b == 13) // Tab, LF, CR
				{
					bytes.Add(b);
				}
				else
				{
					// Non-printable character - might be corrupted data or end of strings
					// But in VPK files, some paths might have unusual characters, so be lenient
					// Log.Warning($"VPK {Path.GetFileName(_vpkPath)}: Non-standard character 0x{b:X2} at position {reader.BaseStream.Position - 1}");
					bytes.Add(b); // Include it but warn
				}

				// Prevent runaway strings
				if (bytes.Count > 1000)
				{
					Log.Warning($"VPK {Path.GetFileName(_vpkPath)}: String too long at position {startPos}, truncating");
					break;
				}
			}
		}
		catch (EndOfStreamException)
		{
			Log.Warning($"VPK {Path.GetFileName(_vpkPath)}: Hit end of stream while reading string at position {startPos}");
			return string.Empty;
		}

		var result = Encoding.ASCII.GetString(bytes.ToArray());
		
		// Log very long strings for debugging
		// if (result.Length > 200)
		// {
		//	Log.Info($"VPK {Path.GetFileName(_vpkPath)}: Long string at {startPos}: '{result.Substring(0, 50)}...' (length: {result.Length})");
		// }

		return result;
	}

	public VpkEntry FindEntry(string path)
	{
		return _entries.Find(e => e.GetFullPath().Equals(path, StringComparison.OrdinalIgnoreCase));
	}

	public Stream GetEntryStream(VpkEntry entry)
	{
		if (entry == null)
			throw new ArgumentNullException(nameof(entry));

		var data = new byte[entry.EntryLength];
		
		// Handle preload data
		if (entry.PreloadData != null && entry.PreloadData.Length > 0)
		{
			Array.Copy(entry.PreloadData, 0, data, 0, Math.Min(entry.PreloadData.Length, data.Length));
		}

		// Read remaining data from archive
		if (entry.EntryLength > (entry.PreloadData?.Length ?? 0))
		{
			var remainingSize = entry.EntryLength - (uint)(entry.PreloadData?.Length ?? 0);
			var remainingData = ReadFromArchive(entry, remainingSize);
			
			if (remainingData != null)
			{
				Array.Copy(remainingData, 0, data, entry.PreloadData?.Length ?? 0, remainingData.Length);
			}
		}

		return new MemoryStream(data);
	}

	private byte[] ReadFromArchive(VpkEntry entry, uint size)
	{
		try
		{
			string archivePath;
			
			if (entry.ArchiveIndex == 0x7FFF)
			{
				// Data is in the main VPK file
				archivePath = _vpkPath;
			}
			else
			{
				// Data is in a separate archive file
				var baseName = Path.GetFileNameWithoutExtension(_vpkPath);
				var directory = Path.GetDirectoryName(_vpkPath);
				
				// Remove "_dir" suffix if present for archive file names
				// e.g., "tf2_misc_dir" -> "tf2_misc" for archive files
				if (baseName.EndsWith("_dir", StringComparison.OrdinalIgnoreCase))
				{
					baseName = baseName.Substring(0, baseName.Length - 4);
				}
				
				archivePath = Path.Combine(directory, $"{baseName}_{entry.ArchiveIndex:D3}.vpk");
			}

			// Check if archive file exists
			if (!File.Exists(archivePath))
			{
				Log.Warning($"VPK {Path.GetFileName(_vpkPath)}: Archive file not found: {Path.GetFileName(archivePath)} for {entry.GetFullPath()}");
				return new byte[size]; // Return empty data as fallback
			}

			using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
			stream.Seek(entry.EntryOffset, SeekOrigin.Begin);
			
			var buffer = new byte[size];
			var bytesRead = stream.Read(buffer, 0, (int)size);
			
			if (bytesRead != size)
			{
				Log.Warning($"VPK {Path.GetFileName(_vpkPath)}: Expected {size} bytes, got {bytesRead} for {entry.GetFullPath()}");
			}
			
			return buffer;
		}
		catch (Exception ex)
		{
			Log.Error($"VPK {Path.GetFileName(_vpkPath)}: Failed to read from archive: {ex.Message}");
			return new byte[size]; // Return empty data as fallback
		}
	}

	public void Dispose()
	{
		if (!_disposed)
		{
			_entries.Clear();
			_entriesByExtension.Clear();
			_disposed = true;
		}
	}
}
