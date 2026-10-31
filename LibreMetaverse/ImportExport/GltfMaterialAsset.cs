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
using System.Text;
using System.Text.Json;
using OpenMetaverse.StructuredData;

namespace OpenMetaverse.ImportExport
{
    /// <summary>
    /// A PBR material asset (<see cref="AssetType.Material"/>) as the viewer writes it
    /// (LLGLTFMaterial::asJSON wrapped by LLMaterialEditor::getEncodedAsset): a binary LLSD map of
    /// version, type and a one-material glTF document whose image URIs are texture asset UUIDs.
    /// </summary>
    public class GltfMaterialAsset
    {
        public const string AssetVersion = "1.1";
        public const string AssetTypeName = "GLTF 2.0";

        public UUID BaseColorTexture = UUID.Zero;
        public UUID NormalTexture = UUID.Zero;
        /// <summary>Occlusion (R), roughness (G), metallic (B); also written as the occlusion texture</summary>
        public UUID MetallicRoughnessTexture = UUID.Zero;
        public UUID EmissiveTexture = UUID.Zero;

        /// <summary>Linear RGBA</summary>
        public Color4 BaseColor = Color4.White;
        /// <summary>Linear RGB</summary>
        public Vector3 EmissiveColor = Vector3.Zero;
        public float MetallicFactor = 1f;
        public float RoughnessFactor = 1f;
        /// <summary>OPAQUE, MASK or BLEND</summary>
        public string AlphaMode = "OPAQUE";
        public float AlphaCutoff = 0.5f;
        public bool DoubleSided;

        /// <summary>The glTF document the viewer stores in the asset's "data" field</summary>
        public string ToJson()
        {
            var images = new List<UUID>();
            int Add(UUID id)
            {
                images.Add(id);
                return images.Count - 1;
            }

            using var buffer = new MemoryStream();
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                w.WriteStartObject("asset");
                w.WriteString("version", "2.0");
                w.WriteEndObject();

                w.WriteStartArray("materials");
                w.WriteStartObject();
                w.WriteString("alphaMode", AlphaMode);
                w.WriteNumber("alphaCutoff", AlphaCutoff);
                w.WriteBoolean("doubleSided", DoubleSided);
                if (EmissiveColor != Vector3.Zero)
                {
                    w.WriteStartArray("emissiveFactor");
                    w.WriteNumberValue(EmissiveColor.X);
                    w.WriteNumberValue(EmissiveColor.Y);
                    w.WriteNumberValue(EmissiveColor.Z);
                    w.WriteEndArray();
                }
                if (EmissiveTexture != UUID.Zero)
                    WriteTexture(w, "emissiveTexture", Add(EmissiveTexture));
                if (NormalTexture != UUID.Zero)
                    WriteTexture(w, "normalTexture", Add(NormalTexture));
                // The viewer writes the ORM map as the occlusion texture too, for glTF compliance
                if (MetallicRoughnessTexture != UUID.Zero)
                    WriteTexture(w, "occlusionTexture", Add(MetallicRoughnessTexture));

                w.WriteStartObject("pbrMetallicRoughness");
                w.WriteStartArray("baseColorFactor");
                w.WriteNumberValue(BaseColor.R);
                w.WriteNumberValue(BaseColor.G);
                w.WriteNumberValue(BaseColor.B);
                w.WriteNumberValue(BaseColor.A);
                w.WriteEndArray();
                if (BaseColorTexture != UUID.Zero)
                    WriteTexture(w, "baseColorTexture", Add(BaseColorTexture));
                w.WriteNumber("metallicFactor", MetallicFactor);
                if (MetallicRoughnessTexture != UUID.Zero)
                    WriteTexture(w, "metallicRoughnessTexture", Add(MetallicRoughnessTexture));
                w.WriteNumber("roughnessFactor", RoughnessFactor);
                w.WriteEndObject();
                w.WriteEndObject();
                w.WriteEndArray();

                if (images.Count > 0)
                {
                    w.WriteStartArray("images");
                    foreach (var id in images)
                    {
                        w.WriteStartObject();
                        w.WriteString("uri", id.ToString());
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();

                    w.WriteStartArray("textures");
                    for (var i = 0; i < images.Count; i++)
                    {
                        w.WriteStartObject();
                        w.WriteNumber("source", i);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        private static void WriteTexture(Utf8JsonWriter w, string name, int index)
        {
            w.WriteStartObject(name);
            w.WriteNumber("index", index);
            w.WriteEndObject();
        }

        /// <summary>The asset bytes to upload: binary LLSD with the viewer's header</summary>
        public byte[] Encode()
        {
            var asset = new OSDMap
            {
                ["version"] = AssetVersion,
                ["type"] = AssetTypeName,
                ["data"] = ToJson()
            };
            var header = Encoding.ASCII.GetBytes("<? LLSD/Binary ?>\n");
            var body = OSDParser.SerializeLLSDBinary(asset, false);
            var bytes = new byte[header.Length + body.Length];
            Buffer.BlockCopy(header, 0, bytes, 0, header.Length);
            Buffer.BlockCopy(body, 0, bytes, header.Length, body.Length);
            return bytes;
        }
    }
}
