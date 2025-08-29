using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using IO = System.IO;
using Sandbox;
using Sandbox.Mounting;

internal class TF2Model : ResourceLoader<TF2Mount>
{
	private readonly VpkEntry _entry;
	private readonly List<VpkPackage> _packages;
	private readonly TF2Mount _mount;
	private static readonly bool FlipV = false; // debug toggle

	public TF2Model(VpkEntry entry, List<VpkPackage> packages, TF2Mount mount)
	{
		_entry = entry;
		_packages = packages;
		_mount = mount;
	}

	private Stream GetEntryStream(string path)
	{
		// Find the package containing this entry
		foreach (var package in _packages)
		{
			var foundEntry = package.FindEntry(path);
			if (foundEntry != null)
			{
				return package.GetEntryStream(foundEntry);
			}
		}

		return null;
	}

	private Stream GetMdlStream() => GetEntryStream(_entry.GetFullPath());

	private Stream GetVvdStream()
	{
		var mdlPath = _entry.GetFullPath();
		var vvdPath = System.IO.Path.ChangeExtension(mdlPath, ".vvd");
		return GetEntryStream(vvdPath);
	}

	private Stream GetVtxStream()
	{
		var mdlPath = _entry.GetFullPath();
		var basePath = System.IO.Path.ChangeExtension(mdlPath, null);
		
		// Try common VTX variants
		string[] vtxCandidates = { 
			basePath + ".dx90.vtx", 
			basePath + ".dx80.vtx", 
			basePath + ".sw.vtx", 
			basePath + ".vtx" 
		};
		
		foreach (var candidate in vtxCandidates)
		{
			var stream = GetEntryStream(candidate);
			if (stream != null)
				return stream;
		}
		
		return null;
	}

	protected override object Load()
	{
		try
		{
			Log.Info($"[tf2 mdl] Loading: {System.IO.Path.GetFileName(_entry.GetFullPath())}");
			
			using var mdlStream = GetMdlStream();
			if (mdlStream == null)
			{
				Log.Error($"[tf2 mdl] Failed to get MDL stream for {_entry.GetFullPath()}");
				return CreateFallbackModel();
			}

			using var br = new BinaryReader(mdlStream, System.Text.Encoding.ASCII, leaveOpen: false);

			// Read MDL header
			var id = new string(br.ReadChars(4));
			int version = br.ReadInt32();
			int checksum = br.ReadInt32();

			if (id != "IDST" && id != "MDLZ")
			{
				Log.Warning($"[tf2 mdl] Unexpected MDL id '{id}' in {_entry.GetFullPath()}");
				return CreateFallbackModel();
			}

			// Read stored internal name
			string mdlName = System.Text.Encoding.ASCII.GetString(br.ReadBytes(64)).TrimEnd('\0');
			int fileLength = br.ReadInt32();
			if (string.IsNullOrWhiteSpace(mdlName)) 
				mdlName = System.IO.Path.GetFileName(_entry.GetFullPath());

			Log.Info($"[tf2 mdl] Parsed header: name='{mdlName}' v{version} len={fileLength} checksum=0x{checksum:X8}");

			// Try to read basic header info
			try
			{
				var eye = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
				var illum = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
				var hullMin = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
				var hullMax = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
				var viewMin = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
				var viewMax = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
				int flags = br.ReadInt32();
				int numBones = br.ReadInt32();
				int boneIndex = br.ReadInt32();
				Log.Info($"[tf2 mdl] eye={eye} hull=({hullMin} .. {hullMax}) bones={numBones} flags=0x{flags:X8}");
			}
			catch (Exception)
			{
				// ignore partial header read errors for now
			}

			// Get companion files
			SourceVvd.Data vvdData = null;
			using (var vvdStream = GetVvdStream())
			{
				if (vvdStream != null)
				{
					vvdData = SourceVvd.Parse(vvdStream);
					Log.Info($"[tf2 mdl] VVD verts={vvdData.Vertices.Count} ver={vvdData.Version} lods={vvdData.LodCount}");
				}
				else
				{
					Log.Warning($"[tf2 mdl] No VVD file found for {mdlName}");
				}
			}

			SourceVtx.Summary vtxSummary = null;
			using (var vtxStream = GetVtxStream())
			{
				if (vtxStream != null)
				{
					vtxSummary = SourceVtx.Parse(vtxStream);
					Log.Info($"[tf2 mdl] VTX triangles={vtxSummary.TriangleCount} meshes={vtxSummary.Meshes}");
				}
				else
				{
					Log.Warning($"[tf2 mdl] No VTX file found for {mdlName}");
				}
			}

					// Create model builder
		var builder = Model.Builder.WithName(Path);

		// Build skeleton (bones) from MDL
		try
		{
			BuildSkeletonFromMdl(builder, mdlStream, mdlName);
		}
		catch (Exception ex)
		{
			Log.Warning($"[tf2 mdl] Skeleton build failed: {ex.Message}");
		}

		// Load materials for the model and create material dictionary
		var (materialDict, materialNames) = CreateMaterialsForModel(mdlStream, mdlName);

		// Build all meshes if we have companion files
		int builtCount = 0;
		if (vvdData != null && vtxSummary != null && vtxSummary.TriangleCount > 0)
		{
			builtCount = BuildAllMeshesLOD0(builder, vvdData, mdlName, materialDict, materialNames);
		}

		// Fallback mesh if nothing was built
		if (builtCount == 0)
			{
				var material = Material.Create("model", "simple_color");
				material?.Set("Color", Texture.White);
				
				var mesh = new Mesh(material);
				var verts = new List<SimpleVertex>
				{
					new SimpleVertex(new Vector3(-5, -5, 0), Vector3.Up, Vector3.Zero, new Vector2(0,0)),
					new SimpleVertex(new Vector3( 5, -5, 0), Vector3.Up, Vector3.Zero, new Vector2(1,0)),
					new SimpleVertex(new Vector3( 0,  5, 0), Vector3.Up, Vector3.Zero, new Vector2(0.5f,1))
				};
				mesh.CreateVertexBuffer(verts.Count, SimpleVertex.Layout, verts);
				mesh.CreateIndexBuffer(3, new int[]{0,1,2});
				mesh.Bounds = BBox.FromPositionAndSize(Vector3.Zero, new Vector3(10,10,0.1f));
				builder.AddMesh(mesh);
				Log.Info($"[tf2 mdl] Built placeholder mesh for '{mdlName}'");
			}

			var model = builder.Create();
			Log.Info($"[tf2 mdl] Successfully loaded model '{mdlName}' with {builtCount} meshes");
			return model;
		}
		catch (Exception ex)
		{
			Log.Error($"[tf2 mdl] Failed to load MDL '{_entry.GetFullPath()}': {ex.Message}");
			return CreateFallbackModel();
		}
	}

	private int BuildAllMeshesLOD0(ModelBuilder builder, SourceVvd.Data vvdData, string modelName, Dictionary<string, Material> materialDict, List<string> materialNames)
	{
		using var vtxStream = GetVtxStream();
		if (vtxStream == null) return 0;
		
		int added = 0;
		
		// Get hierarchy to know mesh counts per bodypart/model
		var h = SourceVtx.ParseHierarchy(vtxStream);
		
		// Compute vertex offsets for proper VTX -> VVD mapping
		var vertexOffsets = ComputeVertexOffsets(h, vtxStream);
		
		// Track which bodygroup choices we've added (for empty mesh creation)
		var addedChoices = new Dictionary<string, HashSet<int>>();
		
		Log.Info($"[tf2 mdl] Building all meshes: {h.BodyParts.Count} bodyparts");
		
		for (int bp = 0; bp < h.BodyParts.Count; bp++)
		{
			var bpInfo = h.BodyParts[bp];
			for (int m = 0; m < bpInfo.Models.Count; m++)
			{
				var modelInfo = bpInfo.Models[m];
				int meshCount = (modelInfo.Lods.Count > 0) ? modelInfo.Lods[0].MeshCount : 0;
				if (meshCount <= 0) continue;
				
				for (int me = 0; me < meshCount; me++)
				{
					try
					{
						var origIndices = SourceVtx.ReadLod0MeshOriginalIndices(vtxStream, bp, m, me);
						if (origIndices == null || origIndices.Count < 3) continue;
						
						Log.Info($"[tf2 mdl] Building mesh bp={bp} m={m} me={me} indices={origIndices.Count}");
						
						var outVerts = new List<SourceSkinnedVertex>();
						var outIndices = new List<int>();
						var vertMap = new Dictionary<int,int>();
						
						for (int i = 0; i < origIndices.Count; i++)
						{
							int vtxIndex = origIndices[i];
							int orig = vertexOffsets.BodyPartStart[bp] + vertexOffsets.ModelStart[bp][m] + vertexOffsets.MeshStart[bp][m][me] + Math.Max(0, vtxIndex);
							if (!vertMap.TryGetValue(orig, out int outIdx))
							{
								if (orig < 0 || orig >= vvdData.Vertices.Count) continue;
								var v = vvdData.Vertices[orig];
								
								// Map bone weights to Color32 format (match GMod's approach)
								byte i0 = v.Bones.Length > 0 ? v.Bones[0] : (byte)0;
								byte i1 = v.Bones.Length > 1 ? v.Bones[1] : (byte)0;
								byte i2 = v.Bones.Length > 2 ? v.Bones[2] : (byte)0;
								byte i3 = (byte)0;
								
								float w0f = v.Weights.Length > 0 ? v.Weights[0] : 1f;
								float w1f = v.Weights.Length > 1 ? v.Weights[1] : 0f;
								float w2f = v.Weights.Length > 2 ? v.Weights[2] : 0f;
								float w3f = 0f;
								
								// Convert to int for proper normalization (GMod approach)
								int w0 = (int)(w0f * 255f + 0.5f);
								int w1 = (int)(w1f * 255f + 0.5f);
								int w2 = (int)(w2f * 255f + 0.5f);
								int w3 = (int)(w3f * 255f + 0.5f);
								
								// Normalize weights to exactly sum to 255
								int sum = w0 + w1 + w2 + w3;
								if (sum != 255)
								{
									int diff = 255 - sum;
									int mx = Math.Max(Math.Max(w0, w1), Math.Max(w2, w3));
									if (w0 == mx) w0 += diff; 
									else if (w1 == mx) w1 += diff; 
									else if (w2 == mx) w2 += diff; 
									else w3 += diff;
								}
								
								// Debug log for first vertex
								if (outVerts.Count == 0)
								{
									Log.Info($"[tf2 mdl] Bone weights sample: bones=[{i0},{i1},{i2},{i3}] weights=[{w0},{w1},{w2},{w3}] sum={w0+w1+w2+w3}");
								}
								
								float u = v.UV.x;
								float vv = FlipV ? (1f - v.UV.y) : v.UV.y;
								
								// Compute basic tangent
								Vector3 t = new Vector3(1,0,0);
								
								var sv = new SourceSkinnedVertex(
									new Vector3(v.Position.x, v.Position.y, v.Position.z),
									new Vector3(v.Normal.x, v.Normal.y, v.Normal.z),
									t,
									new Vector2(u, vv),
									new Color32(i0, i1, i2, i3),
									new Color32((byte)w0, (byte)w1, (byte)w2, (byte)w3)
								);
								
								outIdx = outVerts.Count;
								outVerts.Add(sv);
								vertMap[orig] = outIdx;
							}
							outIndices.Add(outIdx);
						}

						if (outIndices.Count < 3) continue;
						
						// Remove incomplete triangles
						int rag = outIndices.Count % 3;
						if (rag != 0)
							outIndices.RemoveRange(outIndices.Count - rag, rag);
						if (outIndices.Count < 3) continue;
						
						// Flip triangle winding for s&box
						for (int t = 0; t + 2 < outIndices.Count; t += 3)
						{
							int tmp = outIndices[t + 1];
							outIndices[t + 1] = outIndices[t + 2];
							outIndices[t + 2] = tmp;
						}
						
						// Get material for this mesh by reading from MDL file structure (like GMod)
						Material material = null;
						try
						{
							int materialIndex = ReadMeshMaterialIndexFromMdl(GetMdlStream(), bp, m, me);
							if (materialIndex >= 0 && materialIndex < materialNames.Count)
							{
								string materialName = materialNames[materialIndex];
								if (materialDict.TryGetValue(materialName, out var foundMaterial))
								{
									material = foundMaterial;
									Log.Info($"[tf2 mdl] Using MDL material[{materialIndex}] '{materialName}' for mesh bp={bp} m={m} me={me}");
								}
								else
								{
									Log.Info($"[tf2 mdl] MDL material[{materialIndex}] '{materialName}' not found in dictionary for mesh bp={bp} m={m} me={me}");
								}
							}
							else
							{
								Log.Warning($"[tf2 mdl] Invalid MDL material index {materialIndex} for mesh bp={bp} m={m} me={me}");
							}
						}
						catch (Exception ex)
						{
							Log.Warning($"[tf2 mdl] Failed to read MDL material for mesh bp={bp} m={m} me={me}: {ex.Message}");
						}
						
						// Fallback to default material if not found
						if (material == null)
						{
							material = Material.Create($"tf2_model_bp{bp}_m{m}_me{me}", "simple_color");
							material?.Set("Color", Texture.White);
						}
						
						var outMesh = new Mesh($"tf2_mesh_bp{bp}_m{m}_me{me}", material);
						outMesh.CreateVertexBuffer(outVerts.Count, SourceSkinnedVertex.Layout, outVerts);
						outMesh.CreateIndexBuffer(outIndices.Count, outIndices.ToArray());
						outMesh.Bounds = BBox.FromPoints(outVerts.ConvertAll(v => v.position));
						
						// Create bodygroup name and choice index
						string displayGroup = $"bodypart_{bp}";
						int choiceIndex = m;
						
						builder.AddMesh(outMesh, 0, displayGroup, choiceIndex);
						
						// Track this choice for empty mesh creation
						if (!addedChoices.TryGetValue(displayGroup, out var set))
						{
							set = new HashSet<int>();
							addedChoices[displayGroup] = set;
						}
						set.Add(choiceIndex);
						
						added++;
						
						if (added == 1)
						{
							Log.Info($"[tf2 mdl] Built LOD0 mesh for bodypart {bp} model {m} mesh {me} tris={outIndices.Count/3}");
						}
					}
					catch (Exception ex)
					{
						Log.Warning($"[tf2 mdl] Failed to build mesh bp={bp} m={m} me={me}: {ex.Message}");
						continue; // Skip this mesh and continue with others
					}
				}
			}
		}
		
		// Add empty meshes for missing bodygroup choices (to allow toggling off)
		for (int bp = 0; bp < h.BodyParts.Count; bp++)
		{
			string displayGroup = $"bodypart_{bp}";
			if (!addedChoices.TryGetValue(displayGroup, out var set)) 
				set = new HashSet<int>();
			
			var bpInfo = h.BodyParts[bp];
			int maxChoices = Math.Max(2, bpInfo.Models.Count); // Ensure at least 2 choices (0=off, 1=on)
			
			for (int c = 0; c < maxChoices; c++)
			{
				if (set.Contains(c)) continue; // Already have this choice
				
				var emptyMesh = CreateEmptyMesh($"Empty Choice {c}");
				builder.AddMesh(emptyMesh, 0, displayGroup, c);
				Log.Info($"[tf2 mdl] Added empty bodygroup choice '{displayGroup}' #{c}");
			}
		}
		
		Log.Info($"[tf2 mdl] Added {added} meshes for LOD0 across {h.BodyParts.Count} bodyparts");
		return added;
	}

	private Model CreateFallbackModel()
	{
		var material = Material.Create("tf2_fallback", "simple_color");
		material?.Set("Color", Texture.White);
		
		var mesh = new Mesh(material);
		var verts = new List<SimpleVertex>
		{
			new SimpleVertex(new Vector3(-5, -5, 0), Vector3.Up, Vector3.Zero, new Vector2(0,0)),
			new SimpleVertex(new Vector3( 5, -5, 0), Vector3.Up, Vector3.Zero, new Vector2(1,0)),
			new SimpleVertex(new Vector3( 0,  5, 0), Vector3.Up, Vector3.Zero, new Vector2(0.5f,1))
		};
		mesh.CreateVertexBuffer(verts.Count, SimpleVertex.Layout, verts);
		mesh.CreateIndexBuffer(3, new int[]{0,1,2});
		mesh.Bounds = BBox.FromPositionAndSize(Vector3.Zero, new Vector3(10,10,0.1f));
		
		return Model.Builder
			.WithName(Path)
			.AddMesh(mesh)
			.Create();
	}

	private sealed class VertexOffsets
	{
		public List<int> BodyPartStart = new();
		public List<List<int>> ModelStart = new();
		public List<List<List<int>>> MeshStart = new();
	}

	// Compute vertex offsets for proper VTX -> VVD mapping (simplified version based on GMod)
	private VertexOffsets ComputeVertexOffsets(SourceVtx.Hierarchy h, IO.Stream vtxStream)
	{
		var offsets = new VertexOffsets();
		int globalVertexCount = 0;

		for (int bp = 0; bp < h.BodyParts.Count; bp++)
		{
			offsets.BodyPartStart.Add(globalVertexCount);
			var modelStarts = new List<int>();
			var meshStartsForModels = new List<List<int>>();
			int bodyPartVertexCount = 0;

			var bpInfo = h.BodyParts[bp];
			for (int m = 0; m < bpInfo.Models.Count; m++)
			{
				modelStarts.Add(bodyPartVertexCount);
				var meshStarts = new List<int>();
				int modelVertexCount = 0;

				int meshCount = (bpInfo.Models[m].Lods.Count > 0) ? bpInfo.Models[m].Lods[0].MeshCount : 0;
				for (int me = 0; me < meshCount; me++)
				{
					meshStarts.Add(modelVertexCount);
					
					// Count unique vertices in this mesh by finding max VTX index
					var indices = SourceVtx.ReadLod0MeshOriginalIndices(vtxStream, bp, m, me);
					int meshVertexCount = 0;
					if (indices != null && indices.Count > 0)
					{
						int maxIndex = 0;
						foreach (int idx in indices)
						{
							if (idx > maxIndex) maxIndex = idx;
						}
						meshVertexCount = maxIndex + 1;
					}
					modelVertexCount += meshVertexCount;
				}
				meshStartsForModels.Add(meshStarts);
				bodyPartVertexCount += modelVertexCount;
			}
			offsets.ModelStart.Add(modelStarts);
			offsets.MeshStart.Add(meshStartsForModels);
			globalVertexCount += bodyPartVertexCount;
		}

		Log.Info($"[tf2 mdl] Computed vertex offsets: {offsets.BodyPartStart.Count} bodyparts, total vertices={globalVertexCount}");
		return offsets;
	}

	// Create an empty mesh for bodygroup toggling (based on GMod implementation)
	private static Mesh CreateEmptyMesh(string name)
	{
		var material = Material.Create($"tf2_empty_{name}", "simple_color");
		material?.Set("Color", Texture.White);
		
		var mesh = new Mesh(name, material);
		
		// Create minimal vertex data (single degenerate triangle)
		var verts = new List<SourceSkinnedVertex>
		{
			new SourceSkinnedVertex(
				Vector3.Zero,           // position
				Vector3.Up,            // normal
				new Vector3(1, 0, 0),  // tangent
				Vector2.Zero,          // uv
				new Color32(0, 0, 0, 0), // bone indices
				new Color32(255, 0, 0, 0) // bone weights
			)
		};
		
		// Create degenerate triangle (all same vertex = invisible)
		var indices = new int[] { 0, 0, 0 };
		
		mesh.CreateVertexBuffer(verts.Count, SourceSkinnedVertex.Layout, verts);
		mesh.CreateIndexBuffer(indices.Length, indices);
		mesh.Bounds = BBox.FromPositionAndSize(Vector3.Zero, new Vector3(0.0001f, 0.0001f, 0.0001f));
		
		return mesh;
	}

	// Build skeleton from MDL file (based on GMod implementation)
	private void BuildSkeletonFromMdl(ModelBuilder builder, Stream mdlStream, string modelName)
	{
		using var br = new BinaryReader(mdlStream, System.Text.Encoding.ASCII, leaveOpen: true);
		mdlStream.Seek(0, SeekOrigin.Begin);

		// Header
		var id = new string(br.ReadChars(4));
		if (id != "IDST")
		{
			Log.Warning($"[tf2 mdl] Invalid MDL ID '{id}' in {modelName}");
			return;
		}

		int version = br.ReadInt32();
		br.ReadInt32(); // checksum
		br.ReadBytes(64); // name
		br.ReadInt32(); // length
		br.ReadBytes(sizeof(float) * 18); // eye/illum/hull/view vectors
		br.ReadInt32(); // flags
		int numbones = br.ReadInt32();
		int boneindex = br.ReadInt32();

		// Skip to bone section
		if (numbones <= 0 || boneindex <= 0)
		{
			Log.Info($"[tf2 mdl] No bones found in {modelName} (count={numbones}, index={boneindex})");
			return;
		}

		Log.Info($"[tf2 mdl] Reading {numbones} bones from {modelName}");

		// First pass: read raw bone data (mstudiobone_t ~216 bytes each)
		var boneNames = new List<string>(numbones);
		var parentIndex = new List<int>(numbones);
		var localPos = new List<System.Numerics.Vector3>(numbones);
		var localQuat = new List<System.Numerics.Quaternion>(numbones);

		for (int b = 0; b < numbones; b++)
		{
			long bonePos = boneindex + b * 216;
			mdlStream.Seek(bonePos, SeekOrigin.Begin);
			
			int nameOffset = br.ReadInt32();
			int parent = br.ReadInt32();
			
			// Skip bonecontroller[6]
			for (int i = 0; i < 6; i++) br.ReadInt32();
			
			// pos (Vector) - MDL local (parent-relative)
			float posX = br.ReadSingle();
			float posY = br.ReadSingle();
			float posZ = br.ReadSingle();
			
			// quat (Quaternion) - MDL local (parent-relative)
			float qx = br.ReadSingle();
			float qy = br.ReadSingle();
			float qz = br.ReadSingle();
			float qw = br.ReadSingle();
			
			// Skip rest of bone data (rot, scales, poseToBone matrix, etc.)
			
			// Read bone name
			string name = ReadCString(mdlStream, br, bonePos + nameOffset);
			if (string.IsNullOrWhiteSpace(name)) name = $"bone_{b}";

			boneNames.Add(name);
			parentIndex.Add(parent);
			localPos.Add(new System.Numerics.Vector3(posX, posY, posZ));
			localQuat.Add(new System.Numerics.Quaternion(qx, qy, qz, qw));
		}

		// Second pass: compute world transforms by walking parent chain
		var worldPos = new Vector3[numbones];
		var worldRot = new Rotation[numbones];
		
		for (int i = 0; i < numbones; i++)
		{
			var lpos = new Vector3(localPos[i].X, localPos[i].Y, localPos[i].Z);
			var lq = localQuat[i];
			var lrot = new Rotation(lq.X, lq.Y, lq.Z, lq.W);
			
			int p = parentIndex[i];
			if (p < 0 || p >= i) // Root bone or invalid parent
			{
				worldRot[i] = lrot;
				worldPos[i] = lpos;
			}
			else
			{
				var pr = worldRot[p];
				worldRot[i] = pr * lrot;
				worldPos[i] = worldPos[p] + pr * lpos;
			}

			// Add bone to model with world transform
			string parentName = (p >= 0 && p < numbones) ? boneNames[p] : null;
			builder.AddBone(boneNames[i], worldPos[i], worldRot[i], parentName);
		}

		// Log skeleton summary
		int roots = 0;
		var rootNames = new List<string>();
		for (int i = 0; i < numbones; i++)
		{
			if (parentIndex[i] < 0)
			{
				roots++;
				if (rootNames.Count < 3) rootNames.Add(boneNames[i]);
			}
		}
		Log.Info($"[tf2 mdl] Skeleton built: {numbones} bones, {roots} roots. Root bones: {string.Join(", ", rootNames)}");
	}

	// Read null-terminated string from stream at given offset
	private static string ReadCString(Stream stream, BinaryReader br, long offset)
	{
		stream.Seek(offset, SeekOrigin.Begin);
		var chars = new List<char>();
		
		while (true)
		{
			byte b = br.ReadByte();
			if (b == 0) break;
			chars.Add((char)b);
		}
		
		return new string(chars.ToArray());
	}

	// Create materials for the model by reading MDL materials and loading VTF textures (based on GMod)
	private (Dictionary<string, Material> materialDict, List<string> materialNames) CreateMaterialsForModel(Stream mdlStream, string modelName)
	{
		var materialDict = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
		var materialNames = new List<string>();
		
		try
		{
			materialNames = ReadMaterialNamesFromMdl(mdlStream);
			Log.Info($"[tf2 mdl] Found {materialNames.Count} materials in {modelName}");
			
			foreach (var materialName in materialNames)
			{
				try
				{
					var material = CreateMaterialFromName(materialName);
					if (material != null)
					{
						materialDict[materialName] = material;
					}
				}
				catch (Exception ex)
				{
					Log.Warning($"[tf2 mdl] Failed to create material '{materialName}': {ex.Message}");
				}
			}
			
			Log.Info($"[tf2 mdl] Successfully created {materialDict.Count} materials for {modelName}");
		}
		catch (Exception ex)
		{
			Log.Warning($"[tf2 mdl] Failed to read materials from {modelName}: {ex.Message}");
		}
		
		return (materialDict, materialNames);
	}

	private Material CreateMaterialFromName(string materialName)
	{
		// Try to find VMT file for this material - check if it contains path already
		var vmtCandidates = new List<string>();
		
		if (materialName.Contains('/') || materialName.Contains('\\'))
		{
			// Material name already has path info
			var normalizedMaterial = materialName.Replace('\\', '/');
			vmtCandidates.Add($"materials/{normalizedMaterial}.vmt");
		}
		else
		{
			// No path info - derive from model directory structure
			string modelPath = _entry.GetFullPath(); // e.g., "models/props_doomsday/sign_gameplay_doomsday01b_l.mdl"
			if (modelPath.StartsWith("models/"))
			{
				// Extract directory from model path
				var modelDir = System.IO.Path.GetDirectoryName(modelPath);
				if (!string.IsNullOrEmpty(modelDir))
				{
					modelDir = modelDir.Replace('\\', '/'); // "models/props_doomsday"
					vmtCandidates.Add($"materials/{modelDir}/{materialName}.vmt"); // "materials/models/props_doomsday/materialname.vmt"
					Log.Info($"[tf2 mdl] Material '{materialName}' has no path info - trying model directory: materials/{modelDir}/{materialName}.vmt");
				}
			}
			else
			{
				Log.Info($"[tf2 mdl] Skipping material '{materialName}' - no path info and model not in models/ directory");
				return null;
			}
		}
		
		// Try each VMT candidate
		foreach (var vmtPath in vmtCandidates)
		{
			foreach (var package in _packages)
			{
				if (package.Entries.TryGetValue("vmt", out var vmtEntries))
				{
					foreach (var entry in vmtEntries)
					{
						if (entry.GetFullPath().Equals(vmtPath, StringComparison.OrdinalIgnoreCase))
						{
							try
							{
								using var vmtStream = package.GetEntryStream(entry);
								var vmtData = TF2Vmt.Parse(vmtStream);
								
								if (vmtData != null)
								{
									// Try common texture parameter names
									string baseTexture = null;
									if (vmtData.Kv.TryGetValue("$basetexture", out baseTexture) ||
									    vmtData.Kv.TryGetValue("$basetexture2", out baseTexture) ||
									    vmtData.Kv.TryGetValue("$color", out baseTexture) ||
									    vmtData.Kv.TryGetValue("$diffuse", out baseTexture))
									{
										// Load the VTF texture
										var texture = LoadVtfTexture(baseTexture);
										if (texture != null)
										{
											// Create material with texture (like Quake)
											var matName = $"tf2_model_{materialName.Replace('/', '_').Replace('\\', '_')}";
											var material = Material.Create(matName, "simple_color");
											material?.Set("Color", texture);
											return material;
										}
									}
								}
							}
							catch (Exception ex)
							{
								Log.Warning($"[tf2 mdl] Failed to parse VMT '{vmtPath}': {ex.Message}");
							}
							break;
						}
					}
				}
			}
		}
		
		return null; // No VMT found or couldn't create material
	}

	private Texture LoadVtfTexture(string texturePath)
	{
		try
		{
			// Normalize path separators first
			var normalizedTexturePath = texturePath.Replace('\\', '/');
			
			// Add .vtf extension if not present
			var vtfPath = normalizedTexturePath.EndsWith(".vtf", StringComparison.OrdinalIgnoreCase) 
				? $"materials/{normalizedTexturePath}" 
				: $"materials/{normalizedTexturePath}.vtf";
			
			// Find VTF file in packages
			foreach (var package in _packages)
			{
				if (package.Entries.TryGetValue("vtf", out var vtfEntries))
				{
					foreach (var entry in vtfEntries)
					{
						if (entry.GetFullPath().Equals(vtfPath, StringComparison.OrdinalIgnoreCase))
						{
							using var vtfStream = package.GetEntryStream(entry);
							return TF2Vtf.LoadFromStream(vtfStream, vtfPath);
						}
					}
				}
			}
			
			Log.Warning($"[tf2 mdl] VTF texture not found: {vtfPath}");
		}
		catch (Exception ex)
		{
			Log.Warning($"[tf2 mdl] Failed to load VTF texture '{texturePath}': {ex.Message}");
		}
		
		return null; // Return null instead of white fallback so material creation fails properly
	}



	// Read material/texture names from MDL file (simplified version)
	private List<string> ReadMaterialNamesFromMdl(Stream mdlStream)
	{
		var materials = new List<string>();
		
		using var br = new BinaryReader(mdlStream, System.Text.Encoding.ASCII, leaveOpen: true);
		mdlStream.Seek(0, SeekOrigin.Begin);

		// Header validation
		var id = new string(br.ReadChars(4));
		if (id != "IDST") return materials;

		int version = br.ReadInt32();
		br.ReadInt32(); // checksum
		br.ReadBytes(64); // name
		br.ReadInt32(); // length
		br.ReadBytes(sizeof(float) * 18); // eye/illum/hull/view vectors
		br.ReadInt32(); // flags
		br.ReadInt32(); // numbones
		br.ReadInt32(); // boneindex

		// Read header fields exactly like GMod (matching their structure)
		br.ReadInt32(); // numbonecontrollers
		br.ReadInt32(); // bonecontrollerindex
		br.ReadInt32(); // numhitboxsets
		br.ReadInt32(); // hitboxsetindex
		br.ReadInt32(); // numlocalanim
		br.ReadInt32(); // localanimindex
		br.ReadInt32(); // numlocalseq
		br.ReadInt32(); // localseqindex
		br.ReadInt32(); // activitylistversion
		br.ReadInt32(); // eventsindexed

		int textureCount = br.ReadInt32(); // numtextures
		int textureIndex = br.ReadInt32(); // textureindex

		// Read texture/material names
		if (textureCount > 0 && textureIndex > 0)
		{
			for (int i = 0; i < textureCount; i++)
			{
				try
				{
					long texturePos = textureIndex + i * 64; // mstudiotexture_t ~64 bytes
					if (texturePos < 0 || texturePos + 8 >= mdlStream.Length)
					{
						Log.Warning($"[tf2 mdl] Invalid texture position {i}: {texturePos}");
						break;
					}
					
					mdlStream.Seek(texturePos, SeekOrigin.Begin);
					
					int nameOffset = br.ReadInt32();
					int flags = br.ReadInt32();

					long namePos = texturePos + nameOffset;
					if (namePos < 0 || namePos >= mdlStream.Length)
					{
						Log.Warning($"[tf2 mdl] Invalid texture name position {i}: {namePos}");
						continue;
					}

					// Read texture name
					string texName = ReadCString(mdlStream, br, namePos);
					if (!string.IsNullOrWhiteSpace(texName))
					{
						materials.Add(texName);
					}
				}
				catch (Exception ex)
				{
					Log.Warning($"[tf2 mdl] Error reading texture {i}: {ex.Message}");
					break;
				}
			}
		}

		return materials;
	}

	// Read material index directly from MDL mesh structure (based on GMod's TryReadMeshMaterialSkinRef)
	private int ReadMeshMaterialIndexFromMdl(Stream mdlStream, int bodyPartIndex, int modelIndex, int meshIndex)
	{
		try
		{
			using var br = new BinaryReader(mdlStream, System.Text.Encoding.ASCII, leaveOpen: true);
			mdlStream.Seek(0, SeekOrigin.Begin);

			// Read MDL header to get bodypart info
			br.ReadBytes(4 + 4 + 4); // id, version, checksum
			br.ReadBytes(64); // name
			br.ReadInt32(); // length
			br.ReadBytes(sizeof(float) * 18); // eye/illum/hull/view vectors
			br.ReadInt32(); // flags
			br.ReadInt32(); // numbones
			br.ReadInt32(); // boneindex

			// Skip intermediate fields to get to bodyparts (matching GMod exactly)
			for (int i = 0; i < 17; i++) br.ReadInt32();

			int numBodyParts = br.ReadInt32();
			int bodyPartOffset = br.ReadInt32();

			if (bodyPartIndex < 0 || bodyPartIndex >= numBodyParts || bodyPartOffset <= 0) 
				return -1;

			// Navigate to bodypart
			long bodyPartPos = bodyPartOffset + bodyPartIndex * 16; // 4 ints per bodypart
			mdlStream.Seek(bodyPartPos, SeekOrigin.Begin);
			br.ReadInt32(); // name index
			int numModels = br.ReadInt32();
			br.ReadInt32(); // base
			int modelOffset = br.ReadInt32();

			if (modelIndex < 0 || modelIndex >= numModels || modelOffset <= 0) 
				return -1;

			// Navigate to model - try different strides like GMod does
			int[] candidateStrides = new[] { 148, 140, 144, 152 }; // Common v48 model stride sizes
			
			foreach (int stride in candidateStrides)
			{
				try
				{
					long modelPos = bodyPartPos + modelOffset + modelIndex * stride;
					if (modelPos < 0 || modelPos + stride >= mdlStream.Length) 
						continue;

					mdlStream.Seek(modelPos, SeekOrigin.Begin);
					br.ReadBytes(64); // model name
					br.ReadInt32(); // type
					br.ReadSingle(); // radius
					int meshCount = br.ReadInt32();
					int meshOffset = br.ReadInt32();

					if (meshIndex < 0 || meshIndex >= meshCount || meshOffset <= 0) 
						continue;

					// Try different mesh stride sizes
					int[] meshStrides = new[] { 116, 108, 112, 120 }; // Common mesh structure sizes
					
					foreach (int meshStride in meshStrides)
					{
						try
						{
							long meshPos = modelPos + meshOffset + meshIndex * meshStride;
							if (meshPos < 0 || meshPos + 16 >= mdlStream.Length) 
								continue;

							mdlStream.Seek(meshPos, SeekOrigin.Begin);
							int materialIndex = br.ReadInt32(); // First field in mesh structure is material index
							
							// Basic validation - material index should be reasonable
							if (materialIndex >= 0 && materialIndex < 1000) // sanity check
							{
								return materialIndex;
							}
						}
						catch { }
					}
				}
				catch { }
			}
		}
		catch { }

		return -1; // Failed to read
	}


}
