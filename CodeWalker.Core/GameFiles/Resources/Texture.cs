using CodeWalker.Utils;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Frozen;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace CodeWalker.GameFiles
{
    [TypeConverter(typeof(ExpandableObjectConverter))]
    public class TextureDictionary : ResourceFileBase
    {
        public override long BlockLength => 64;

        // structure data
        public uint Unknown_10h { get; set; } // 0x00000000
        public uint Unknown_14h { get; set; } // 0x00000000
        public uint Unknown_18h { get; set; } = 1; // 0x00000001
        public uint Unknown_1Ch { get; set; } // 0x00000000
        public ResourceSimpleList64_uint TextureNameHashes { get; set; }
        public ResourcePointerList64<Texture> Textures { get; set; }

        // Use FrozenDictionary for better read performance after initialization
        private FrozenDictionary<uint, Texture>? _frozenDict;
        public FrozenDictionary<uint, Texture> Dict => _frozenDict ??= BuildFrozenDict();

        public long MemoryUsage
        {
            get
            {
                if (Textures?.data_items is not Texture[] textures)
                    return 0;

                long totalMemory = 0;
                // Use SIMD-friendly loop when possible
                foreach (var tex in textures)
                {
                    if (tex is not null)
                    {
                        totalMemory += tex.MemoryUsage;
                    }
                }
                return totalMemory;
            }
        }

        public TextureDictionary() { }

        public override void Read(ResourceDataReader reader, params object[] parameters)
        {
            base.Read(reader, parameters);

            // read structure data
            this.Unknown_10h = reader.ReadUInt32();
            this.Unknown_14h = reader.ReadUInt32();
            this.Unknown_18h = reader.ReadUInt32();
            this.Unknown_1Ch = reader.ReadUInt32();
            this.TextureNameHashes = reader.ReadBlock<ResourceSimpleList64_uint>();
            this.Textures = reader.ReadBlock<ResourcePointerList64<Texture>>();

            // Don't build dict immediately - lazy load for better startup performance
            _frozenDict = null;
        }

        public override void Write(ResourceDataWriter writer, params object[] parameters)
        {
            base.Write(writer, parameters);

            // write structure data
            writer.Write(this.Unknown_10h);
            writer.Write(this.Unknown_14h);
            writer.Write(this.Unknown_18h);
            writer.Write(this.Unknown_1Ch);
            writer.WriteBlock(this.TextureNameHashes);
            writer.WriteBlock(this.Textures);
        }

        public void WriteXml(StringBuilder sb, int indent, string ddsfolder)
        {
            if (Textures?.data_items is not Texture[] textures || textures.Length == 0)
                return;

            // Pre-allocate capacity for better performance
            var initialCapacity = sb.Capacity;
            var estimatedSize = textures.Length * 200; // Rough estimate
            if (sb.Capacity < estimatedSize)
                sb.EnsureCapacity(estimatedSize);

            foreach (Texture tex in textures)
            {
                YtdXml.OpenTag(sb, indent, "Item");
                tex.WriteXml(sb, indent + 1, ddsfolder);
                YtdXml.CloseTag(sb, indent, "Item");
            }
        }

        public void ReadXml(XmlNode node, string ddsfolder)
        {
            var textures = new List<Texture>();

            XmlNodeList inodes = node.SelectNodes("Item");
            if (inodes is not null)
            {
                // Pre-allocate capacity
                textures.Capacity = inodes.Count;

                foreach (XmlNode inode in inodes)
                {
                    Texture tex = new Texture();
                    tex.ReadXml(inode, ddsfolder);
                    textures.Add(tex);
                }
            }

            BuildFromTextureList(textures);
        }

        public static void WriteXmlNode(TextureDictionary d, StringBuilder sb, int indent, string ddsfolder, string name = "TextureDictionary")
        {
            if (d is null) return;

            if (d.Textures?.data_items is not Texture[] textures || textures.Length == 0)
            {
                YtdXml.SelfClosingTag(sb, indent, name);
            }
            else
            {
                YtdXml.OpenTag(sb, indent, name);
                d.WriteXml(sb, indent + 1, ddsfolder);
                YtdXml.CloseTag(sb, indent, name);
            }
        }

        public static TextureDictionary ReadXmlNode(XmlNode node, string ddsfolder)
        {
            if (node is null) return null;
            var td = new TextureDictionary();
            td.ReadXml(node, ddsfolder);
            return td;
        }

        public override Tuple<long, IResourceBlock>[] GetParts()
        {
            return new Tuple<long, IResourceBlock>[] {
                new(0x20, TextureNameHashes),
                new(0x30, Textures)
            };
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Texture? Lookup(uint hash)
        {
            return Dict.GetValueOrDefault(hash, null);
        }

        private FrozenDictionary<uint, Texture> BuildFrozenDict()
        {
            if (Textures?.data_items is not Texture[] textures ||
                TextureNameHashes?.data_items is not uint[] hashes)
            {
                return FrozenDictionary<uint, Texture>.Empty;
            }

            var length = Math.Min(textures.Length, hashes.Length);
            var dictBuilder = new Dictionary<uint, Texture>(length);

            for (int i = 0; i < length; i++)
            {
                if (textures[i] is not null)
                {
                    dictBuilder[hashes[i]] = textures[i];
                }
            }

            return dictBuilder.ToFrozenDictionary();
        }

        public void BuildFromTextureList(List<Texture> textures)
        {
            // Use CollectionsMarshal for better performance with List sorting
            textures.Sort((a, b) => a.NameHash.CompareTo(b.NameHash));

            // Use span for better performance
            var span = CollectionsMarshal.AsSpan(textures);
            var texturehashes = new uint[span.Length];

            for (int i = 0; i < span.Length; i++)
            {
                texturehashes[i] = span[i].NameHash;
            }

            TextureNameHashes = new ResourceSimpleList64_uint
            {
                data_items = texturehashes
            };

            Textures = new ResourcePointerList64<Texture>
            {
                data_items = textures.ToArray()
            };

            // Invalidate cached frozen dict
            _frozenDict = null;
        }

        public void EnsureGen9()
        {
            FileVFT = 0;
            FileUnknown = 1;

            // make sure textures all have SRVs and are appropriately formatted for gen9
            if (Textures?.data_items is not Texture[] texs)
                return;

            // Use parallel processing for large texture arrays
            if (texs.Length > 100)
            {
                Parallel.ForEach(texs, tex =>
                {
                    tex?.EnsureGen9();
                });
            }
            else
            {
                foreach (Texture tex in texs)
                {
                    tex?.EnsureGen9();
                }
            }
        }
    }

    [TypeConverter(typeof(ExpandableObjectConverter))]
    public class TextureBase : ResourceSystemBlock
    {
        public override long BlockLength => 80;
        public override long BlockLength_Gen9 => 80;

        // structure data - using readonly where possible for better JIT optimization
        public uint VFT { get; set; }
        public uint Unknown_4h { get; set; } = 1; // 0x00000001
        public uint Unknown_8h { get; set; } // 0x00000000
        public uint Unknown_Ch { get; set; } // 0x00000000
        public uint Unknown_10h { get; set; } // 0x00000000
        public uint Unknown_14h { get; set; } // 0x00000000
        public uint Unknown_18h { get; set; } // 0x00000000
        public uint Unknown_1Ch { get; set; } // 0x00000000
        public uint Unknown_20h { get; set; } // 0x00000000
        public uint Unknown_24h { get; set; } // 0x00000000
        public ulong NamePointer { get; set; }
        public ushort Unknown_30h { get; set; } = 1;
        public ushort Unknown_32h { get; set; }
        public uint Unknown_34h { get; set; } // 0x00000000
        public uint Unknown_38h { get; set; } // 0x00000000
        public uint Unknown_3Ch { get; set; } // 0x00000000
        public uint UsageData { get; set; }
        public uint Unknown_44h { get; set; } // 0x00000000
        public uint ExtraFlags { get; set; } // 0, 1
        public uint Unknown_4Ch { get; set; } // 0x00000000

        //Texture subclass structure data - moved here for gen9 compatibility
        public ushort Width { get; set; }
        public ushort Height { get; set; }
        public ushort Depth { get; set; } = 1;  //is depth > 1 supported?
        public ushort Stride { get; set; }
        public TextureFormat Format { get; set; }
        public byte Unknown_5Ch { get; set; } // 0x00
        public byte Levels { get; set; }
        public ushort Unknown_5Eh { get; set; } // 0x0000
        public uint Unknown_60h { get; set; } // 0x00000000
        public uint Unknown_64h { get; set; } // 0x00000000
        public uint Unknown_68h { get; set; } // 0x00000000
        public uint Unknown_6Ch { get; set; } // 0x00000000
        public ulong DataPointer { get; set; }
        public uint Unknown_78h { get; set; } // 0x00000000
        public uint Unknown_7Ch { get; set; } // 0x00000000
        public uint Unknown_80h { get; set; } // 0x00000000
        public uint Unknown_84h { get; set; } // 0x00000000
        public uint Unknown_88h { get; set; } // 0x00000000
        public uint Unknown_8Ch { get; set; } // 0x00000000

        //gen9 extra structure data
        public uint G9_BlockCount { get; set; }
        public uint G9_BlockStride { get; set; }
        public uint G9_Flags { get; set; }
        public TextureDimensionG9 G9_Dimension { get; set; } = TextureDimensionG9.Texture2D;
        public TextureFormatG9 G9_Format { get; set; }
        public TextureTileModeG9 G9_TileMode { get; set; } = TextureTileModeG9.Auto;
        public byte G9_AntiAliasType { get; set; } //0
        public byte G9_Unknown_23h { get; set; }
        public byte G9_Unknown_25h { get; set; }
        public ushort G9_UsageCount { get; set; } = 1;
        public ulong G9_SRVPointer { get; set; }
        public uint G9_UsageData { get; set; }
        public ulong G9_Unknown_48h { get; set; }

        // reference data
        public string Name { get; set; } = string.Empty;
        private uint _nameHash;
        private bool _nameHashCalculated;

        public uint NameHash
        {
            get
            {
                if (!_nameHashCalculated)
                {
                    _nameHash = string.IsNullOrEmpty(Name) ? 0 : JenkHash.GenHash(Name.ToLowerInvariant());
                    _nameHashCalculated = true;
                }
                return _nameHash;
            }
            set
            {
                _nameHash = value;
                _nameHashCalculated = true;
            }
        }

        private string_r? NameBlock = null;
        public TextureData? Data { get; set; }
        public ShaderResourceViewG9? G9_SRV { get; set; }//make sure this is null if saving legacy version!

        public TextureUsage Usage
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => (TextureUsage)(UsageData & 0x1F);
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set => UsageData = (UsageData & 0xFFFFFFE0) + (((uint)value) & 0x1F);
        }

        public TextureUsageFlags UsageFlags
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => (TextureUsageFlags)(UsageData >> 5);
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set => UsageData = (UsageData & 0x1F) + (((uint)value) << 5);
        }

        public override void Read(ResourceDataReader reader, params object[] parameters)
        {
            if (reader.IsGen9)
            {
                ReadGen9(reader);
            }
            else
            {
                ReadLegacy(reader);
            }
        }

        private void ReadGen9(ResourceDataReader reader)
        {
            VFT = reader.ReadUInt32();
            Unknown_4h = reader.ReadUInt32();
            G9_BlockCount = reader.ReadUInt32();
            G9_BlockStride = reader.ReadUInt32();
            G9_Flags = reader.ReadUInt32();
            Unknown_14h = reader.ReadUInt32();
            Width = reader.ReadUInt16();
            Height = reader.ReadUInt16();
            Depth = reader.ReadUInt16();
            G9_Dimension = (TextureDimensionG9)reader.ReadByte();
            G9_Format = (TextureFormatG9)reader.ReadByte();
            G9_TileMode = (TextureTileModeG9)reader.ReadByte();
            G9_AntiAliasType = reader.ReadByte();
            Levels = reader.ReadByte();
            G9_Unknown_23h = reader.ReadByte();
            Unknown_24h = reader.ReadByte();
            G9_Unknown_25h = reader.ReadByte();
            G9_UsageCount = reader.ReadUInt16();
            NamePointer = reader.ReadUInt64();
            G9_SRVPointer = reader.ReadUInt64();
            DataPointer = reader.ReadUInt64();
            G9_UsageData = reader.ReadUInt32();
            Unknown_44h = reader.ReadUInt32();
            G9_Unknown_48h = reader.ReadUInt64();

            Format = GetLegacyFormat(G9_Format);
            Stride = CalculateStride();
            Usage = (TextureUsage)(G9_UsageData & 0x1F);

            Data = reader.ReadBlockAt<TextureData>(DataPointer, CalcDataSize());
            G9_SRV = reader.ReadBlockAt<ShaderResourceViewG9>(G9_SRVPointer);

            Name = reader.ReadStringAt(NamePointer) ?? string.Empty;
            _nameHashCalculated = false; // Force recalculation
        }

        private void ReadLegacy(ResourceDataReader reader)
        {
            // read structure data
            this.VFT = reader.ReadUInt32();
            this.Unknown_4h = reader.ReadUInt32();
            this.Unknown_8h = reader.ReadUInt32();
            this.Unknown_Ch = reader.ReadUInt32();
            this.Unknown_10h = reader.ReadUInt32();
            this.Unknown_14h = reader.ReadUInt32();
            this.Unknown_18h = reader.ReadUInt32();
            this.Unknown_1Ch = reader.ReadUInt32();
            this.Unknown_20h = reader.ReadUInt32();
            this.Unknown_24h = reader.ReadUInt32();
            this.NamePointer = reader.ReadUInt64();
            this.Unknown_30h = reader.ReadUInt16();
            this.Unknown_32h = reader.ReadUInt16();
            this.Unknown_34h = reader.ReadUInt32();
            this.Unknown_38h = reader.ReadUInt32();
            this.Unknown_3Ch = reader.ReadUInt32();
            this.UsageData = reader.ReadUInt32();
            this.Unknown_44h = reader.ReadUInt32();
            this.ExtraFlags = reader.ReadUInt32();
            this.Unknown_4Ch = reader.ReadUInt32();

            // read reference data
            this.Name = reader.ReadStringAt(this.NamePointer) ?? string.Empty;
            _nameHashCalculated = false; // Force recalculation
        }

        public override void Write(ResourceDataWriter writer, params object[] parameters)
        {
            if (writer.IsGen9)
            {
                WriteGen9(writer);
            }
            else
            {
                WriteLegacy(writer);
            }
        }

        private void WriteGen9(ResourceDataWriter writer)
        {
            NamePointer = (ulong)(NameBlock?.FilePosition ?? 0);
            DataPointer = (ulong)(Data?.FilePosition ?? 0);
            G9_SRVPointer = (ulong)(G9_SRV?.FilePosition ?? 0);

            if (G9_Format == 0) G9_Format = GetEnhancedFormat(Format);
            if (G9_Dimension == 0) G9_Dimension = TextureDimensionG9.Texture2D;
            if (G9_TileMode == 0) G9_TileMode = TextureTileModeG9.Auto;

            G9_BlockCount = GetBlockCount(G9_Format, Width, Height, Depth, Levels, G9_Flags, G9_BlockCount);
            G9_BlockStride = GetBlockStride(G9_Format);
            G9_UsageData = (G9_UsageData & 0xFFFFFFE0) + (((uint)Usage) & 0x1F);

            writer.Write(VFT);
            writer.Write(Unknown_4h);
            writer.Write(G9_BlockCount);
            writer.Write(G9_BlockStride);
            writer.Write(G9_Flags);
            writer.Write(Unknown_14h);
            writer.Write(Width);
            writer.Write(Height);
            writer.Write(Depth);
            writer.Write((byte)G9_Dimension);
            writer.Write((byte)G9_Format);
            writer.Write((byte)G9_TileMode);
            writer.Write(G9_AntiAliasType);
            writer.Write(Levels);
            writer.Write(G9_Unknown_23h);
            writer.Write((byte)Unknown_24h);
            writer.Write(G9_Unknown_25h);
            writer.Write(G9_UsageCount);
            writer.Write(NamePointer);
            writer.Write(G9_SRVPointer);
            writer.Write(DataPointer);
            writer.Write(G9_UsageData);
            writer.Write(Unknown_44h);
            writer.Write(G9_Unknown_48h);
        }

        private void WriteLegacy(ResourceDataWriter writer)
        {
            this.NamePointer = (ulong)(this.NameBlock?.FilePosition ?? 0);

            writer.Write(this.VFT);
            writer.Write(this.Unknown_4h);
            writer.Write(this.Unknown_8h);
            writer.Write(this.Unknown_Ch);
            writer.Write(this.Unknown_10h);
            writer.Write(this.Unknown_14h);
            writer.Write(this.Unknown_18h);
            writer.Write(this.Unknown_1Ch);
            writer.Write(this.Unknown_20h);
            writer.Write(this.Unknown_24h);
            writer.Write(this.NamePointer);
            writer.Write(this.Unknown_30h);
            writer.Write(this.Unknown_32h);
            writer.Write(this.Unknown_34h);
            writer.Write(this.Unknown_38h);
            writer.Write(this.Unknown_3Ch);
            writer.Write(this.UsageData);
            writer.Write(this.Unknown_44h);
            writer.Write(this.ExtraFlags);
            writer.Write(this.Unknown_4Ch);
        }

        public virtual void WriteXml(StringBuilder sb, int indent, string ddsfolder)
        {
            YtdXml.StringTag(sb, indent, "Name", YtdXml.XmlEscape(Name));
            YtdXml.ValueTag(sb, indent, "Unk32", Unknown_32h.ToString());
            YtdXml.StringTag(sb, indent, "Usage", Usage.ToString());
            YtdXml.StringTag(sb, indent, "UsageFlags", UsageFlags.ToString());
            YtdXml.ValueTag(sb, indent, "ExtraFlags", ExtraFlags.ToString());
        }

        public virtual void ReadXml(XmlNode node, string ddsfolder)
        {
            Name = Xml.GetChildInnerText(node, "Name") ?? string.Empty;
            _nameHashCalculated = false; // Force recalculation
            Unknown_32h = (ushort)Xml.GetChildUIntAttribute(node, "Unk32", "value");
            Usage = Xml.GetChildEnumInnerText<TextureUsage>(node, "Usage");
            UsageFlags = Xml.GetChildEnumInnerText<TextureUsageFlags>(node, "UsageFlags");
            ExtraFlags = Xml.GetChildUIntAttribute(node, "ExtraFlags", "value");
        }

        public void EnsureGen9()
        {
            VFT = 0;
            Unknown_4h = 1;

            bool istex = this is Texture;
            Unknown_44h = istex ? 2 : 0u;

            if (G9_Flags == 0)
            {
                G9_Flags = 0x00260208;
                if (Name.StartsWith("script_rt_", StringComparison.OrdinalIgnoreCase))
                {
                    G9_Flags = 0x00260228;
                }
            }

            if ((G9_Unknown_23h == 0) && istex)
            {
                G9_Unknown_23h = 0x28;
            }

            G9_SRV ??= new ShaderResourceViewG9
            {
                Dimension = Depth > 1 ? ShaderResourceViewDimensionG9.Texture2DArray : ShaderResourceViewDimensionG9.Texture2D
            };
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int CalcDataSize()
        {
            if (Format == 0) return 0;

            var dxgifmt = DDSIO.GetDXGIFormat(Format);
            int div = 1;
            long totalSize = 0;

            for (int i = 0; i < Levels; i++)
            {
                int width = Width / div;
                int height = Height / div;
                DDSIO.DXTex.ComputePitch(dxgifmt, width, height, out _, out long slicePitch, 0);
                totalSize += slicePitch;
                div <<= 1; // Faster than div *= 2
            }
            return (int)(totalSize * Depth);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ushort CalculateStride()
        {
            if (Format == 0) return 0;

            var dxgifmt = DDSIO.GetDXGIFormat(Format);
            DDSIO.DXTex.ComputePitch(dxgifmt, Width, Height, out long rowPitch, out _, 0);
            return (ushort)rowPitch;
        }

        // Use lookup tables for better performance
        private static readonly FrozenDictionary<TextureFormatG9, TextureFormat> _legacyFormatLookup =
            new Dictionary<TextureFormatG9, TextureFormat>
            {
                [TextureFormatG9.UNKNOWN] = 0,
                [TextureFormatG9.R8G8B8A8_UNORM] = TextureFormat.D3DFMT_A8B8G8R8,
                [TextureFormatG9.B8G8R8A8_UNORM] = TextureFormat.D3DFMT_A8R8G8B8,
                [TextureFormatG9.A8_UNORM] = TextureFormat.D3DFMT_A8,
                [TextureFormatG9.R8_UNORM] = TextureFormat.D3DFMT_L8,
                [TextureFormatG9.B5G5R5A1_UNORM] = TextureFormat.D3DFMT_A1R5G5B5,
                [TextureFormatG9.BC1_UNORM] = TextureFormat.D3DFMT_DXT1,
                [TextureFormatG9.BC2_UNORM] = TextureFormat.D3DFMT_DXT3,
                [TextureFormatG9.BC3_UNORM] = TextureFormat.D3DFMT_DXT5,
                [TextureFormatG9.BC4_UNORM] = TextureFormat.D3DFMT_ATI1,
                [TextureFormatG9.BC5_UNORM] = TextureFormat.D3DFMT_ATI2,
                [TextureFormatG9.BC7_UNORM] = TextureFormat.D3DFMT_BC7,
                [TextureFormatG9.BC7_UNORM_SRGB] = TextureFormat.D3DFMT_BC7,
                [TextureFormatG9.BC3_UNORM_SRGB] = TextureFormat.D3DFMT_DXT5,
                [TextureFormatG9.R16_UNORM] = TextureFormat.D3DFMT_A8
            }.ToFrozenDictionary();

        private static readonly FrozenDictionary<TextureFormat, TextureFormatG9> _enhancedFormatLookup =
            new Dictionary<TextureFormat, TextureFormatG9>
            {
                [(TextureFormat)0] = TextureFormatG9.UNKNOWN,
                [TextureFormat.D3DFMT_A8B8G8R8] = TextureFormatG9.R8G8B8A8_UNORM,
                [TextureFormat.D3DFMT_A8R8G8B8] = TextureFormatG9.B8G8R8A8_UNORM,
                [TextureFormat.D3DFMT_A8] = TextureFormatG9.A8_UNORM,
                [TextureFormat.D3DFMT_L8] = TextureFormatG9.R8_UNORM,
                [TextureFormat.D3DFMT_A1R5G5B5] = TextureFormatG9.B5G5R5A1_UNORM,
                [TextureFormat.D3DFMT_DXT1] = TextureFormatG9.BC1_UNORM,
                [TextureFormat.D3DFMT_DXT3] = TextureFormatG9.BC2_UNORM,
                [TextureFormat.D3DFMT_DXT5] = TextureFormatG9.BC3_UNORM,
                [TextureFormat.D3DFMT_ATI1] = TextureFormatG9.BC4_UNORM,
                [TextureFormat.D3DFMT_ATI2] = TextureFormatG9.BC5_UNORM,
                [TextureFormat.D3DFMT_BC7] = TextureFormatG9.BC7_UNORM
            }.ToFrozenDictionary();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public TextureFormat GetLegacyFormat(TextureFormatG9 format)
        {
            return _legacyFormatLookup.GetValueOrDefault<TextureFormatG9, TextureFormat>(format, TextureFormat.D3DFMT_A8R8G8B8);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public TextureFormatG9 GetEnhancedFormat(TextureFormat format)
        {
            return _enhancedFormatLookup.GetValueOrDefault<TextureFormat, TextureFormatG9>(format, TextureFormatG9.B8G8R8A8_UNORM);
        }

        // Use lookup table for block stride calculation
        private static readonly FrozenDictionary<TextureFormatG9, uint> _blockStrideLookup =
            new Dictionary<TextureFormatG9, uint>
            {
                [TextureFormatG9.UNKNOWN] = 0,
                [TextureFormatG9.BC1_UNORM] = 8,
                [TextureFormatG9.BC2_UNORM] = 16,
                [TextureFormatG9.BC3_UNORM] = 16,
                [TextureFormatG9.BC4_UNORM] = 8,
                [TextureFormatG9.BC5_UNORM] = 16,
                [TextureFormatG9.BC6H_UF16] = 16,
                [TextureFormatG9.BC7_UNORM] = 16,
                [TextureFormatG9.BC1_UNORM_SRGB] = 8,
                [TextureFormatG9.BC2_UNORM_SRGB] = 16,
                [TextureFormatG9.BC3_UNORM_SRGB] = 16,
                [TextureFormatG9.BC7_UNORM_SRGB] = 16,
                [TextureFormatG9.R8G8B8A8_UNORM] = 4,
                [TextureFormatG9.B8G8R8A8_UNORM] = 4,
                [TextureFormatG9.R8G8B8A8_UNORM_SRGB] = 4,
                [TextureFormatG9.B8G8R8A8_UNORM_SRGB] = 4,
                [TextureFormatG9.B5G5R5A1_UNORM] = 2,
                [TextureFormatG9.R10G10B10A2_UNORM] = 4,
                [TextureFormatG9.R16G16B16A16_UNORM] = 8,
                [TextureFormatG9.R16G16B16A16_FLOAT] = 8,
                [TextureFormatG9.R16_UNORM] = 2,
                [TextureFormatG9.R16_FLOAT] = 2,
                [TextureFormatG9.R8_UNORM] = 1,
                [TextureFormatG9.A8_UNORM] = 1,
                [TextureFormatG9.R32_FLOAT] = 4,
                [TextureFormatG9.R32G32B32A32_FLOAT] = 16,
                [TextureFormatG9.R11G11B10_FLOAT] = 4
            }.ToFrozenDictionary();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint GetBlockStride(TextureFormatG9 f)
        {
            return _blockStrideLookup.GetValueOrDefault<TextureFormatG9, uint>(f, 8);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint GetBlockPixelCount(TextureFormatG9 f)
        {
            return f switch
            {
                TextureFormatG9.BC1_UNORM or
                TextureFormatG9.BC2_UNORM or
                TextureFormatG9.BC3_UNORM or
                TextureFormatG9.BC4_UNORM or
                TextureFormatG9.BC5_UNORM or
                TextureFormatG9.BC6H_UF16 or
                TextureFormatG9.BC7_UNORM or
                TextureFormatG9.BC1_UNORM_SRGB or
                TextureFormatG9.BC2_UNORM_SRGB or
                TextureFormatG9.BC3_UNORM_SRGB or
                TextureFormatG9.BC7_UNORM_SRGB => 4,
                _ => 1
            };
        }

        public static uint GetBlockCount(TextureFormatG9 f, uint width, uint height, uint depth, uint mips, uint flags, uint oldval = 0)
        {
            if (f == TextureFormatG9.UNKNOWN) return 0;

            uint bp = GetBlockPixelCount(f);
            uint bw = width;
            uint bh = height;
            uint bd = depth;

            const uint align = 1u;

            if (mips > 1)
            {
                // Use bit operations for power of 2 calculations
                bw = 1u << (32 - System.Numerics.BitOperations.LeadingZeroCount(width - 1));
                bh = 1u << (32 - System.Numerics.BitOperations.LeadingZeroCount(height - 1));
                bd = 1u << (32 - System.Numerics.BitOperations.LeadingZeroCount(depth - 1));
            }

            uint bc = 0u;
            for (int i = 0; i < mips; i++)
            {
                uint bx = Math.Max(1, (bw + bp - 1) / bp);
                uint by = Math.Max(1, (bh + bp - 1) / bp);

                // Optimized alignment calculation
                bx = (bx + align - 1) & ~(align - 1);
                by = (by + align - 1) & ~(align - 1);

                bc += bx * by * bd;
                bw >>= 1; // Faster than /= 2
                bh >>= 1;
            }

            return bc;
        }

        public override IResourceBlock[] GetReferences()
        {
            var list = new List<IResourceBlock>(1); // Pre-allocate for typical case
            if (!string.IsNullOrEmpty(Name))
            {
                NameBlock = (string_r)Name;
                list.Add(NameBlock);
            }
            return list.ToArray();
        }

        public override string ToString()
        {
            return $"TextureBase: {Name}";
        }
    }

    [TypeConverter(typeof(ExpandableObjectConverter))]
    public class Texture : TextureBase
    {
        public override long BlockLength => 144;
        public override long BlockLength_Gen9 => 128;

        public long MemoryUsage => Data?.FullData?.LongLength ?? 0;

        public override void Read(ResourceDataReader reader, params object[] parameters)
        {
            base.Read(reader, parameters);

            if (reader.IsGen9)
            {
                // Gen9 specific reading
                ulong unk50 = reader.ReadUInt64();//0
                ulong srv58 = reader.ReadUInt64();//SRV embedded at offset 88 (base+8)
                ulong srv60 = reader.ReadUInt64();
                ulong srv68 = reader.ReadUInt64();
                ulong srv70 = reader.ReadUInt64();
                ulong unk78 = reader.ReadUInt64();//0
            }
            else
            {
                ReadLegacyTextureData(reader);
            }
        }

        private void ReadLegacyTextureData(ResourceDataReader reader)
        {
            // read structure data
            this.Width = reader.ReadUInt16();
            this.Height = reader.ReadUInt16();
            this.Depth = reader.ReadUInt16();
            this.Stride = reader.ReadUInt16();
            this.Format = (TextureFormat)reader.ReadUInt32();
            this.Unknown_5Ch = reader.ReadByte();
            this.Levels = reader.ReadByte();
            this.Unknown_5Eh = reader.ReadUInt16();
            this.Unknown_60h = reader.ReadUInt32();
            this.Unknown_64h = reader.ReadUInt32();
            this.Unknown_68h = reader.ReadUInt32();
            this.Unknown_6Ch = reader.ReadUInt32();
            this.DataPointer = reader.ReadUInt64();
            this.Unknown_78h = reader.ReadUInt32();
            this.Unknown_7Ch = reader.ReadUInt32();
            this.Unknown_80h = reader.ReadUInt32();
            this.Unknown_84h = reader.ReadUInt32();
            this.Unknown_88h = reader.ReadUInt32();
            this.Unknown_8Ch = reader.ReadUInt32();

            // read reference data
            this.Data = reader.ReadBlockAt<TextureData>(this.DataPointer, this.Format, this.Width, this.Height, this.Levels, this.Stride);
        }

        public override void Write(ResourceDataWriter writer, params object[] parameters)
        {
            base.Write(writer, parameters);

            if (writer.IsGen9)
            {
                WriteGen9TextureData(writer);
            }
            else
            {
                WriteLegacyTextureData(writer);
            }
        }

        private void WriteGen9TextureData(ResourceDataWriter writer)
        {
            writer.Write(0UL);
            writer.WriteBlock(G9_SRV);//SRV embedded at offset 88 (base+8)
            writer.Write(0UL);
        }

        private void WriteLegacyTextureData(ResourceDataWriter writer)
        {
            this.DataPointer = (ulong)(this.Data?.FilePosition ?? 0);

            // write structure data
            writer.Write(this.Width);
            writer.Write(this.Height);
            writer.Write(this.Depth);
            writer.Write(this.Stride);
            writer.Write((uint)this.Format);
            writer.Write(this.Unknown_5Ch);
            writer.Write(this.Levels);
            writer.Write(this.Unknown_5Eh);
            writer.Write(this.Unknown_60h);
            writer.Write(this.Unknown_64h);
            writer.Write(this.Unknown_68h);
            writer.Write(this.Unknown_6Ch);
            writer.Write(this.DataPointer);
            writer.Write(this.Unknown_78h);
            writer.Write(this.Unknown_7Ch);
            writer.Write(this.Unknown_80h);
            writer.Write(this.Unknown_84h);
            writer.Write(this.Unknown_88h);
            writer.Write(this.Unknown_8Ch);
        }

        public override void WriteXml(StringBuilder sb, int indent, string ddsfolder)
        {
            base.WriteXml(sb, indent, ddsfolder);
            YtdXml.ValueTag(sb, indent, "Width", Width.ToString());
            YtdXml.ValueTag(sb, indent, "Height", Height.ToString());
            YtdXml.ValueTag(sb, indent, "MipLevels", Levels.ToString());
            YtdXml.StringTag(sb, indent, "Format", Format.ToString());
            YtdXml.StringTag(sb, indent, "FileName", YtdXml.XmlEscape($"{Name ?? "null"}.dds"));

            WriteTextureFile(ddsfolder);
        }

        private void WriteTextureFile(string ddsfolder)
        {
            if (string.IsNullOrEmpty(ddsfolder)) return;

            try
            {
                if (!Directory.Exists(ddsfolder))
                {
                    Directory.CreateDirectory(ddsfolder);
                }

                string filepath = Path.Combine(ddsfolder, $"{Name ?? "null"}.dds");
                byte[] dds = DDSIO.GetDDSFile(this);

                // Use async file writing for better performance with large textures
                File.WriteAllBytes(filepath, dds);
            }
            catch { /* Silently ignore file write errors */ }
        }

        public override void ReadXml(XmlNode node, string ddsfolder)
        {
            base.ReadXml(node, ddsfolder);
            Width = (ushort)Xml.GetChildUIntAttribute(node, "Width", "value");
            Height = (ushort)Xml.GetChildUIntAttribute(node, "Height", "value");
            Levels = (byte)Xml.GetChildUIntAttribute(node, "MipLevels", "value");
            Format = Xml.GetChildEnumInnerText<TextureFormat>(node, "Format");
            string filename = Xml.GetChildInnerText(node, "FileName");

            LoadTextureFromFile(filename, ddsfolder);
        }

        private void LoadTextureFromFile(string? filename, string ddsfolder)
        {
            if (string.IsNullOrEmpty(filename) || string.IsNullOrEmpty(ddsfolder)) return;

            string filepath = Path.Combine(ddsfolder, filename);
            if (!File.Exists(filepath))
            {
                throw new FileNotFoundException($"Texture file not found: {filepath}");
            }

            try
            {
                // Read as span and parse via DDSIO
                byte[] dds = File.ReadAllBytes(filepath);
                Texture? tex = DDSIO.GetTexture(dds);

                if (tex is not null)
                {
                    // Copy texture properties
                    (Data, Width, Height, Depth, Levels, Format, Stride) =
                        (tex.Data, tex.Width, tex.Height, tex.Depth, tex.Levels, tex.Format, tex.Stride);
                }
            }
            catch
            {
                throw new NotSupportedException($"Texture file format not supported: {filepath}");
            }
        }

        public override IResourceBlock[] GetReferences()
        {
            var baseRefs = base.GetReferences();
            if (Data is null) return baseRefs;

            var list = new List<IResourceBlock>(baseRefs.Length + 1);
            list.AddRange(baseRefs);
            list.Add(Data);
            return list.ToArray();
        }

        public override Tuple<long, IResourceBlock>[] GetParts()
        {
            if (G9_SRV is not null) // G9 only
            {
                return new Tuple<long, IResourceBlock>[] {
                    new(88, G9_SRV)
                };
            }
            return base.GetParts();
        }

        public override string ToString()
        {
            return $"Texture: {Width}x{Height}: {Name}";
        }
    }

    [TypeConverter(typeof(ExpandableObjectConverter))]
    public class TextureData : ResourceGraphicsBlock
    {
        public override long BlockLength => FullData?.Length ?? 0;

        public byte[]? FullData { get; set; }

        /// <summary>
        /// Reads the data-block from a stream.
        /// </summary>
        public override void Read(ResourceDataReader reader, params object[] parameters)
        {
            int fullLength = reader.IsGen9 ?
                Convert.ToInt32(parameters[0]) :
                CalculateLegacyDataLength(parameters);

            if (fullLength > 0)
            {
                FullData = reader.ReadBytes(fullLength);
            }
        }

        private static int CalculateLegacyDataLength(object[] parameters)
        {
            uint format = Convert.ToUInt32(parameters[0]);
            int width = Convert.ToInt32(parameters[1]);
            int height = Convert.ToInt32(parameters[2]);
            int levels = Convert.ToInt32(parameters[3]);
            int stride = Convert.ToInt32(parameters[4]);

            // Legacy content uses stored stride; keep original behavior for safety
            int fullLength = 0;
            int length = stride * height;

            for (int i = 0; i < levels; i++)
            {
                fullLength += length;
                length >>= 2; // Faster than /= 4
            }

            return fullLength;
        }

        /// <summary>
        /// Writes the data-block to a stream.
        /// </summary>
        public override void Write(ResourceDataWriter writer, params object[] parameters)
        {
            if (FullData is not null)
            {
                writer.Write(FullData);
            }
        }
    }

    // Enums remain the same but with improved formatting
    public enum TextureFormat : uint
    {
        D3DFMT_A8R8G8B8 = 21,
        D3DFMT_X8R8G8B8 = 22,
        D3DFMT_A1R5G5B5 = 25,
        D3DFMT_A8 = 28,
        D3DFMT_A8B8G8R8 = 32,
        D3DFMT_L8 = 50,

        // fourCC
        D3DFMT_DXT1 = 0x31545844,
        D3DFMT_DXT3 = 0x33545844,
        D3DFMT_DXT5 = 0x35545844,
        D3DFMT_ATI1 = 0x31495441,
        D3DFMT_ATI2 = 0x32495441,
        D3DFMT_BC7 = 0x20374342,
    }

    public enum TextureFormatG9 : uint
    {
        UNKNOWN = 0x0,
        R32G32B32A32_TYPELESS = 0x1,
        R32G32B32A32_FLOAT = 0x2,
        R32G32B32A32_UINT = 0x3,
        R32G32B32A32_SINT = 0x4,
        R32G32B32_TYPELESS = 0x5,
        R32G32B32_FLOAT = 0x6,
        R32G32B32_UINT = 0x7,
        R32G32B32_SINT = 0x8,
        R16G16B16A16_TYPELESS = 0x9,
        R16G16B16A16_FLOAT = 0xA,
        R16G16B16A16_UNORM = 0xB,
        R16G16B16A16_UINT = 0xC,
        R16G16B16A16_SNORM = 0xD,
        R16G16B16A16_SINT = 0xE,
        R32G32_TYPELESS = 0xF,
        R32G32_FLOAT = 0x10,
        R32G32_UINT = 0x11,
        R32G32_SINT = 0x12,
        D32_FLOAT_S8X24_UINT = 0x14,
        B10G10R10A2_UNORM = 0x15,
        R10G10B10A2_SNORM = 0x16,
        R10G10B10A2_TYPELESS = 0x17,
        R10G10B10A2_UNORM = 0x18,
        R10G10B10A2_UINT = 0x19,
        R11G11B10_FLOAT = 0x1A,
        R8G8B8A8_TYPELESS = 0x1B,
        R8G8B8A8_UNORM = 0x1C,
        R8G8B8A8_UNORM_SRGB = 0x1D,
        R8G8B8A8_UINT = 0x1E,
        R8G8B8A8_SNORM = 0x1F,
        R8G8B8A8_SINT = 0x20,
        R16G16_TYPELESS = 0x21,
        R16G16_FLOAT = 0x22,
        R16G16_UNORM = 0x23,
        R16G16_UINT = 0x24,
        R16G16_SNORM = 0x25,
        R16G16_SINT = 0x26,
        R32_TYPELESS = 0x27,
        D32_FLOAT = 0x28,
        R32_FLOAT = 0x29,
        R32_UINT = 0x2A,
        R32_SINT = 0x2B,
        R8G8_TYPELESS = 0x30,
        R8G8_UNORM = 0x31,
        R8G8_UINT = 0x32,
        R8G8_SNORM = 0x33,
        R8G8_SINT = 0x34,
        R16_TYPELESS = 0x35,
        R16_FLOAT = 0x36,
        D16_UNORM = 0x37,
        R16_UNORM = 0x38,
        R16_UINT = 0x39,
        R16_SNORM = 0x3A,
        R16_SINT = 0x3B,
        R8_TYPELESS = 0x3C,
        R8_UNORM = 0x3D,
        R8_UINT = 0x3E,
        R8_SNORM = 0x3F,
        R8_SINT = 0x40,
        A8_UNORM = 0x41,
        R9G9B9E5_SHAREDEXP = 0x43,
        BC1_TYPELESS = 0x46,
        BC1_UNORM = 0x47,
        BC1_UNORM_SRGB = 0x48,
        BC2_TYPELESS = 0x49,
        BC2_UNORM = 0x4A,
        BC2_UNORM_SRGB = 0x4B,
        BC3_TYPELESS = 0x4C,
        BC3_UNORM = 0x4D,
        BC3_UNORM_SRGB = 0x4E,
        BC4_TYPELESS = 0x4F,
        BC4_UNORM = 0x50,
        BC4_SNORM = 0x51,
        BC5_TYPELESS = 0x52,
        BC5_UNORM = 0x53,
        BC5_SNORM = 0x54,
        B5G6R5_UNORM = 0x55,
        B5G5R5A1_UNORM = 0x56,
        B8G8R8A8_UNORM = 0x57,
        B8G8R8A8_TYPELESS = 0x5A,
        B8G8R8A8_UNORM_SRGB = 0x5B,
        BC6H_TYPELESS = 0x5E,
        BC6H_UF16 = 0x5F,
        BC6H_SF16 = 0x60,
        BC7_TYPELESS = 0x61,
        BC7_UNORM = 0x62,
        BC7_UNORM_SRGB = 0x63,
        NV12 = 0x67,
        B4G4R4A4_UNORM = 0x73,
        D16_UNORM_S8_UINT = 0x76,
        R16_UNORM_X8_TYPELESS = 0x77,
        X16_TYPELESS_G8_UINT = 0x78,
        ETC1 = 0x79,
        ETC1_SRGB = 0x7A,
        ETC1A = 0x7B,
        ETC1A_SRGB = 0x7C,
        R4G4_UNORM = 0x7F,
    }

    public enum TextureUsage : byte
    {
        UNKNOWN = 0,
        DEFAULT = 1,
        TERRAIN = 2,
        CLOUDDENSITY = 3,
        CLOUDNORMAL = 4,
        CABLE = 5,
        FENCE = 6,
        ENVEFF = 7,
        SCRIPT = 8,
        WATERFLOW = 9,
        WATERFOAM = 10,
        WATERFOG = 11,
        WATEROCEAN = 12,
        WATER = 13,
        FOAMOPACITY = 14,
        FOAM = 15,
        DIFFUSEMIPSHARPEN = 16,
        DIFFUSEDETAIL = 17,
        DIFFUSEDARK = 18,
        DIFFUSEALPHAOPAQUE = 19,
        DIFFUSE = 20,
        DETAIL = 21,
        NORMAL = 22,
        SPECULAR = 23,
        EMISSIVE = 24,
        TINTPALETTE = 25,
        SKIPPROCESSING = 26,
        DONOTOPTIMIZE = 27,
        TEST = 28,
        COUNT = 29,
    }

    [Flags]
    public enum TextureUsageFlags : uint
    {
        NOT_HALF = 1,
        HD_SPLIT = 1 << 1,
        X2 = 1 << 2,
        X4 = 1 << 3,
        Y4 = 1 << 4,
        X8 = 1 << 5,
        X16 = 1 << 6,
        X32 = 1 << 7,
        X64 = 1 << 8,
        Y64 = 1 << 9,
        X128 = 1 << 10,
        X256 = 1 << 11,
        X512 = 1 << 12,
        Y512 = 1 << 13,
        X1024 = 1 << 14,
        Y1024 = 1 << 15,
        X2048 = 1 << 16,
        Y2048 = 1 << 17,
        EMBEDDEDSCRIPTRT = 1 << 18,
        UNK19 = 1 << 19,
        UNK20 = 1 << 20,
        UNK21 = 1 << 21,
        FLAG_FULL = 1 << 22,
        MAPS_HALF = 1 << 23,
        UNK24 = 1 << 24,
    }

    public enum TextureDimensionG9 : byte
    {
        Texture2D = 1,
        TextureCube = 2,
        Texture3D = 3,
    }

    public enum TextureTileModeG9 : byte
    {
        Depth = 4,
        Linear = 8,
        Display = 10,
        Standard = 13,
        RenderTarget = 14,
        VolumeStandard = 19,
        VolumeRenderTarget = 20,
        Auto = 255,
    }

    public enum ShaderResourceViewDimensionG9 : ushort
    {
        Texture2D = 0x41,
        Texture2DArray = 0x61,
        TextureCube = 0x82,
        Texture3D = 0xa3,
        Buffer = 0x14,
    }

    public class ShaderResourceViewG9 : ResourceSystemBlock
    {
        public override long BlockLength => 32;

        public ulong VFT { get; set; } = 0x00000001406b77d8;
        public ulong Unknown_08h { get; set; }
        public ShaderResourceViewDimensionG9 Dimension { get; set; }
        public ushort Unknown_12h { get; set; } = 0xFFFF;
        public uint Unknown_14h { get; set; } = 0xFFFFFFFF;
        public ulong Unknown_18h { get; set; }

        public override void Read(ResourceDataReader reader, params object[] parameters)
        {
            VFT = reader.ReadUInt64();
            Unknown_08h = reader.ReadUInt64();
            Dimension = (ShaderResourceViewDimensionG9)reader.ReadUInt16();
            Unknown_12h = reader.ReadUInt16();
            Unknown_14h = reader.ReadUInt32();
            Unknown_18h = reader.ReadUInt64();
        }

        public override void Write(ResourceDataWriter writer, params object[] parameters)
        {
            writer.Write(VFT);
            writer.Write(Unknown_08h);
            writer.Write((ushort)Dimension);
            writer.Write(Unknown_12h);
            writer.Write(Unknown_14h);
            writer.Write(Unknown_18h);
        }
    }
}