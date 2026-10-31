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

using System.Text;
using System.Text.Json;
using NUnit.Framework;
using OpenMetaverse;
using OpenMetaverse.ImportExport;
using OpenMetaverse.StructuredData;

namespace LibreMetaverse.Tests
{
    [TestFixture]
    public class GltfMaterialAssetTests
    {
        [Test]
        public void EncodesTheViewersLayout()
        {
            var baseColor = UUID.Random();
            var normal = UUID.Random();
            var orm = UUID.Random();
            var material = new GltfMaterialAsset
            {
                BaseColorTexture = baseColor,
                NormalTexture = normal,
                MetallicRoughnessTexture = orm,
                BaseColor = new Color4(0.5f, 0.25f, 1f, 1f),
                MetallicFactor = 0f,
                RoughnessFactor = 0.4f,
                AlphaMode = "MASK"
            };

            var bytes = material.Encode();
            const string header = "<? LLSD/Binary ?>\n";
            Assert.That(Encoding.ASCII.GetString(bytes, 0, header.Length), Is.EqualTo(header));

            var asset = (OSDMap)OSDParser.DeserializeLLSDBinary(bytes);
            Assert.That(asset["version"].AsString(), Is.EqualTo("1.1"));
            Assert.That(asset["type"].AsString(), Is.EqualTo("GLTF 2.0"));

            using var doc = JsonDocument.Parse(asset["data"].AsString());
            var root = doc.RootElement;
            var mat = root.GetProperty("materials")[0];
            string Uri(JsonElement info) =>
                root.GetProperty("images")[root.GetProperty("textures")[info.GetProperty("index").GetInt32()].GetProperty("source").GetInt32()]
                    .GetProperty("uri").GetString()!;

            var pbr = mat.GetProperty("pbrMetallicRoughness");
            Assert.That(Uri(pbr.GetProperty("baseColorTexture")), Is.EqualTo(baseColor.ToString()));
            Assert.That(Uri(pbr.GetProperty("metallicRoughnessTexture")), Is.EqualTo(orm.ToString()));
            Assert.That(Uri(mat.GetProperty("occlusionTexture")), Is.EqualTo(orm.ToString()));
            Assert.That(Uri(mat.GetProperty("normalTexture")), Is.EqualTo(normal.ToString()));
            Assert.That(mat.GetProperty("alphaMode").GetString(), Is.EqualTo("MASK"));
            Assert.That(pbr.GetProperty("baseColorFactor")[1].GetSingle(), Is.EqualTo(0.25f));
            Assert.That(pbr.GetProperty("roughnessFactor").GetSingle(), Is.EqualTo(0.4f));
            Assert.That(root.GetProperty("asset").GetProperty("version").GetString(), Is.EqualTo("2.0"));
            Assert.That(mat.TryGetProperty("emissiveTexture", out _), Is.False);
        }

        [Test]
        public void MaterialInventoryTypeMatchesTheViewer()
        {
            Assert.That((int)InventoryType.Material, Is.EqualTo(26));
            Assert.That(Utils.InventoryTypeToString(InventoryType.Material), Is.EqualTo("material"));
            Assert.That(Utils.StringToInventoryType("settings"), Is.EqualTo(InventoryType.Settings));
        }
    }
}
