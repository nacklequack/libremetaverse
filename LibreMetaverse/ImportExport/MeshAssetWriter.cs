/*
 * Copyright (c) 2026, Sjofn LLC.
 * All rights reserved.
 *
 * - Redistribution and use in source and binary forms, with or without
 *   modification, are permitted provided that the following conditions are met:
 *
 * - Redistributions of source code must retain the above copyright notice, this
 *   list of conditions and the following disclaimer.
 * - Neither the name of the openmetaverse.co nor the names
 *   of its contributors may be used to endorse or promote products derived from
 *   this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
 * AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
 * IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
 * ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE
 * LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
 * CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
 * SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
 * INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
 * CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
 * ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
 * POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenMetaverse.StructuredData;

namespace OpenMetaverse.ImportExport
{
    /// <summary>
    /// Writes a Second Life mesh asset with all four levels of detail, an optional
    /// skin block and a physics shape, following the layout the official viewer
    /// writes (LLModel::writeModel). See
    /// http://wiki.secondlife.com/wiki/Mesh/Mesh_Asset_Format
    /// </summary>
    /// <remarks>
    /// Face positions are given in object space (metres, Z up). Like the viewer, the
    /// writer normalizes the geometry into the unit cube using the high level of
    /// detail's bounds, and records that scale in the prim's
    /// <see cref="ModelPrim.Scale"/> and, for a rigged mesh, in the bind shape matrix.
    /// Every level of detail and the physics shape share that one normalization, so a
    /// lower level of detail with smaller bounds is not stretched to fit.
    /// </remarks>
    public static class MeshAssetWriter
    {
        /// <summary>Most faces a single mesh prim may have</summary>
        public const int MaxFaces = 8;

        static readonly string[] LodNames = { "lowest_lod", "low_lod", "medium_lod", "high_lod" };

        /// <summary>
        /// Encodes <paramref name="prim"/> into <see cref="ModelPrim.Asset"/>. Sets
        /// <see cref="ModelPrim.Scale"/> to the high level of detail's size, moves
        /// <see cref="ModelPrim.Position"/> to its centre, and sets
        /// <see cref="ModelPrim.PhysicsShapeType"/> to match the physics written.
        /// </summary>
        /// <param name="prim">Prim to encode. <see cref="ModelPrim.Faces"/> is the high level of detail.</param>
        /// <returns>The encoded asset</returns>
        public static byte[] Encode(ModelPrim prim)
        {
            if (prim.Faces.Count == 0)
                throw new ArgumentException("The high level of detail has no faces.", nameof(prim));
            if (prim.Faces.Count > MaxFaces)
                throw new ArgumentException(
                    $"A mesh prim has at most {MaxFaces} faces; this one has {prim.Faces.Count}.", nameof(prim));

            var skin = prim.Skin;
            if (skin != null)
                ValidateSkin(skin);

            // Normalization from the high level of detail's bounds (LLModel::normalizeVolumeFaces)
            GetBounds(prim.Faces, out var min, out var max);
            var size = max - min;
            if (size.X <= 0f) size.X = 1f;
            if (size.Y <= 0f) size.Y = 1f;
            if (size.Z <= 0f) size.Z = 1f;
            var center = (min + max) * 0.5f;

            var mdl = new OSDMap();

            if (skin != null)
            {
                // The viewer folds the normalization into the bind shape matrix: a vertex is
                // first scaled back to object space and moved to its centre, then bound.
                var denormalize = Matrix4.CreateScale(size) * Matrix4.CreateTranslation(center);
                mdl["skin"] = SkinToOSD(skin, denormalize * skin.BindShapeMatrix);
            }

            mdl["physics_convex"] = BoundingBoxHull(prim.Faces, center, size);

            // Missing levels repeat the next more detailed level, so every level is present
            var levels = new List<ModelFace>[4];
            levels[3] = prim.Faces;
            levels[2] = prim.MediumLodFaces ?? levels[3];
            levels[1] = prim.LowLodFaces ?? levels[2];
            levels[0] = prim.LowestLodFaces ?? levels[1];

            for (int i = 0; i < 4; i++)
            {
                var aligned = AlignToHigh(prim.Faces, levels[i], LodNames[i]);
                mdl[LodNames[i]] = LodToOSD(aligned, center, size, skin);
            }

            if (prim.PhysicsFaces != null && prim.PhysicsFaces.Count > 0)
            {
                mdl["physics_mesh"] = LodToOSD(prim.PhysicsFaces, center, size, null);
                prim.PhysicsShapeType = 0; // prim
            }
            else
            {
                prim.PhysicsShapeType = 2; // convex hull
            }

            prim.Asset = WriteModelToStream(mdl);
            prim.Scale = size;
            prim.Position += center * prim.Rotation;
            return prim.Asset;
        }

        static void ValidateSkin(ModelSkin skin)
        {
            if (skin.JointNames.Count == 0)
                throw new ArgumentException("A skin needs at least one joint.");
            if (skin.JointNames.Count > 255)
                throw new ArgumentException("A skin has at most 255 joints.");
            if (skin.InverseBindMatrices.Count != skin.JointNames.Count)
                throw new ArgumentException("A skin needs one inverse bind matrix per joint.");
            if (skin.AlternateInverseBindMatrices != null &&
                skin.AlternateInverseBindMatrices.Count != skin.JointNames.Count)
                throw new ArgumentException("Joint position overrides need one matrix per joint.");
        }

        static void GetBounds(IEnumerable<ModelFace> faces, out Vector3 min, out Vector3 max)
        {
            min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            foreach (var face in faces)
            {
                foreach (var v in face.Vertices)
                {
                    min = Vector3.Min(min, v.Position);
                    max = Vector3.Max(max, v.Position);
                }
            }
            if (min.X > max.X)
                throw new ArgumentException("The high level of detail has no vertices.");
        }

        /// <summary>
        /// Orders a level's faces like the high level of detail, matching by material,
        /// and fills absent materials with empty faces. Without material IDs the faces
        /// are matched by position in the list.
        /// </summary>
        static List<ModelFace?> AlignToHigh(List<ModelFace> high, List<ModelFace> lod, string lodName)
        {
            if (ReferenceEquals(high, lod))
                return lod.Cast<ModelFace?>().ToList();

            bool byMaterial = high.All(f => !string.IsNullOrEmpty(f.MaterialID));
            var aligned = new List<ModelFace?>(high.Count);
            if (byMaterial)
            {
                var byId = new Dictionary<string, ModelFace>();
                foreach (var face in lod)
                {
                    if (string.IsNullOrEmpty(face.MaterialID) || byId.ContainsKey(face.MaterialID))
                        throw new ArgumentException($"{lodName}: each face needs a distinct material ID.");
                    if (high.All(f => f.MaterialID != face.MaterialID))
                        throw new ArgumentException(
                            $"{lodName}: material '{face.MaterialID}' is not in the high level of detail.");
                    byId[face.MaterialID] = face;
                }
                foreach (var face in high)
                    aligned.Add(byId.TryGetValue(face.MaterialID, out var match) ? match : null);
            }
            else
            {
                if (lod.Count > high.Count)
                    throw new ArgumentException($"{lodName} has more faces than the high level of detail.");
                for (int i = 0; i < high.Count; i++)
                    aligned.Add(i < lod.Count ? lod[i] : null);
            }
            return aligned;
        }

        static OSDArray LodToOSD(IReadOnlyList<ModelFace?> faces, Vector3 center, Vector3 size, ModelSkin? skin)
        {
            var scale = new Vector3(1f / size.X, 1f / size.Y, 1f / size.Z);

            // Position domain spans the whole level, as the viewer writes it
            var positions = faces.Select(f => f == null || f.Vertices.Count < 3
                ? Array.Empty<Vector3>()
                : f.Vertices.Select(v => (v.Position - center) * scale).ToArray()).ToList();
            var all = positions.SelectMany(p => p).ToList();
            var minPos = all.Count > 0 ? all.Aggregate(Vector3.Min) : Vector3.Zero;
            var maxPos = all.Count > 0 ? all.Aggregate(Vector3.Max) : Vector3.Zero;
            var posRange = maxPos - minPos;

            var result = new OSDArray();
            for (int fi = 0; fi < faces.Count; fi++)
            {
                var face = faces[fi];
                if (face == null || face.Vertices.Count < 3 || face.Indices.Count < 3)
                {
                    result.Add(new OSDMap { ["NoGeometry"] = true });
                    continue;
                }
                if (face.Vertices.Count > 65536)
                    throw new ArgumentException(
                        $"Face {fi} has {face.Vertices.Count} vertices; a face holds at most 65536.");

                int count = face.Vertices.Count;
                var verts = new byte[count * 6];
                var normals = new byte[count * 6];
                var texcoords = new byte[count * 4];

                var minTc = new Vector2(float.MaxValue, float.MaxValue);
                var maxTc = new Vector2(float.MinValue, float.MinValue);
                foreach (var v in face.Vertices)
                {
                    minTc.X = Math.Min(minTc.X, v.TexCoord.X);
                    minTc.Y = Math.Min(minTc.Y, v.TexCoord.Y);
                    maxTc.X = Math.Max(maxTc.X, v.TexCoord.X);
                    maxTc.Y = Math.Max(maxTc.Y, v.TexCoord.Y);
                }
                var tcRange = maxTc - minTc;

                for (int j = 0; j < count; j++)
                {
                    var v = face.Vertices[j];
                    var p = positions[fi][j];
                    WriteU16(verts, j * 6, Quantize(p.X, minPos.X, posRange.X));
                    WriteU16(verts, j * 6 + 2, Quantize(p.Y, minPos.Y, posRange.Y));
                    WriteU16(verts, j * 6 + 4, Quantize(p.Z, minPos.Z, posRange.Z));

                    // Normals follow the normalization (inverse transpose of the scale)
                    var n = v.Normal * size;
                    var length = n.Length();
                    n = length > 0f ? n / length : Vector3.UnitZ;
                    WriteU16(normals, j * 6, Quantize(n.X, -1f, 2f));
                    WriteU16(normals, j * 6 + 2, Quantize(n.Y, -1f, 2f));
                    WriteU16(normals, j * 6 + 4, Quantize(n.Z, -1f, 2f));

                    WriteU16(texcoords, j * 4, Quantize(v.TexCoord.X, minTc.X, tcRange.X));
                    WriteU16(texcoords, j * 4 + 2, Quantize(v.TexCoord.Y, minTc.Y, tcRange.Y));
                }

                var indices = new byte[face.Indices.Count * 2];
                for (int j = 0; j < face.Indices.Count; j++)
                {
                    if (face.Indices[j] >= count)
                        throw new ArgumentException($"Face {fi} has an index past its vertices.");
                    WriteU16(indices, j * 2, (ushort)face.Indices[j]);
                }

                var map = new OSDMap
                {
                    ["PositionDomain"] = new OSDMap { ["Min"] = minPos, ["Max"] = maxPos },
                    ["NormalizedScale"] = size,
                    ["Position"] = verts,
                    ["Normal"] = normals,
                    ["TexCoord0Domain"] = new OSDMap { ["Min"] = minTc, ["Max"] = maxTc },
                    ["TexCoord0"] = texcoords,
                    ["TriangleList"] = indices
                };

                if (skin != null)
                {
                    if (face.Weights.Count != count)
                        throw new ArgumentException($"Rigged face {fi} needs one weight list per vertex.");
                    map["Weights"] = EncodeWeights(face.Weights, skin.JointNames.Count, fi);
                }

                result.Add(map);
            }
            return result;
        }

        /// <summary>
        /// Up to four influences per vertex, each a joint index byte and a 16 bit weight,
        /// ended by 0xFF when there are fewer than four.
        /// </summary>
        static byte[] EncodeWeights(List<ModelVertexWeight[]> weights, int jointCount, int faceIndex)
        {
            using var ms = new MemoryStream();
            for (int j = 0; j < weights.Count; j++)
            {
                var influences = weights[j]
                    .Where(w => w.Weight > 0f)
                    .OrderByDescending(w => w.Weight)
                    .Take(4)
                    .ToArray();
                if (influences.Length == 0)
                    throw new ArgumentException($"Rigged face {faceIndex}, vertex {j} has no joint influence.");

                float total = influences.Sum(w => w.Weight);
                foreach (var w in influences)
                {
                    if (w.Joint < 0 || w.Joint >= jointCount)
                        throw new ArgumentException(
                            $"Rigged face {faceIndex}, vertex {j} names joint {w.Joint}, past the skin's joints.");
                    ms.WriteByte((byte)w.Joint);
                    var value = Quantize(w.Weight / total, 0f, 1f);
                    ms.WriteByte((byte)(value & 0xFF));
                    ms.WriteByte((byte)(value >> 8));
                }
                if (influences.Length < 4)
                    ms.WriteByte(0xFF);
            }
            return ms.ToArray();
        }

        static OSDMap SkinToOSD(ModelSkin skin, Matrix4 bindShape)
        {
            var map = new OSDMap();
            var names = new OSDArray();
            foreach (var name in skin.JointNames)
                names.Add(name);
            map["joint_names"] = names;

            var inverseBind = new OSDArray();
            foreach (var m in skin.InverseBindMatrices)
                inverseBind.Add(MatrixToOSD(m));
            map["inverse_bind_matrix"] = inverseBind;
            map["bind_shape_matrix"] = MatrixToOSD(bindShape);

            if (skin.AlternateInverseBindMatrices != null)
            {
                var alt = new OSDArray();
                foreach (var m in skin.AlternateInverseBindMatrices)
                    alt.Add(MatrixToOSD(m));
                map["alt_inverse_bind_matrix"] = alt;
                if (skin.LockScaleIfJointPosition)
                    map["lock_scale_if_joint_position"] = true;
                map["pelvis_offset"] = skin.PelvisOffset;
            }
            return map;
        }

        static OSDArray MatrixToOSD(Matrix4 m)
        {
            return new OSDArray
            {
                m.M11, m.M12, m.M13, m.M14,
                m.M21, m.M22, m.M23, m.M24,
                m.M31, m.M32, m.M33, m.M34,
                m.M41, m.M42, m.M43, m.M44
            };
        }

        /// <summary>
        /// A single convex hull: the box around the high level of detail, in the
        /// normalized space (LLModel::Decomposition::asLLSD with only a base hull).
        /// </summary>
        static OSDMap BoundingBoxHull(List<ModelFace> high, Vector3 center, Vector3 size)
        {
            GetBounds(high, out var min, out var max);
            var scale = new Vector3(1f / size.X, 1f / size.Y, 1f / size.Z);
            var lo = (min - center) * scale;
            var hi = (max - center) * scale;

            var corners = new byte[8 * 6];
            for (int i = 0; i < 8; i++)
            {
                WriteU16(corners, i * 6, (i & 1) != 0 ? ushort.MaxValue : (ushort)0);
                WriteU16(corners, i * 6 + 2, (i & 2) != 0 ? ushort.MaxValue : (ushort)0);
                WriteU16(corners, i * 6 + 4, (i & 4) != 0 ? ushort.MaxValue : (ushort)0);
            }
            return new OSDMap
            {
                ["Min"] = lo,
                ["Max"] = hi,
                ["BoundingVerts"] = corners
            };
        }

        /// <summary>
        /// Binary LLSD header of block offsets, then each block zlib compressed, in the
        /// viewer's order (LLModel::writeModelToStream).
        /// </summary>
        static byte[] WriteModelToStream(OSDMap mdl)
        {
            var header = new OSDMap();
            var blocks = new List<byte[]>();
            int offset = 0;

            foreach (var key in new[] { "skin", "physics_convex", "lowest_lod", "low_lod", "medium_lod", "high_lod", "physics_mesh" })
            {
                if (!mdl.TryGetValue(key, out var block))
                    continue;
                var bytes = Helpers.ZCompressOSD(block);
                header[key] = new OSDMap { ["offset"] = offset, ["size"] = bytes.Length };
                offset += bytes.Length;
                blocks.Add(bytes);
            }

            var headerBytes = OSDParser.SerializeLLSDBinary(header, false);
            var asset = new byte[headerBytes.Length + offset];
            Buffer.BlockCopy(headerBytes, 0, asset, 0, headerBytes.Length);
            int at = headerBytes.Length;
            foreach (var bytes in blocks)
            {
                Buffer.BlockCopy(bytes, 0, asset, at, bytes.Length);
                at += bytes.Length;
            }
            return asset;
        }

        static ushort Quantize(float value, float lower, float range)
        {
            if (range <= 0f)
                return 0;
            float t = (value - lower) / range;
            if (t <= 0f) return 0;
            if (t >= 1f) return ushort.MaxValue;
            return (ushort)(t * ushort.MaxValue);
        }

        static void WriteU16(byte[] buffer, int offset, ushort value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)(value >> 8);
        }
    }
}
