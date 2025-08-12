/*
	Copyright(c) 2015 Neodymium

	Permission is hereby granted, free of charge, to any person obtaining a copy
	of this software and associated documentation files (the "Software"), to deal
	in the Software without restriction, including without limitation the rights
	to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
	copies of the Software, and to permit persons to whom the Software is
	furnished to do so, subject to the following conditions:

	The above copyright notice and this permission notice shall be included in
	all copies or substantial portions of the Software.

	THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
	IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
	FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
	AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
	LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
	OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
	THE SOFTWARE.
*/

using CodeWalker.GameFiles;
using DirectXTexNet;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace CodeWalker.Utils
{
    public static class DDSIO
    {
        // Helper to extract pixel data from a DirectXTexNet image
        private static byte[] ExtractPixels(DirectXTexNet.Image img)
        {
            var pixels = new byte[img.SlicePitch];
            Marshal.Copy(img.Pixels, pixels, 0, (int)img.SlicePitch);
            return pixels;
        }

        // Cache the DirectXTex instance
        public static class DXTex
        {
            // Helper method to compute pitch for a given format
            public static void ComputePitch(DXGI_FORMAT format, int width, int height, out long rowPitch, out long slicePitch, CP_FLAGS flags = CP_FLAGS.NONE)
            {
                TexHelper.Instance.ComputePitch(format, width, height, out rowPitch, out slicePitch, flags);
            }
        }

        /// Gets pixel data from a texture at the specified mip level, decompressing if necessary
        /// Always returns pixels in R8G8B8A8 (RGBA) order suitable for display
        public static unsafe byte[] GetPixels(Texture texture, int mip)
        {
            if (texture?.Data?.FullData == null)
                throw new ArgumentException("Texture or texture data is null");

            var format = GetDXGIFormat(texture.Format);

            // Calculate how many mips actually fit in FullData
            int maxPossibleMips = texture.Levels;
            int dataLen = texture.Data.FullData.Length;
            int width = texture.Width;
            int height = texture.Height;
            int depth = texture.Depth;
            // Check for mip 0 size
            DXTex.ComputePitch(format, width, height, out long rowPitch0, out long slicePitch0);
            int mip0Size = (int)(slicePitch0 * depth);
            if (dataLen < mip0Size)
                throw new Exception($"Texture data is too small for even the base mip level (expected at least {mip0Size} bytes, got {dataLen}). The texture is corrupted or incomplete.");

            int actualMipCount = 0;
            int offset = 0;
            for (int m = 0; m < maxPossibleMips; m++)
            {
                DXTex.ComputePitch(format, width, height, out long rowPitch, out long slicePitch);
                int mipSize = (int)(slicePitch * depth);
                if (offset + mipSize > dataLen) break;
                offset += mipSize;
                actualMipCount++;
                width = Math.Max(1, width / 2);
                height = Math.Max(1, height / 2);
            }
            int clampedMipCount = Math.Min(texture.Levels, actualMipCount);

            // Create a full DDS in memory for only the available mips
            using var stream = new MemoryStream();
            WriteDDSHeader(stream, format, texture.Width, texture.Height, clampedMipCount, texture.Depth);
            stream.Write(texture.Data.FullData, 0, offset);
            var ddsBytes = stream.ToArray();
            
            // Fast path: for simple 2D uncompressed 32-bit formats, directly slice mip bytes
            if ((format == DXGI_FORMAT.R8G8B8A8_UNORM || format == DXGI_FORMAT.R8G8B8A8_UNORM_SRGB ||
                 format == DXGI_FORMAT.B8G8R8A8_UNORM || format == DXGI_FORMAT.B8G8R8A8_UNORM_SRGB)
                && texture.Depth == 1)
            {
                int tWidth = texture.Width;
                int tHeight = texture.Height;
                int mcount = clampedMipCount;
                int safeMip = Math.Min(Math.Max(mip, 0), mcount - 1);
                int offToMip = 0;
                for (int m = 0; m < safeMip; m++)
                {
                    DXTex.ComputePitch(format, tWidth, tHeight, out _, out long sp, 0);
                    offToMip += (int)sp;
                    tWidth = Math.Max(1, tWidth / 2);
                    tHeight = Math.Max(1, tHeight / 2);
                }
                DXTex.ComputePitch(format, tWidth, tHeight, out _, out long targetSlicePitch, 0);
                int countBytes = (int)targetSlicePitch;
                if (offToMip + countBytes <= texture.Data.FullData.Length)
                {
                    var slice = new byte[countBytes];
                    Buffer.BlockCopy(texture.Data.FullData, offToMip, slice, 0, countBytes);
                    if (format == DXGI_FORMAT.B8G8R8A8_UNORM || format == DXGI_FORMAT.B8G8R8A8_UNORM_SRGB)
                    {
                        for (int i = 0; i + 3 < slice.Length; i += 4)
                        {
                            byte b = slice[i];
                            byte r = slice[i + 2];
                            slice[i] = r;
                            slice[i + 2] = b;
                        }
                    }
                    return slice;
                }
            }

            fixed (byte* ptr = ddsBytes)
            {
                using var image = TexHelper.Instance.LoadFromDDSMemory((IntPtr)ptr, ddsBytes.Length, DDS_FLAGS.NONE);
                int mipCount = (int)image.GetMetadata().MipLevels;
                int safeMip = Math.Min(Math.Max(mip, 0), mipCount - 1);
                // Fast path for 32-bit formats: convert BGRA->RGBA if needed, otherwise copy
                if (format == DXGI_FORMAT.R8G8B8A8_UNORM || format == DXGI_FORMAT.R8G8B8A8_UNORM_SRGB)
                {
                    return ExtractPixels(image.GetImage(safeMip));
                }
                if (format == DXGI_FORMAT.B8G8R8A8_UNORM || format == DXGI_FORMAT.B8G8R8A8_UNORM_SRGB)
                {
                    var p = ExtractPixels(image.GetImage(safeMip));
                    // Convert BGRA to RGBA for consumers
                    for (int i = 0; i + 3 < p.Length; i += 4)
                    {
                        byte b = p[i];
                        byte r = p[i + 2];
                        p[i] = r;
                        p[i + 2] = b;
                    }
                    return p;
                }
                if (IsCompressedFormat(format))
                {
                    using var decompressed = image.Decompress(DXGI_FORMAT.R8G8B8A8_UNORM);
                    int decMipCount = (int)decompressed.GetMetadata().MipLevels;
                    int decSafeMip = Math.Min(Math.Max(mip, 0), decMipCount - 1);
                    return ExtractPixels(decompressed.GetImage(decSafeMip));
                }
                using var converted = image.Convert(DXGI_FORMAT.R8G8B8A8_UNORM, TEX_FILTER_FLAGS.DEFAULT, 0.5f);
                int convMipCount = (int)converted.GetMetadata().MipLevels;
                int convSafeMip = Math.Min(Math.Max(mip, 0), convMipCount - 1);
                return ExtractPixels(converted.GetImage(convSafeMip));
            }
        }

        /// Gets pixel data suitable for GDI+ Bitmap (BGRA order)
        public static unsafe byte[] GetPixelsBGRA(Texture texture, int mip)
        {
            if (texture?.Data?.FullData == null)
                throw new ArgumentException("Texture or texture data is null");

            var format = GetDXGIFormat(texture.Format);

            // Calculate how many mips actually fit in FullData
            int maxPossibleMips = texture.Levels;
            int dataLen = texture.Data.FullData.Length;
            int width = texture.Width;
            int height = texture.Height;
            int depth = texture.Depth;
            DXTex.ComputePitch(format, width, height, out long rowPitch0, out long slicePitch0);
            int mip0Size = (int)(slicePitch0 * depth);
            if (dataLen < mip0Size)
                throw new Exception($"Texture data is too small for even the base mip level (expected at least {mip0Size} bytes, got {dataLen}). The texture is corrupted or incomplete.");

            int actualMipCount = 0;
            int offset = 0;
            for (int m = 0; m < maxPossibleMips; m++)
            {
                DXTex.ComputePitch(format, width, height, out long rowPitch, out long slicePitch);
                int mipSize = (int)(slicePitch * depth);
                if (offset + mipSize > dataLen) break;
                offset += mipSize;
                actualMipCount++;
                width = Math.Max(1, width / 2);
                height = Math.Max(1, height / 2);
            }
            int clampedMipCount = Math.Min(texture.Levels, actualMipCount);

            // Create a full DDS in memory for only the available mips
            using var stream = new MemoryStream();
            WriteDDSHeader(stream, format, texture.Width, texture.Height, clampedMipCount, texture.Depth);
            stream.Write(texture.Data.FullData, 0, offset);
            var ddsBytes = stream.ToArray();

            // Fast path for 32-bit formats to avoid conversions
            if ((format == DXGI_FORMAT.B8G8R8A8_UNORM || format == DXGI_FORMAT.B8G8R8A8_UNORM_SRGB) && texture.Depth == 1)
            {
                int tWidth = texture.Width;
                int tHeight = texture.Height;
                int mcount = clampedMipCount;
                int safeMip = Math.Min(Math.Max(mip, 0), mcount - 1);
                int offToMip = 0;
                for (int m = 0; m < safeMip; m++)
                {
                    DXTex.ComputePitch(format, tWidth, tHeight, out _, out long sp, 0);
                    offToMip += (int)sp;
                    tWidth = Math.Max(1, tWidth / 2);
                    tHeight = Math.Max(1, tHeight / 2);
                }
                DXTex.ComputePitch(format, tWidth, tHeight, out _, out long targetSlicePitch, 0);
                int countBytes = (int)targetSlicePitch;
                if (offToMip + countBytes <= texture.Data.FullData.Length)
                {
                    var slice = new byte[countBytes];
                    Buffer.BlockCopy(texture.Data.FullData, offToMip, slice, 0, countBytes);
                    return slice;
                }
            }

            fixed (byte* ptr = ddsBytes)
            {
                using var image = TexHelper.Instance.LoadFromDDSMemory((IntPtr)ptr, ddsBytes.Length, DDS_FLAGS.NONE);
                int mipCount = (int)image.GetMetadata().MipLevels;
                int safeMip = Math.Min(Math.Max(mip, 0), mipCount - 1);
                if (format == DXGI_FORMAT.B8G8R8A8_UNORM || format == DXGI_FORMAT.B8G8R8A8_UNORM_SRGB)
                {
                    return ExtractPixels(image.GetImage(safeMip));
                }
                if (format == DXGI_FORMAT.R8G8B8A8_UNORM || format == DXGI_FORMAT.R8G8B8A8_UNORM_SRGB)
                {
                    var p = ExtractPixels(image.GetImage(safeMip));
                    // Convert RGBA to BGRA
                    for (int i = 0; i + 3 < p.Length; i += 4)
                    {
                        byte r = p[i];
                        byte b = p[i + 2];
                        p[i] = b;
                        p[i + 2] = r;
                    }
                    return p;
                }
                if (IsCompressedFormat(format))
                {
                    using var decompressed = image.Decompress(DXGI_FORMAT.R8G8B8A8_UNORM);
                    int decMipCount = (int)decompressed.GetMetadata().MipLevels;
                    int decSafeMip = Math.Min(Math.Max(mip, 0), decMipCount - 1);
                    var p = ExtractPixels(decompressed.GetImage(decSafeMip));
                    // Convert RGBA to BGRA
                    for (int i = 0; i + 3 < p.Length; i += 4)
                    {
                        byte r = p[i];
                        byte b = p[i + 2];
                        p[i] = b;
                        p[i + 2] = r;
                    }
                    return p;
                }
                using var converted = image.Convert(DXGI_FORMAT.R8G8B8A8_UNORM, TEX_FILTER_FLAGS.DEFAULT, 0.5f);
                int convMipCount = (int)converted.GetMetadata().MipLevels;
                int convSafeMip = Math.Min(Math.Max(mip, 0), convMipCount - 1);
                var p2 = ExtractPixels(converted.GetImage(convSafeMip));
                for (int i = 0; i + 3 < p2.Length; i += 4)
                {
                    byte r = p2[i];
                    byte b = p2[i + 2];
                    p2[i] = b;
                    p2[i + 2] = r;
                }
                return p2;
            }
        }

        /// Creates a DDS file from a texture
        public static unsafe byte[] GetDDSFile(Texture texture)
        {
            if (texture?.Data?.FullData == null)
                throw new ArgumentException("Texture or texture data is null");

            var format = GetDXGIFormat(texture.Format);

            // Cubemap detection (Gen9 or legacy):
            bool isCubemap = false;
            try
            {
                // Try to detect cubemap via G9_Dimension (Gen9)
                var g9DimProp = texture.GetType().GetProperty("G9_Dimension");
                if (g9DimProp != null)
                {
                    var g9Dim = g9DimProp.GetValue(texture, null);
                    if (g9Dim != null && g9Dim.ToString() == "TextureCube")
                        isCubemap = true;
                }
            }
            catch { }

            // Calculate how many mips actually fit in FullData
            int maxPossibleMips = texture.Levels;
            int dataLen = texture.Data.FullData.Length;
            int width = texture.Width;
            int height = texture.Height;
            int depth = texture.Depth;
            int faceCount = isCubemap ? 6 : 1;
            int actualMipCount = 0;
            int offset = 0;
            for (int m = 0; m < maxPossibleMips; m++)
            {
                DXTex.ComputePitch(format, width, height, out long rowPitch, out long slicePitch);
                int mipSize = (int)(slicePitch * depth * faceCount);
                if (offset + mipSize > dataLen) break;
                offset += mipSize;
                actualMipCount++;
                width = Math.Max(1, width / 2);
                height = Math.Max(1, height / 2);
            }
            int clampedMipCount = Math.Min(texture.Levels, actualMipCount);

            using var stream = new MemoryStream();
            WriteDDSHeader(stream, format, texture.Width, texture.Height, clampedMipCount, texture.Depth, isCubemap);
            stream.Write(texture.Data.FullData, 0, offset);
            return stream.ToArray();
        }

        /// Creates a texture from DDS file data
        public static unsafe Texture GetTexture(byte[] ddsfile)
        {
            if (ddsfile == null || ddsfile.Length == 0)
                throw new ArgumentException("DDS file data is null or empty");

            fixed (byte* ptr = ddsfile)
            {
                using var image = TexHelper.Instance.LoadFromDDSMemory((IntPtr)ptr, ddsfile.Length, DDS_FLAGS.NONE);
                var metadata = image.GetMetadata();

                var texture = new Texture
                {
                    Width = (ushort)metadata.Width,
                    Height = (ushort)metadata.Height,
                    Depth = (ushort)Math.Max(1, metadata.Depth),
                    Levels = (byte)metadata.MipLevels,
                    Format = GetTextureFormat(metadata.Format)
                };

                texture.Stride = texture.CalculateStride();

                var fullDataSize = CalculateTextureDataSize(metadata.Format,
                    (int)metadata.Width, (int)metadata.Height,
                    (int)metadata.Depth, (int)metadata.MipLevels);

                var fullData = new byte[fullDataSize];
                int dataOffset = 0;
                int count = Math.Min((int)image.GetImageCount(), (int)metadata.MipLevels);
                for (int i = 0; i < count; i++)
                {
                    var img = image.GetImage(i);
                    int size = (int)img.SlicePitch;
                    if (dataOffset + size > fullData.Length)
                        size = fullData.Length - dataOffset;
                    Marshal.Copy(img.Pixels, fullData, dataOffset, size);
                    dataOffset += size;
                }

                texture.Data = new TextureData { FullData = fullData };
                return texture;
            }
        }

        /// Calculates the total size needed for texture data including all mips
        private static int CalculateTextureDataSize(DXGI_FORMAT format, int width, int height, int depth, int mipLevels)
        {
            long totalSize = 0;

            for (int mip = 0; mip < mipLevels; mip++)
            {
                DXTex.ComputePitch(format, width, height, out _, out long slicePitch);
                totalSize += slicePitch * depth;

                width = Math.Max(1, width / 2);
                height = Math.Max(1, height / 2);
            }

            return (int)totalSize;
        }

        /// Checks if a format is compressed
        private static bool IsCompressedFormat(DXGI_FORMAT format)
        {
            switch (format)
            {
                case DXGI_FORMAT.BC1_TYPELESS:
                case DXGI_FORMAT.BC1_UNORM:
                case DXGI_FORMAT.BC1_UNORM_SRGB:
                case DXGI_FORMAT.BC2_TYPELESS:
                case DXGI_FORMAT.BC2_UNORM:
                case DXGI_FORMAT.BC2_UNORM_SRGB:
                case DXGI_FORMAT.BC3_TYPELESS:
                case DXGI_FORMAT.BC3_UNORM:
                case DXGI_FORMAT.BC3_UNORM_SRGB:
                case DXGI_FORMAT.BC4_TYPELESS:
                case DXGI_FORMAT.BC4_UNORM:
                case DXGI_FORMAT.BC4_SNORM:
                case DXGI_FORMAT.BC5_TYPELESS:
                case DXGI_FORMAT.BC5_UNORM:
                case DXGI_FORMAT.BC5_SNORM:
                case DXGI_FORMAT.BC6H_TYPELESS:
                case DXGI_FORMAT.BC6H_UF16:
                case DXGI_FORMAT.BC6H_SF16:
                case DXGI_FORMAT.BC7_TYPELESS:
                case DXGI_FORMAT.BC7_UNORM:
                case DXGI_FORMAT.BC7_UNORM_SRGB:
                    return true;
                default:
                    return false;
            }
        }

        /// Checks if a format needs DX10 header
        private static bool NeedsDX10Header(DXGI_FORMAT format)
        {
            switch (format)
            {
                case DXGI_FORMAT.BC7_UNORM:
                case DXGI_FORMAT.BC7_UNORM_SRGB:
                case DXGI_FORMAT.BC6H_UF16:
                case DXGI_FORMAT.BC6H_SF16:
                    return true;
                default:
                    return false;
            }
        }

        /// Writes a DDS header to a stream
        private static void WriteDDSHeader(Stream stream, DXGI_FORMAT format, int width, int height, int mipCount, int depth, bool isCubemap = false)
        {
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.Default, true))
            {
                // DDS magic number
                writer.Write(0x20534444); // "DDS "

                // DDS_HEADER
                writer.Write(124); // dwSize

                uint flags = 0x00001007; // DDSD_CAPS | DDSD_HEIGHT | DDSD_WIDTH | DDSD_PIXELFORMAT
                if (mipCount > 1)
                    flags |= 0x00020000; // DDSD_MIPMAPCOUNT
                if (IsCompressedFormat(format))
                    flags |= 0x00080000; // DDSD_LINEARSIZE
                else
                    flags |= 0x00000008; // DDSD_PITCH

                writer.Write(flags);
                writer.Write(height);
                writer.Write(width);

                // Pitch or linear size: always use DXTex.ComputePitch for consistency
                DXTex.ComputePitch(format, width, height, out long rowPitch, out long slicePitch);
                if (IsCompressedFormat(format))
                    writer.Write((int)slicePitch); // linear size for compressed formats
                else
                    writer.Write((int)rowPitch);   // pitch for uncompressed

                writer.Write(depth); // dwDepth
                writer.Write(mipCount); // dwMipMapCount

                // Reserved
                for (int i = 0; i < 11; i++)
                    writer.Write(0);

                // DDS_PIXELFORMAT
                WriteDDSPixelFormat(writer, format);

                // dwCaps
                uint caps = 0x00001000; // DDSCAPS_TEXTURE
                if (mipCount > 1)
                    caps |= 0x00400008; // DDSCAPS_COMPLEX | DDSCAPS_MIPMAP
                if (isCubemap)
                    caps |= 0x00000008; // DDSCAPS_COMPLEX (required for cubemaps)
                writer.Write(caps);

                // dwCaps2
                uint caps2 = 0;
                if (isCubemap)
                {
                    caps2 |= 0x00000200; // DDSCAPS2_CUBEMAP
                    caps2 |= 0x00000FC0; // All cubemap faces (DDSCAPS2_CUBEMAP_POSITIVEX, etc.)
                }
                writer.Write(caps2);
                writer.Write(0); // dwCaps3
                writer.Write(0); // dwCaps4
                writer.Write(0); // dwReserved2

                // Write DX10 header if needed
                if (NeedsDX10Header(format))
                {
                    WriteDX10Header(writer, format, width, height, depth, mipCount, isCubemap);
                }
            }
        }

        /// Writes DDS pixel format structure
        private static void WriteDDSPixelFormat(BinaryWriter writer, DXGI_FORMAT format)
        {
            writer.Write(32); // dwSize

            switch (format)
            {
                case DXGI_FORMAT.BC1_UNORM:
                case DXGI_FORMAT.BC1_UNORM_SRGB:
                    writer.Write(0x00000004); // DDPF_FOURCC
                    writer.Write(0x31545844); // "DXT1"
                    writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
                    break;

                case DXGI_FORMAT.BC2_UNORM:
                case DXGI_FORMAT.BC2_UNORM_SRGB:
                    writer.Write(0x00000004); // DDPF_FOURCC
                    writer.Write(0x33545844); // "DXT3"
                    writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
                    break;

                case DXGI_FORMAT.BC3_UNORM:
                case DXGI_FORMAT.BC3_UNORM_SRGB:
                    writer.Write(0x00000004); // DDPF_FOURCC
                    writer.Write(0x35545844); // "DXT5"
                    writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
                    break;

                case DXGI_FORMAT.BC4_UNORM:
                    writer.Write(0x00000004); // DDPF_FOURCC
                    writer.Write(0x31495441); // "ATI1"
                    writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
                    break;

                case DXGI_FORMAT.BC5_UNORM:
                    writer.Write(0x00000004); // DDPF_FOURCC
                    writer.Write(0x32495441); // "ATI2"
                    writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
                    break;

                case DXGI_FORMAT.BC7_UNORM:
                case DXGI_FORMAT.BC7_UNORM_SRGB:
                    writer.Write(0x00000004); // DDPF_FOURCC
                    writer.Write(0x30315844); // "DX10"
                    writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
                    break;

                case DXGI_FORMAT.R8G8B8A8_UNORM:
                case DXGI_FORMAT.R8G8B8A8_UNORM_SRGB:
                    writer.Write(0x00000041); // DDPF_RGB | DDPF_ALPHAPIXELS
                    writer.Write(0); // dwFourCC
                    writer.Write(32); // dwRGBBitCount
                    writer.Write(0x000000ff); // dwRBitMask
                    writer.Write(0x0000ff00); // dwGBitMask
                    writer.Write(0x00ff0000); // dwBBitMask
                    writer.Write(0xff000000); // dwABitMask
                    break;

                case DXGI_FORMAT.B8G8R8A8_UNORM:
                case DXGI_FORMAT.B8G8R8A8_UNORM_SRGB:
                    writer.Write(0x00000041); // DDPF_RGB | DDPF_ALPHAPIXELS
                    writer.Write(0); // dwFourCC
                    writer.Write(32); // dwRGBBitCount
                    writer.Write(0x00ff0000); // dwRBitMask
                    writer.Write(0x0000ff00); // dwGBitMask
                    writer.Write(0x000000ff); // dwBBitMask
                    writer.Write(0xff000000); // dwABitMask
                    break;

                case DXGI_FORMAT.A8_UNORM:
                    writer.Write(0x00000002); // DDPF_ALPHA
                    writer.Write(0); // dwFourCC
                    writer.Write(8); // dwRGBBitCount
                    writer.Write(0); // dwRBitMask
                    writer.Write(0); // dwGBitMask
                    writer.Write(0); // dwBBitMask
                    writer.Write(0xff); // dwABitMask
                    break;

                case DXGI_FORMAT.R8_UNORM:
                    writer.Write(0x00020000); // DDPF_LUMINANCE
                    writer.Write(0); // dwFourCC
                    writer.Write(8); // dwRGBBitCount
                    writer.Write(0xff); // dwRBitMask
                    writer.Write(0); // dwGBitMask
                    writer.Write(0); // dwBBitMask
                    writer.Write(0); // dwABitMask
                    break;

                case DXGI_FORMAT.B5G5R5A1_UNORM:
                    writer.Write(0x00000041); // DDPF_RGB | DDPF_ALPHAPIXELS
                    writer.Write(0); // dwFourCC
                    writer.Write(16); // dwRGBBitCount
                    writer.Write(0x00007c00); // dwRBitMask
                    writer.Write(0x000003e0); // dwGBitMask
                    writer.Write(0x0000001f); // dwBBitMask
                    writer.Write(0x00008000); // dwABitMask
                    break;

                default:
                    // Default to BGRA8
                    writer.Write(0x00000041); // DDPF_RGB | DDPF_ALPHAPIXELS
                    writer.Write(0); // dwFourCC
                    writer.Write(32); // dwRGBBitCount
                    writer.Write(0x00ff0000); // dwRBitMask
                    writer.Write(0x0000ff00); // dwGBitMask
                    writer.Write(0x000000ff); // dwBBitMask
                    writer.Write(0xff000000); // dwABitMask
                    break;
            }
        }

        /// Writes DX10 header for formats that require it
        private static void WriteDX10Header(BinaryWriter writer, DXGI_FORMAT format, int width, int height, int depth, int mipCount, bool isCubemap = false)
        {
            writer.Write((uint)format); // dxgiFormat
            // resourceDimension: 3 = DDS_DIMENSION_TEXTURE2D, 4 = DDS_DIMENSION_TEXTURE3D, 5 = DDS_DIMENSION_TEXTURECUBE
            writer.Write(3u); // DDS_DIMENSION_TEXTURE2D (DirectXTex expects 3 for 2D/cubemap)
            uint miscFlag = isCubemap ? 0x4u : 0u; // RESOURCE_MISC_TEXTURECUBE
            writer.Write(miscFlag); // miscFlag
            writer.Write(isCubemap ? 6u : 1u); // arraySize
            writer.Write(0u); // miscFlags2
        }

        /// Converts DXGI format to TextureFormat
        public static TextureFormat GetTextureFormat(DXGI_FORMAT format)
        {
            return format switch
            {
                DXGI_FORMAT.R8G8B8A8_UNORM => TextureFormat.D3DFMT_A8B8G8R8,
                DXGI_FORMAT.R8G8B8A8_UNORM_SRGB => TextureFormat.D3DFMT_A8B8G8R8,
                DXGI_FORMAT.B8G8R8A8_UNORM => TextureFormat.D3DFMT_A8R8G8B8,
                DXGI_FORMAT.B8G8R8A8_UNORM_SRGB => TextureFormat.D3DFMT_A8R8G8B8,
                DXGI_FORMAT.B8G8R8X8_UNORM => TextureFormat.D3DFMT_X8R8G8B8,
                DXGI_FORMAT.B5G5R5A1_UNORM => TextureFormat.D3DFMT_A1R5G5B5,
                DXGI_FORMAT.A8_UNORM => TextureFormat.D3DFMT_A8,
                DXGI_FORMAT.R8_UNORM => TextureFormat.D3DFMT_L8,
                DXGI_FORMAT.BC1_UNORM => TextureFormat.D3DFMT_DXT1,
                DXGI_FORMAT.BC1_UNORM_SRGB => TextureFormat.D3DFMT_DXT1,
                DXGI_FORMAT.BC2_UNORM => TextureFormat.D3DFMT_DXT3,
                DXGI_FORMAT.BC2_UNORM_SRGB => TextureFormat.D3DFMT_DXT3,
                DXGI_FORMAT.BC3_UNORM => TextureFormat.D3DFMT_DXT5,
                DXGI_FORMAT.BC3_UNORM_SRGB => TextureFormat.D3DFMT_DXT5,
                DXGI_FORMAT.BC4_UNORM => TextureFormat.D3DFMT_ATI1,
                DXGI_FORMAT.BC5_UNORM => TextureFormat.D3DFMT_ATI2,
                DXGI_FORMAT.BC7_UNORM => TextureFormat.D3DFMT_BC7,
                DXGI_FORMAT.BC7_UNORM_SRGB => TextureFormat.D3DFMT_BC7,
                _ => TextureFormat.D3DFMT_A8R8G8B8 // Default fallback
            };
        }

        /// Converts TextureFormat to DXGI format
        public static DXGI_FORMAT GetDXGIFormat(TextureFormat format)
        {
            return format switch
            {
                TextureFormat.D3DFMT_A8B8G8R8 => DXGI_FORMAT.R8G8B8A8_UNORM,
                TextureFormat.D3DFMT_A8R8G8B8 => DXGI_FORMAT.B8G8R8A8_UNORM,
                TextureFormat.D3DFMT_X8R8G8B8 => DXGI_FORMAT.B8G8R8X8_UNORM,
                TextureFormat.D3DFMT_A1R5G5B5 => DXGI_FORMAT.B5G5R5A1_UNORM,
                TextureFormat.D3DFMT_A8 => DXGI_FORMAT.A8_UNORM,
                TextureFormat.D3DFMT_L8 => DXGI_FORMAT.R8_UNORM,
                TextureFormat.D3DFMT_DXT1 => DXGI_FORMAT.BC1_UNORM,
                TextureFormat.D3DFMT_DXT3 => DXGI_FORMAT.BC2_UNORM,
                TextureFormat.D3DFMT_DXT5 => DXGI_FORMAT.BC3_UNORM,
                TextureFormat.D3DFMT_ATI1 => DXGI_FORMAT.BC4_UNORM,
                TextureFormat.D3DFMT_ATI2 => DXGI_FORMAT.BC5_UNORM,
                TextureFormat.D3DFMT_BC7 => DXGI_FORMAT.BC7_UNORM,
                _ => DXGI_FORMAT.B8G8R8A8_UNORM // Default fallback
            };
        }
    }
}