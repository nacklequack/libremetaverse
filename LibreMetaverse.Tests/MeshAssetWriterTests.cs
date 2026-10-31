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

using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using OpenMetaverse;
using OpenMetaverse.Assets;
using OpenMetaverse.ImportExport;
using OpenMetaverse.Rendering;
using OpenMetaverse.StructuredData;

namespace LibreMetaverse.Tests
{
    [TestFixture]
    public class MeshAssetWriterTests
    {
        static ModelFace Quad(string material, float z, ModelVertexWeight[] weights)
        {
            var face = new ModelFace { MaterialID = material };
            var corners = new[] { new Vector3(0, 0, z), new Vector3(1, 0, z), new Vector3(1, 2, z), new Vector3(0, 2, z) };
            foreach (var c in corners)
            {
                face.Vertices.Add(new Vertex { Position = c, Normal = Vector3.UnitZ, TexCoord = new Vector2(c.X, c.Y / 2) });
                face.Weights.Add(weights);
            }
            face.Indices.AddRange(new uint[] { 0, 1, 2, 0, 2, 3 });
            return face;
        }

        static ModelPrim RiggedPrim()
        {
            var four = new[]
            {
                new ModelVertexWeight(0, 0.4f), new ModelVertexWeight(1, 0.3f),
                new ModelVertexWeight(2, 0.2f), new ModelVertexWeight(3, 0.1f)
            };
            var one = new[] { new ModelVertexWeight(1, 1f) };
            return new ModelPrim
            {
                Faces = new List<ModelFace> { Quad("a", 0, four), Quad("b", 1, one) },
                LowestLodFaces = new List<ModelFace> { Quad("b", 1, one) },
                Skin = new ModelSkin
                {
                    JointNames = new List<string> { "mPelvis", "mTorso", "mChest", "mNeck" },
                    InverseBindMatrices = new List<Matrix4> { Matrix4.Identity, Matrix4.Identity, Matrix4.Identity, Matrix4.Identity }
                }
            };
        }

        [Test]
        public void WritesEveryLevelSkinAndPhysics()
        {
            var prim = RiggedPrim();
            var asset = MeshAssetWriter.Encode(prim);

            var header = (OSDMap)OSDParser.DeserializeLLSDBinary(new MemoryStream(asset));
            foreach (var key in new[] { "skin", "physics_convex", "high_lod", "medium_lod", "low_lod", "lowest_lod" })
                Assert.That(header.ContainsKey(key), Is.True, key);

            Assert.That(prim.Scale, Is.EqualTo(new Vector3(1, 2, 1)));
            Assert.That(prim.Position, Is.EqualTo(new Vector3(0.5f, 1, 0.5f)));
            Assert.That(prim.PhysicsShapeType, Is.EqualTo(2));
        }

        [Test]
        public void LowerLevelKeepsHighFaceOrderAndBounds()
        {
            var prim = RiggedPrim();
            var mesh = new AssetMesh(UUID.Random(), MeshAssetWriter.Encode(prim));
            var primitive = new Primitive { Textures = new Primitive.TextureEntry(UUID.Zero) };

            Assert.That(FacetedMesh.TryDecodeFromAsset(primitive, mesh, DetailLevel.Low, out var lowest), Is.True);
            var lowestArray = (OSDArray)mesh.MeshData["lowest_lod"];
            Assert.That(lowestArray.Count, Is.EqualTo(2));
            Assert.That(((OSDMap)lowestArray[0])["NoGeometry"].AsBoolean(), Is.True);
            Assert.That(lowest!.Faces.Count, Is.EqualTo(1));
            Assert.That(lowest.Faces[0].ID, Is.EqualTo(1));

            // The lowest level sits at z = 1 only, yet keeps the high level's normalization
            var bsm = lowest.SkinData!.BindShapeMatrix;
            var bind = new Matrix4(bsm[0], bsm[1], bsm[2], bsm[3], bsm[4], bsm[5], bsm[6], bsm[7],
                bsm[8], bsm[9], bsm[10], bsm[11], bsm[12], bsm[13], bsm[14], bsm[15]);
            foreach (var v in lowest.Faces[0].Vertices)
                Assert.That(Vector3.Transform(v.Position, bind).Z, Is.EqualTo(1f).Within(1e-4f));
        }

        [Test]
        public void FourInfluencesDecodeWithoutSentinel()
        {
            var prim = RiggedPrim();
            var mesh = new AssetMesh(UUID.Random(), MeshAssetWriter.Encode(prim));
            var primitive = new Primitive { Textures = new Primitive.TextureEntry(UUID.Zero) };

            Assert.That(FacetedMesh.TryDecodeFromAsset(primitive, mesh, DetailLevel.Highest, out var high), Is.True);
            var four = high!.Faces[0].Weights!;
            Assert.That(four.Count, Is.EqualTo(4));
            foreach (var w in four)
            {
                Assert.That(w.Joint0, Is.EqualTo(0));
                Assert.That(w.Joint3, Is.EqualTo(3));
                Assert.That(w.Weight0, Is.EqualTo(0.4f).Within(1e-3f));
                Assert.That(w.Weight3, Is.EqualTo(0.1f).Within(1e-3f));
            }
            Assert.That(high.Faces[1].Weights![0].Joint0, Is.EqualTo(1));
        }

        [Test]
        public void RejectsMoreThanEightFaces()
        {
            var prim = new ModelPrim();
            for (int i = 0; i < 9; i++)
                prim.Faces.Add(Quad("m" + i, i, new[] { new ModelVertexWeight(0, 1f) }));
            Assert.Throws<System.ArgumentException>(() => MeshAssetWriter.Encode(prim));
        }
    }
}
