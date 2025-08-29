using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sandbox;
using Sandbox.Mounting;


internal class TF2Texture : ResourceLoader<TF2Mount>
{
	private readonly VpkEntry _entry;
	private readonly List<VpkPackage> _packages;

	public TF2Texture(VpkEntry entry, List<VpkPackage> packages)
	{
		_entry = entry;
		_packages = packages;
	}

	private Stream GetEntryStream()
	{
		// Find the package containing this entry
		foreach (var package in _packages)
		{
			var foundEntry = package.FindEntry(_entry.GetFullPath());
			if (foundEntry != null)
			{
				return package.GetEntryStream(foundEntry);
			}
		}

		throw new FileNotFoundException($"Entry not found in any package: {_entry.GetFullPath()}");
	}

	protected override object Load()
	{
		try
		{
			using var stream = GetEntryStream();
			
			Log.Info($"Loading TF2 texture: {_entry.GetFullPath()}");
			
			// Use the comprehensive VTF loader from TF2Vtf.cs
			return TF2Vtf.LoadFromStream(stream, _entry.GetFullPath());
		}
		catch (Exception ex)
		{
			Log.Error($"Failed to load TF2 texture {_entry.GetFullPath()}: {ex.Message}");
			
			// Return a fallback texture (pink/magenta missing texture)
			var fallbackData = new byte[64 * 64 * 4];
			for (int i = 0; i < fallbackData.Length; i += 4)
			{
				fallbackData[i] = 255;   // R
				fallbackData[i + 1] = 0; // G
				fallbackData[i + 2] = 255; // B
				fallbackData[i + 3] = 255; // A
			}
			
			return Texture.Create(64, 64)
				.WithData(fallbackData)
				.Finish();
		}
	}
}
