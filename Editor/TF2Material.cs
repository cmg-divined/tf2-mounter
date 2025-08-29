using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Sandbox;
using Sandbox.Mounting;


internal class TF2Material : ResourceLoader<TF2Mount>
{
	private readonly VpkEntry _entry;
	private readonly List<VpkPackage> _packages;

	public TF2Material(VpkEntry entry, List<VpkPackage> packages)
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
			using var reader = new StreamReader(stream, Encoding.UTF8);
			
			var vmtContent = reader.ReadToEnd();
			Log.Info($"Loading TF2 material: {_entry.GetFullPath()}");
			
			// Parse VMT file (simplified KeyValues parsing)
			var material = ParseVMT(vmtContent);
			
			return material;
		}
		catch (Exception ex)
		{
			Log.Error($"Failed to load TF2 material {_entry.GetFullPath()}: {ex.Message}");
			
			// Return a fallback material
			var fallback = Material.Create("model", "simple_color");
			fallback?.Set("Color", Texture.White);
			return fallback;
		}
	}

	private Material ParseVMT(string vmtContent)
	{
		try
		{
			// Basic VMT parsing - this is a simplified version
			// TODO: Implement proper KeyValues parser for complete VMT support
			
			var lines = vmtContent.Split('\n', StringSplitOptions.RemoveEmptyEntries);
			string shaderName = null;
			string baseTexture = null;
			string normalMap = null;
			
			// Find shader name (first line usually contains the shader)
			foreach (var line in lines)
			{
				var trimmed = line.Trim();
				if (trimmed.StartsWith("//") || string.IsNullOrEmpty(trimmed))
					continue;
					
				if (trimmed.Contains("LightmappedGeneric") || trimmed.Contains("\"LightmappedGeneric\""))
				{
					shaderName = "LightmappedGeneric";
					break;
				}
				else if (trimmed.Contains("VertexLitGeneric") || trimmed.Contains("\"VertexLitGeneric\""))
				{
					shaderName = "VertexLitGeneric";
					break;
				}
				else if (trimmed.Contains("UnlitGeneric") || trimmed.Contains("\"UnlitGeneric\""))
				{
					shaderName = "UnlitGeneric";
					break;
				}
			}

			// Extract texture parameters
			foreach (var line in lines)
			{
				var trimmed = line.Trim().ToLower();
				
				if (trimmed.Contains("$basetexture") && trimmed.Contains("\""))
				{
					var start = trimmed.IndexOf("\"", trimmed.IndexOf("$basetexture")) + 1;
					var end = trimmed.IndexOf("\"", start);
					if (end > start)
					{
						baseTexture = trimmed.Substring(start, end - start);
					}
				}
				else if (trimmed.Contains("$normalmap") && trimmed.Contains("\""))
				{
					var start = trimmed.IndexOf("\"", trimmed.IndexOf("$normalmap")) + 1;
					var end = trimmed.IndexOf("\"", start);
					if (end > start)
					{
						normalMap = trimmed.Substring(start, end - start);
					}
				}
			}

			// Create appropriate s&box material based on shader type
			Material material;
			
			if (shaderName == "LightmappedGeneric")
			{
				material = Material.Create("model", "standard");
			}
			else if (shaderName == "VertexLitGeneric")
			{
				material = Material.Create("model", "simple_lit");
			}
			else
			{
				material = Material.Create("model", "simple_color");
			}

			// Try to load and set the base texture
			if (!string.IsNullOrEmpty(baseTexture) && material != null)
			{
				try
				{
					// Convert TF2 texture path to VTF path
					var vtfPath = baseTexture + ".vtf";
					
					// Try to load the texture through the mount system
					// For now, use a placeholder
					var texture = Texture.Load($"mount://tf2/{vtfPath}") ?? Texture.White;
					
					material.Set("Color", texture);
					
					Log.Info($"Set base texture for material {_entry.GetFullPath()}: {baseTexture}");
				}
				catch (Exception ex)
				{
					Log.Warning($"Failed to load base texture '{baseTexture}' for material {_entry.GetFullPath()}: {ex.Message}");
					material.Set("Color", Texture.White);
				}
			}
			else
			{
				material?.Set("Color", Texture.White);
			}

			return material;
		}
		catch (Exception ex)
		{
			Log.Error($"Failed to parse VMT {_entry.GetFullPath()}: {ex.Message}");
			
			var fallback = Material.Create("model", "simple_color");
			fallback?.Set("Color", Texture.White);
			return fallback;
		}
	}
}
