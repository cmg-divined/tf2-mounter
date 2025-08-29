using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Sandbox.Mounting;

using IO = System.IO;

public class TF2Mount : BaseGameMount
{
	private const long AppId = 440; // Team Fortress 2

	private readonly List<VpkPackage> _packages = new();
	private readonly Dictionary<string, VpkEntry> _mdlFiles = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, VpkEntry> _vtfFiles = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, VpkEntry> _vmtFiles = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, List<VpkEntry>> _materialNameIndex = new(StringComparer.OrdinalIgnoreCase);
	private string _gameRoot;

	public override string Ident => "tf2";
	public override string Title => "Team Fortress 2";

	protected override void Initialize(InitializeContext context)
	{
		try
		{
			if (!context.IsAppInstalled(AppId))
			{
				Log.Info($"Team Fortress 2 (Steam App {AppId}) not installed.");
				return;
			}

			_gameRoot = context.GetAppDirectory(AppId);
			if (string.IsNullOrWhiteSpace(_gameRoot) || !IO.Directory.Exists(_gameRoot))
			{
				Log.Warning("TF2 root directory not found.");
				return;
			}

			Log.Info($"Found TF2 installation at: {_gameRoot}");

			// Load TF2 VPK files
			LoadVpkFiles();

			// Scan VPKs for assets
			ScanVpkAssets();

			IsInstalled = _packages.Count > 0 && (_mdlFiles.Count > 0 || _vtfFiles.Count > 0 || _vmtFiles.Count > 0);
			Log.Info($"TF2 Mount: Found {_packages.Count} VPK files with {_mdlFiles.Count} models, {_vtfFiles.Count} textures, {_vmtFiles.Count} materials.");
		}
		catch (Exception ex)
		{
			Log.Error($"TF2Mount Initialize failed: {ex.Message}");
		}
	}

	private void LoadVpkFiles()
	{
		var tfFolder = Path.Combine(_gameRoot, "tf");
		if (!IO.Directory.Exists(tfFolder))
		{
			Log.Warning("TF2 'tf' folder not found.");
			return;
		}

		// Find all VPK files in the tf directory
		var vpkFiles = IO.Directory.EnumerateFiles(tfFolder, "*_dir.vpk", SearchOption.TopDirectoryOnly);
		
		foreach (var vpkPath in vpkFiles)
		{
			try
			{
				var package = new VpkPackage(vpkPath);
				_packages.Add(package);
				Log.Info($"Loaded VPK: {Path.GetFileName(vpkPath)}");
			}
			catch (Exception ex)
			{
				Log.Warning($"Failed to load VPK {Path.GetFileName(vpkPath)}: {ex.Message}");
			}
		}
	}

	private void ScanVpkAssets()
	{
		var extensionCounts = new Dictionary<string, int>();
		
		foreach (var package in _packages)
		{
			foreach (var (extension, entries) in package.Entries)
			{
				var ext = extension.ToLowerInvariant();
				
				// Track all extensions we find
				if (!extensionCounts.ContainsKey(ext))
					extensionCounts[ext] = 0;
				extensionCounts[ext] += entries.Count;
				
				foreach (var entry in entries)
				{
					var fullPath = entry.GetFullPath();
					
					switch (ext)
					{
						case "mdl":
							_mdlFiles[fullPath] = entry;
							break;
						case "vtf":
							_vtfFiles[fullPath] = entry;
							break;
						case "vmt":
							_vmtFiles[fullPath] = entry;
							// Build material name index for faster lookup
							BuildMaterialNameIndex(entry);
							break;
						// TF2 also uses these model-related extensions
						case "dx80.vtx":
						case "dx90.vtx":
						case "sw.vtx":
						case "phy":
						case "vvd":
							// These are supporting files for models, don't mount them separately
							// but count them for logging
							break;
					}
				}
			}
		}
		
		// Log what extensions we found to help debug
		Log.Info($"TF2 Found extensions: {string.Join(", ", extensionCounts.Select(kvp => $"{kvp.Key}({kvp.Value})"))}");	
	}

	protected override Task Mount(MountContext context)
	{
		try
		{
			// Register models
			foreach (var kvp in _mdlFiles)
			{
				var path = kvp.Key.Replace('\\', '/');
				context.Add(ResourceType.Model, path, new TF2Model(kvp.Value, _packages, this));
			}

			// Register textures
			foreach (var kvp in _vtfFiles)
			{
				var path = kvp.Key.Replace('\\', '/');
				context.Add(ResourceType.Texture, path, new TF2Texture(kvp.Value, _packages));
			}

			// Register materials
			foreach (var kvp in _vmtFiles)
			{
				var path = kvp.Key.Replace('\\', '/');
				context.Add(ResourceType.Material, path, new TF2Material(kvp.Value, _packages));
			}

			IsMounted = true;
			Log.Info($"TF2 Mount: Registered {_mdlFiles.Count} models, {_vtfFiles.Count} textures, {_vmtFiles.Count} materials.");
		}
		catch (Exception ex)
		{
			Log.Error($"TF2Mount failed: {ex.Message}");
		}

		return Task.CompletedTask;
	}

	public void Dispose()
	{
		foreach (var package in _packages)
		{
			package?.Dispose();
		}
		_packages.Clear();
		_mdlFiles.Clear();
		_vtfFiles.Clear();
		_vmtFiles.Clear();
		_materialNameIndex.Clear();
	}

	private void BuildMaterialNameIndex(VpkEntry vmtEntry)
	{
		try
		{
			var fullPath = vmtEntry.GetFullPath();
			
			// Extract material name from path
			// e.g., "materials/models/player/hwm/spy.vmt" -> "spy"
			var fileName = Path.GetFileNameWithoutExtension(fullPath);
			
			if (!_materialNameIndex.ContainsKey(fileName))
			{
				_materialNameIndex[fileName] = new List<VpkEntry>();
			}
			
			_materialNameIndex[fileName].Add(vmtEntry);
		}
		catch (Exception ex)
		{
			Log.Warning($"Failed to index material {vmtEntry.GetFullPath()}: {ex.Message}");
		}
	}

	// Public method to find VMT entries by material name
	public List<VpkEntry> FindVmtEntriesByMaterialName(string materialName)
	{
		if (_materialNameIndex.TryGetValue(materialName, out var entries))
		{
			return entries;
		}
		return new List<VpkEntry>();
	}
}
