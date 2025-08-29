using System;
using System.Collections.Generic;
using IO = System.IO;
using Sandbox;

internal static class SourceVtx
{
    public sealed class Header
    {
        public int Version;
        public int VertexCacheSize;
        public ushort MaxBonesPerStrip;
        public ushort MaxBonesPerTri;
        public int MaxBonesPerVertex;
        public int Checksum;
        public int LodCount;
        public int MaterialReplacementListOffset;
        public int BodyPartCount;
        public int BodyPartOffset;
    }

    public sealed class Summary
    {
        public Header Header = new();
        public int BodyParts;
        public int Models;
        public int Lods;
        public int Meshes;
        public int StripGroups;
        public int Strips;
        public int VtxVertices;
        public int VtxIndices;
        public int TriangleCount;
    }

    public sealed class LodInfo { public int MeshCount; }
    public sealed class ModelInfo { public List<LodInfo> Lods = new(); }
    public sealed class BodyPartInfo { public List<ModelInfo> Models = new(); }
    public sealed class Hierarchy { public List<BodyPartInfo> BodyParts = new(); }

    public static Summary Parse(IO.Stream stream)
    {
        using var br = new IO.BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);
        var s = new Summary();

        // Bounds check for header
        if (stream.Length < 40) // VTX header is at least 40 bytes
        {
            Log.Warning($"[tf2 vtx] Stream too small for VTX header ({stream.Length} bytes)");
            return s;
        }

        stream.Seek(0, IO.SeekOrigin.Begin);
        // Header
        s.Header.Version = br.ReadInt32();
        s.Header.VertexCacheSize = br.ReadInt32();
        s.Header.MaxBonesPerStrip = br.ReadUInt16();
        s.Header.MaxBonesPerTri = br.ReadUInt16();
        s.Header.MaxBonesPerVertex = br.ReadInt32();
        s.Header.Checksum = br.ReadInt32();
        s.Header.LodCount = br.ReadInt32();
        s.Header.MaterialReplacementListOffset = br.ReadInt32();
        s.Header.BodyPartCount = br.ReadInt32();
        s.Header.BodyPartOffset = br.ReadInt32();

        // Walk bodyparts/models/lods/meshes/stripgroups to accumulate counts
        if (s.Header.BodyPartCount > 0 && s.Header.BodyPartOffset > 0)
        {
            long bodyPartsArray = s.Header.BodyPartOffset;
            
            // Bounds check for bodyparts array
            if (bodyPartsArray + (s.Header.BodyPartCount * 8) > stream.Length)
            {
                Log.Warning($"[tf2 vtx] BodyParts array extends beyond stream (pos={bodyPartsArray}, count={s.Header.BodyPartCount}, len={stream.Length})");
                return s;
            }

            s.BodyParts = s.Header.BodyPartCount;
            for (int bp = 0; bp < s.Header.BodyPartCount; bp++)
            {
                long bodyPartPos = bodyPartsArray + bp * 8; // int modelCount, int modelOffset
                stream.Seek(bodyPartPos, IO.SeekOrigin.Begin);
                int modelCount = br.ReadInt32();
                int modelOffset = br.ReadInt32();
                s.Models += Math.Max(0, modelCount);

                if (modelCount > 0 && modelOffset > 0)
                {
                    // Bounds check for models array
                    long modelsArray = bodyPartPos + modelOffset;
                    if (modelsArray + (modelCount * 8) > stream.Length)
                    {
                        Log.Warning($"[tf2 vtx] Models array extends beyond stream (pos={modelsArray}, count={modelCount}, len={stream.Length})");
                        continue;
                    }
                    for (int m = 0; m < modelCount; m++)
                    {
                        long modelPos = modelsArray + m * 8; // int lodCount, int lodOffset
                        stream.Seek(modelPos, IO.SeekOrigin.Begin);
                        int lodCount = br.ReadInt32();
                        int lodOffset = br.ReadInt32();
                        s.Lods += Math.Max(0, lodCount);
                        if (lodCount > 0 && lodOffset > 0)
                        {
                            // Bounds check for LODs array
                            long lodsArray = modelPos + lodOffset;
                            if (lodsArray + (lodCount * 12) > stream.Length)
                            {
                                Log.Warning($"[tf2 vtx] LODs array extends beyond stream (pos={lodsArray}, count={lodCount}, len={stream.Length})");
                                continue;
                            }

                            for (int l = 0; l < lodCount; l++)
                            {
                                long lodPos = lodsArray + l * 12; // int meshCount, int meshOffset, float switchPoint
                                stream.Seek(lodPos, IO.SeekOrigin.Begin);
                                int meshCount = br.ReadInt32();
                                int meshOffset = br.ReadInt32();
                                br.ReadSingle(); // switchPoint
                                s.Meshes += Math.Max(0, meshCount);
                                if (meshCount > 0 && meshOffset > 0)
                                {
                                    // Bounds check for meshes array
                                    long meshesArray = lodPos + meshOffset;
                                    if (meshesArray + (meshCount * 9) > stream.Length)
                                    {
                                        Log.Warning($"[tf2 vtx] Meshes array extends beyond stream (pos={meshesArray}, count={meshCount}, len={stream.Length})");
                                        continue;
                                    }

                                    for (int me = 0; me < meshCount; me++)
                                    {
                                        long meshPos = meshesArray + me * 9; // int stripGroupCount, int stripGroupOffset, byte flags
                                        stream.Seek(meshPos, IO.SeekOrigin.Begin);
                                        int stripGroupCount = br.ReadInt32();
                                        int stripGroupOffset = br.ReadInt32();
                                        br.ReadByte(); // flags
                                        s.StripGroups += Math.Max(0, stripGroupCount);
                                        if (stripGroupCount > 0 && stripGroupOffset > 0)
                                        {
                                            // Bounds check for strip groups array
                                            long stripGroupsArray = meshPos + stripGroupOffset;
                                            if (stripGroupsArray + (stripGroupCount * 25) > stream.Length)
                                            {
                                                Log.Warning($"[tf2 vtx] StripGroups array extends beyond stream (pos={stripGroupsArray}, count={stripGroupCount}, len={stream.Length})");
                                                continue;
                                            }
                                            for (int sg = 0; sg < stripGroupCount; sg++)
                                            {
                                                long sgPos = stripGroupsArray + sg * 25; // vertexCount, vertexOffset, indexCount, indexOffset, stripCount, stripOffset, flags
                                                stream.Seek(sgPos, IO.SeekOrigin.Begin);
                                                int vertexCount = br.ReadInt32();
                                                int vertexOffset = br.ReadInt32();
                                                int indexCount = br.ReadInt32();
                                                int indexOffset = br.ReadInt32();
                                                int stripCount = br.ReadInt32();
                                                int stripOffset = br.ReadInt32();
                                                br.ReadByte(); // flags

                                                s.VtxVertices += Math.Max(0, vertexCount);
                                                s.VtxIndices += Math.Max(0, indexCount);
                                                s.Strips += Math.Max(0, stripCount);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        s.TriangleCount = s.VtxIndices / 3;
        Log.Info($"[tf2 vtx] ver={s.Header.Version} bodyparts={s.BodyParts} models={s.Models} lods={s.Lods} meshes={s.Meshes} stripgroups={s.StripGroups} idx={s.VtxIndices} (~{s.TriangleCount} tris)");
        return s;
    }

    public static Hierarchy ParseHierarchy(IO.Stream stream)
    {
        using var br = new IO.BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);
        var h = new Hierarchy();

        // Bounds check for header
        if (stream.Length < 40) // VTX header is at least 40 bytes
        {
            Log.Warning($"[tf2 vtx] Stream too small for VTX header ({stream.Length} bytes)");
            return h;
        }

        stream.Seek(0, IO.SeekOrigin.Begin);
        int version = br.ReadInt32();
        int vertexCacheSize = br.ReadInt32();
        ushort maxBonesPerStrip = br.ReadUInt16();
        ushort maxBonesPerTri = br.ReadUInt16();
        int maxBonesPerVertex = br.ReadInt32();
        int checksum = br.ReadInt32();
        int lodCount = br.ReadInt32();
        int matReplOffset = br.ReadInt32();
        int bodyPartCount = br.ReadInt32();
        int bodyPartOffset = br.ReadInt32();

        if (bodyPartCount <= 0 || bodyPartOffset <= 0)
            return h;

        // Bounds check for bodyparts array
        long bodyPartsArray = bodyPartOffset;
        if (bodyPartsArray + (bodyPartCount * 8) > stream.Length)
        {
            Log.Warning($"[tf2 vtx] BodyParts array extends beyond stream (pos={bodyPartsArray}, count={bodyPartCount}, len={stream.Length})");
            return h;
        }

        for (int bp = 0; bp < bodyPartCount; bp++)
        {
            long bodyPartPos = bodyPartsArray + bp * 8;
            stream.Seek(bodyPartPos, IO.SeekOrigin.Begin);
            int modelCount = br.ReadInt32();
            int modelOffset = br.ReadInt32();

            var bpInfo = new BodyPartInfo();
            if (modelCount > 0 && modelOffset > 0)
            {
                // Bounds check for models array
                long modelsArray = bodyPartPos + modelOffset;
                if (modelsArray + (modelCount * 8) > stream.Length)
                {
                    Log.Warning($"[tf2 vtx] Models array extends beyond stream (pos={modelsArray}, count={modelCount}, len={stream.Length})");
                    h.BodyParts.Add(bpInfo);
                    continue;
                }

                for (int m = 0; m < modelCount; m++)
                {
                    long modelPos = modelsArray + m * 8;
                    stream.Seek(modelPos, IO.SeekOrigin.Begin);
                    int lodCountM = br.ReadInt32();
                    int lodOffset = br.ReadInt32();

                    var modelInfo = new ModelInfo();
                    if (lodCountM > 0 && lodOffset > 0)
                    {
                        // Bounds check for LODs array
                        long lodsArray = modelPos + lodOffset;
                        if (lodsArray + (lodCountM * 12) > stream.Length)
                        {
                            Log.Warning($"[tf2 vtx] LODs array extends beyond stream (pos={lodsArray}, count={lodCountM}, len={stream.Length})");
                            bpInfo.Models.Add(modelInfo);
                            continue;
                        }

                        for (int l = 0; l < lodCountM; l++)
                        {
                            long lodPos = lodsArray + l * 12;
                            stream.Seek(lodPos, IO.SeekOrigin.Begin);
                            int meshCount = br.ReadInt32();
                            int meshOffset = br.ReadInt32();
                            br.ReadSingle();
                            modelInfo.Lods.Add(new LodInfo { MeshCount = meshCount });
                        }
                    }
                    bpInfo.Models.Add(modelInfo);
                }
            }
            h.BodyParts.Add(bpInfo);
        }
        return h;
    }

    // Returns originalMeshVertexIndex for each referenced vertex (triangle order) for LOD0 of specific mesh
    public static List<int> ReadLod0MeshOriginalIndices(IO.Stream stream, int bodyPartIndex, int modelIndex, int meshIndex)
    {
        var result = new List<int>();
        using var br = new IO.BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);

        // Bounds check for header
        if (stream.Length < 40) // VTX header is at least 40 bytes
        {
            Log.Warning($"[tf2 vtx] Stream too small for VTX header ({stream.Length} bytes)");
            return result;
        }

        stream.Seek(0, IO.SeekOrigin.Begin);
        int version = br.ReadInt32();
        br.ReadInt32(); // vertexCacheSize
        br.ReadUInt16(); // maxBonesPerStrip
        br.ReadUInt16(); // maxBonesPerTri
        br.ReadInt32(); // maxBonesPerVertex
        br.ReadInt32(); // checksum
        br.ReadInt32(); // lodCount
        br.ReadInt32(); // mat repl offset
        int bodyPartCount = br.ReadInt32();
        int bodyPartOffset = br.ReadInt32();

        if (bodyPartIndex < 0 || bodyPartIndex >= bodyPartCount) return result;
        
        // Bounds check for bodypart position
        long bodyPartPos = bodyPartOffset + bodyPartIndex * 8;
        if (bodyPartPos + 8 > stream.Length)
        {
            Log.Warning($"[tf2 vtx] BodyPart {bodyPartIndex} extends beyond stream (pos={bodyPartPos}, len={stream.Length})");
            return result;
        }
        
        stream.Seek(bodyPartPos, IO.SeekOrigin.Begin);
        int modelCount = br.ReadInt32();
        int modelOffset = br.ReadInt32();
        if (modelIndex < 0 || modelIndex >= modelCount || modelOffset <= 0) return result;

        // Bounds check for model position
        long modelsArray = bodyPartPos + modelOffset;
        long modelPos = modelsArray + modelIndex * 8;
        if (modelPos + 8 > stream.Length)
        {
            Log.Warning($"[tf2 vtx] Model {modelIndex} extends beyond stream (pos={modelPos}, len={stream.Length})");
            return result;
        }
        
        stream.Seek(modelPos, IO.SeekOrigin.Begin);
        int lodCount = br.ReadInt32();
        int lodOffset = br.ReadInt32();
        if (lodCount <= 0 || lodOffset <= 0) return result;

        // Bounds check for LOD position
        long lodsArray = modelPos + lodOffset;
        long lodPos = lodsArray + 0 * 12; // LOD0
        if (lodPos + 12 > stream.Length)
        {
            Log.Warning($"[tf2 vtx] LOD0 extends beyond stream (pos={lodPos}, len={stream.Length})");
            return result;
        }
        
        stream.Seek(lodPos, IO.SeekOrigin.Begin);
        int meshCount = br.ReadInt32();
        int meshOffset = br.ReadInt32();
        br.ReadSingle(); // switchPoint
        if (meshIndex < 0 || meshIndex >= meshCount || meshOffset <= 0) return result;

        // Bounds check for mesh position
        long meshesArray = lodPos + meshOffset;
        long meshPos = meshesArray + meshIndex * 9;
        if (meshPos + 9 > stream.Length)
        {
            Log.Warning($"[tf2 vtx] Mesh {meshIndex} extends beyond stream (pos={meshPos}, len={stream.Length})");
            return result;
        }
        
        stream.Seek(meshPos, IO.SeekOrigin.Begin);
        int stripGroupCount = br.ReadInt32();
        int stripGroupOffset = br.ReadInt32();
        br.ReadByte(); // flags
        if (stripGroupCount <= 0 || stripGroupOffset <= 0) return result;

        // Bounds check for stripgroup array position
        long stripGroupsArray = meshPos + stripGroupOffset;
        if (stripGroupsArray + (stripGroupCount * 25) > stream.Length)
        {
            Log.Warning($"[tf2 vtx] StripGroup array extends beyond stream (pos={stripGroupsArray}, count={stripGroupCount}, len={stream.Length})");
            return result;
        }
        for (int sg = 0; sg < stripGroupCount; sg++)
        {
            long sgPos = stripGroupsArray + sg * 25;
            
            // Bounds check for stripgroup header
            if (sgPos + 25 > stream.Length)
            {
                Log.Warning($"[tf2 vtx] StripGroup {sg} header extends beyond stream (pos={sgPos}, len={stream.Length})");
                break;
            }
            
            stream.Seek(sgPos, IO.SeekOrigin.Begin);
            int vertexCount = br.ReadInt32();
            int vertexOffset = br.ReadInt32();
            int indexCount = br.ReadInt32();
            int indexOffset = br.ReadInt32();
            int stripCount = br.ReadInt32();
            int stripOffset = br.ReadInt32();
            br.ReadByte(); // flags

            // Read stripgroup vertices (to map local vtx vertex index to originalMeshVertexIndex)
            var localOrig = new int[Math.Max(0, vertexCount)];
            if (vertexCount > 0 && vertexOffset > 0)
            {
                long vtxVertsPos = sgPos + vertexOffset;
                long vtxVertsEnd = vtxVertsPos + (vertexCount * 9); // Each vertex is 9 bytes
                
                // Bounds check for vertex data
                if (vtxVertsEnd > stream.Length)
                {
                    Log.Warning($"[tf2 vtx] Vertex data extends beyond stream (end={vtxVertsEnd}, len={stream.Length})");
                    continue;
                }
                
                stream.Seek(vtxVertsPos, IO.SeekOrigin.Begin);
                for (int v = 0; v < vertexCount; v++)
                {
                    // struct: byte boneWeightIndex[3], byte boneCount, ushort originalMeshVertexIndex, byte boneId[3]
                    br.ReadBytes(3); // boneWeightIndex
                    br.ReadByte(); // boneCount
                    ushort orig = br.ReadUInt16();
                    localOrig[v] = orig;
                    br.ReadBytes(3); // boneId
                }
            }
            // Read indices and map through localOrig
            if (indexCount > 0 && indexOffset > 0)
            {
                long idxPos = sgPos + indexOffset;
                long idxEnd = idxPos + (indexCount * 2); // Each index is 2 bytes (ushort)
                
                // Bounds check for index data
                if (idxEnd > stream.Length)
                {
                    Log.Warning($"[tf2 vtx] Index data extends beyond stream (end={idxEnd}, len={stream.Length})");
                    continue;
                }
                
                stream.Seek(idxPos, IO.SeekOrigin.Begin);
                for (int i = 0; i < indexCount; i++)
                {
                    ushort local = br.ReadUInt16();
                    int orig = (local >= 0 && local < localOrig.Length) ? localOrig[local] : 0;
                    result.Add(orig);
                }
            }
        }

        return result;
    }

    // Returns the material index for a given mesh at LOD0
    public static int ReadLod0MeshMaterialIndex(IO.Stream stream, int bodyPartIndex, int modelIndex, int meshIndex)
    {
        try
        {
            // Ensure stream starts at beginning
            stream.Seek(0, IO.SeekOrigin.Begin);
            using var br = new IO.BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);

            int version = br.ReadInt32();
            br.ReadInt32(); // vertexCacheSize
            br.ReadUInt16(); // maxBonesPerStrip
            br.ReadUInt16(); // maxBonesPerTri
            br.ReadInt32(); // maxBonesPerVertex
            br.ReadInt32(); // checksum
            br.ReadInt32(); // lodCount
            br.ReadInt32(); // mat repl offset
            int bodyPartCount = br.ReadInt32();
            int bodyPartOffset = br.ReadInt32();

            if (bodyPartIndex < 0 || bodyPartIndex >= bodyPartCount) return -1;
            long bodyPartPos = bodyPartOffset + bodyPartIndex * 8;
            stream.Seek(bodyPartPos, IO.SeekOrigin.Begin);
            int modelCount = br.ReadInt32();
            int modelOffset = br.ReadInt32();
            if (modelIndex < 0 || modelIndex >= modelCount || modelOffset <= 0) return -1;

            long modelsArray = bodyPartPos + modelOffset;
            long modelPos = modelsArray + modelIndex * 8;
            stream.Seek(modelPos, IO.SeekOrigin.Begin);
            int lodCount = br.ReadInt32();
            int lodOffset = br.ReadInt32();
            if (lodCount <= 0 || lodOffset <= 0) return -1;

            long lodsArray = modelPos + lodOffset;
            long lodPos = lodsArray + 0 * 12; // LOD0
            stream.Seek(lodPos, IO.SeekOrigin.Begin);
            int meshCount = br.ReadInt32();
            int meshOffset = br.ReadInt32();
            br.ReadSingle(); // switchPoint
            if (meshIndex < 0 || meshIndex >= meshCount || meshOffset <= 0) return -1;

            long meshesArray = lodPos + meshOffset;
            long meshPos = meshesArray + meshIndex * 9;
            stream.Seek(meshPos, IO.SeekOrigin.Begin);
            int material = br.ReadInt32();
            return material;
        }
        catch { return -1; }
    }
}
