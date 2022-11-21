using Ryujinx.Graphics.Texture.Utils;
using System;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Threading.Tasks;

namespace Ryujinx.Graphics.Texture.Encoders
{
    static class BC1Encoder
    {
        private static volatile int totalTextures0Alpha = 0;
        private static volatile int totalTexturesSemiAlpha = 0;
        private static volatile int totalTexturesBC1Alpha = 0;

        public static void Encode(Memory<byte> outputStorage, ReadOnlyMemory<byte> data, int width, int height, EncodeMode mode)
        {
            int widthInBlocks = (width + 3) / 4;
            int heightInBlocks = (height + 3) / 4;



            if (!mode.HasFlag(EncodeMode.Multithreaded))
            {
                Parallel.For(0, heightInBlocks, (yInBlocks) =>
                {
                    int y = yInBlocks * 4;
                    var output = outputStorage.Span;
                    for (int xInBlocks = 0; xInBlocks < widthInBlocks; xInBlocks++)
                    {
                        int x = xInBlocks * 4;
                        int offset = (yInBlocks * widthInBlocks + xInBlocks);
                        var block = output.Slice(offset * 8);
                        //CompressBlockFirstIMP(data.Span, x, y, width, height, block);
                        CompressBlock(data.Span, x, y, width, height, block);
                    }
                });
            }
            else
            {
                int offset = 0;
                var output = outputStorage.Span;

                for (int y = 0; y < height; y += 4)
                {
                    for (int x = 0; x < width; x += 4)
                    {
                        var block = output.Slice(offset * 8);
                        //CompressBlockFirstIMP(data.Span, x, y, width, height, block);
                        CompressBlock(data.Span, x, y, width, height, block);
                        offset++;
                    }
                }
            }
        }


        private static void CompressBlockFirstIMP(ReadOnlySpan<byte> data, int x, int y, int width, int height, Span<byte> outputBlock)
        {
            int w = Math.Min(4, width - x);
            int h = Math.Min(4, height - y);

            var dataUint = MemoryMarshal.Cast<byte, uint>(data);

            int baseOffset = y * width + x;

            RawBlock4X4Rgba32 block = default;
            var tile = block.asUintSpan;

            for (int ty = 0; ty < h; ty++)
            {
                int rowOffset = baseOffset + ty * width;

                for (int tx = 0; tx < w; tx++)
                {
                    tile[ty * w + tx] = dataUint[rowOffset + tx];
                }
            }


            (RgbaColor8 minColor, RgbaColor8 maxColor) = BC67Utils.GetMinMaxColors(tile, w, h);

            var MaxColor565 = (ushort)(((maxColor.R >> 3) << 11) | ((maxColor.G >> 2) << 5) | (maxColor.B >> 3));
            var MinColor565 = (ushort)(((minColor.R >> 3) << 11) | ((minColor.G >> 2) << 5) | (minColor.B >> 3));

            var ColorsC0c1 = MemoryMarshal.Cast<byte, ushort>(outputBlock);
            ColorsC0c1[1] = MinColor565;
            ColorsC0c1[0] = MaxColor565;

            if ((minColor.A < 255 && maxColor.A > 0) || (maxColor.A < 255 && minColor.A > 0))
                totalTexturesSemiAlpha++;//ir para BC7
            else if(minColor.A == 0 || maxColor.A == 0)
                totalTexturesBC1Alpha++;//need impl.....
            else totalTextures0Alpha++;//Bc1 ok

            const int C565_5_MASK = 0xF8;
            const int C565_6_MASK = 0xFC;//FC ou FB? não me lembro!!!!!!

            RgbaColor32[] colors = new RgbaColor32[4];
            var indices = MemoryMarshal.Cast<byte, int>(outputBlock.Slice(4))[0];
            indices = 0;

            colors[0].R = (maxColor.R & C565_5_MASK) | (maxColor.R >> 5);
            colors[0].G = (maxColor.G & C565_6_MASK) | (maxColor.G >> 6);
            colors[0].B = (maxColor.B & C565_5_MASK) | (maxColor.B >> 5);
            colors[1].R = (minColor.R & C565_5_MASK) | (minColor.R >> 5);
            colors[1].G = (minColor.G & C565_6_MASK) | (minColor.G >> 6);
            colors[1].B = (minColor.B & C565_5_MASK) | (minColor.B >> 5);

            // 1/2 for alpha ...i think
            colors[2].R = (2 * colors[0].R + 1 * colors[1].R) / 3;
            colors[2].G = (2 * colors[0].G + 1 * colors[1].G) / 3;
            colors[2].B = (2 * colors[0].B + 1 * colors[1].B) / 3;
            colors[3].R = (1 * colors[0].R + 2 * colors[1].R) / 3;
            colors[3].G = (1 * colors[0].G + 2 * colors[1].G) / 3;
            colors[3].B = (1 * colors[0].B + 2 * colors[1].B) / 3;

            var colorBlock = MemoryMarshal.Cast<uint, RgbaColor8>(tile);

            //
            for (int i = 15; i >= 0; i--)
            {
                int c0 = colorBlock[i].R;
                int c1 = colorBlock[i].G;
                int c2 = colorBlock[i].B;
                int d0 = Math.Abs(colors[0].R - c0) + Math.Abs(colors[0].G - c1) + Math.Abs(colors[0].B - c2);
                int d1 = Math.Abs(colors[1].R - c0) + Math.Abs(colors[1].G - c1) + Math.Abs(colors[1].B - c2);
                int d2 = Math.Abs(colors[2].R - c0) + Math.Abs(colors[2].G - c1) + Math.Abs(colors[2].B - c2);
                int d3 = Math.Abs(colors[3].R - c0) + Math.Abs(colors[3].G - c1) + Math.Abs(colors[3].B - c2);

                int b0 = d0 > d3 ? 1 : 0;
                int b1 = d1 > d2 ? 1 : 0;
                int b2 = d0 > d2 ? 1 : 0;
                int b3 = d1 > d3 ? 1 : 0;
                int b4 = d2 > d3 ? 1 : 0;

                int x0 = b1 & b2;
                int x1 = b0 & b3;
                int x2 = b0 & b4;
                indices |= (x2 | ((x0 | x1) << 1)) << (i << 1);
            }

            //maybe another recast?!
            outputBlock[4] = (byte)((indices >> 0) & 255);
            outputBlock[5] = (byte)((indices >> 8) & 255);
            outputBlock[6] = (byte)((indices >> 16) & 255);
            outputBlock[7] = (byte)((indices >> 24) & 255);

            //TODO
            /*
             sometimes there is some big artefacs/blocks on bayonetta face, maybe some decal/Ligths?
            is best and fast try a working project to be sure that i'm not forgeting any formula here.
            parabens idiota, fechou o pr por não saber usar o git;melhor assim, pelo menos não me sinto mal de ver 2GB toda hora e me sentir mal por não poder ter algo melhor.
             */

        }


        private static void CompressBlock(ReadOnlySpan<byte> data, int x, int y, int width, int height, Span<byte> outputBlock)
        {
            int w = Math.Min(4, width - x);
            int h = Math.Min(4, height - y);

            var dataUint = MemoryMarshal.Cast<byte, uint>(data);

            int baseOffset = y * width + x;

            RawBlock4X4Rgba32 block = default;
            var tile = block.asUintSpan;
            //something very WRONG HERE!!!
            /*if (Sse41.IsSupported && w == 4 && h == 4)
            {
                Span<Vector128<byte>> tileVec = MemoryMarshal.Cast<uint, Vector128<byte>>(tile);
                unsafe
                {
                    fixed (uint* pData = dataUint.Slice(baseOffset, w * h))
                    {
                        tileVec[0] = Sse2.LoadVector128(pData).AsByte();
                        tileVec[1] = Sse2.LoadVector128(pData + 4).AsByte();
                        tileVec[2] = Sse2.LoadVector128(pData + 8).AsByte();
                        tileVec[3] = Sse2.LoadVector128(pData + 12).AsByte();
                    }
                }

            }
            else
            {*/
            for (int ty = 0; ty < h; ty++)
            {
                int rowOffset = baseOffset + ty * width;

                for (int tx = 0; tx < w; tx++)
                {
                    tile[ty * w + tx] = dataUint[rowOffset + tx];
                }
            }
            //}

            var datablock = MemoryMarshal.Cast<byte, Bc1Block>(outputBlock);
            datablock[0] = BlockEncodeFastAttemply4(block);
        }

        //trying out some implementation that won't come from my empty head....
        //Everything below come from https://github.com/Nominom/BCnEncoder.NET with small modfications

        static int ChooseClosestColor4(ReadOnlySpan<ColorRgb24> colors, ColorRgba32 color, float rWeight, float gWeight, float bWeight, out float error)
        {
            //i never used a color Weight before, so...
            //Comment this line below to use BCnEncoder.NET implementation
            rWeight = gWeight = bWeight = 1f;

            ReadOnlySpan<float> d = stackalloc float[4]
                {
                MathF.Abs(colors[0].r - color.R) * rWeight
                + MathF.Abs(colors[0].g - color.G) * gWeight
                + MathF.Abs(colors[0].b - color.B) * bWeight,

                MathF.Abs(colors[1].r - color.R) * rWeight
                + MathF.Abs(colors[1].g - color.G) * gWeight
                + MathF.Abs(colors[1].b - color.B) * bWeight,

                MathF.Abs(colors[2].r - color.R) * rWeight
                + MathF.Abs(colors[2].g - color.G) * gWeight
                + MathF.Abs(colors[2].b - color.B) * bWeight,

                MathF.Abs(colors[3].r - color.R) * rWeight
                + MathF.Abs(colors[3].g - color.G) * gWeight
                + MathF.Abs(colors[3].b - color.B) * bWeight,
            };

            var b0 = d[0] > d[3] ? 1 : 0;
            var b1 = d[1] > d[2] ? 1 : 0;
            var b2 = d[0] > d[2] ? 1 : 0;
            var b3 = d[1] > d[3] ? 1 : 0;
            var b4 = d[2] > d[3] ? 1 : 0;

            var x0 = b1 & b2;
            var x1 = b0 & b3;
            var x2 = b0 & b4;

            var idx = x2 | ((x0 | x1) << 1);
            error = d[idx];
            return idx;
        }
        private static Bc1Block TryColors(RawBlock4X4Rgba32 rawBlock, ColorRgb565 color0, ColorRgb565 color1, out float error)
        {

            float rWeight = 0.3f;
            float gWeight = 0.6f;
            float bWeight = 0.1f;

            var output = new Bc1Block();

            var pixels = rawBlock.asPixelSpan;

            output.color0 = color0;
            output.color1 = color1;

            var c0 = color0.ToColorRgb24();
            var c1 = color1.ToColorRgb24();

            ReadOnlySpan<ColorRgb24> colors = output.HasAlphaOrBlack ?
                stackalloc ColorRgb24[] {
                c0,
                c1,
                c0.InterpolateHalf(c1),
                new ColorRgb24(0, 0, 0)
            } : stackalloc ColorRgb24[] {
                c0,
                c1,
                c0.InterpolateThird(c1, 1),
                c0.InterpolateThird(c1, 2)
            };

            error = 0;
            for (var i = 0; i < 16; i++)
            {
                var color = pixels[i];
                output[i] = ChooseClosestColor4(colors, color, rWeight, gWeight, bWeight, out var e);
                error += e;
            }

            return output;
        }

        private static Bc1Block BlockEncodeFastAttemply4(RawBlock4X4Rgba32 rawBlock)
        {
            var output = new Bc1Block();

            var pixels = rawBlock.asPixelSpan;

            var hasAlpha = rawBlock.HasTransparentPixels();


            const int colorInsetShift = 4;
            const int c5655Mask = 0xF8;
            const int c5656Mask = 0xFC;

            int minR = 255,
                minG = 255,
                minB = 255;
            int maxR = 0,
                maxG = 0,
                maxB = 0;

            //keeping it only for reference/debug propouses...we have BC67Utils.GetMinMaxColors... 
            for (var i = 0; i < pixels.Length; i++)
            {
                var c = pixels[i];
                if (c.R < minR) minR = c.R;
                if (c.G < minG) minG = c.G;
                if (c.B < minB) minB = c.B;

                if (c.R > maxR) maxR = c.R;
                if (c.G > maxG) maxG = c.G;
                if (c.B > maxB) maxB = c.B;
            }

            var insetR = (maxR - minR) >> colorInsetShift;
            var insetG = (maxG - minG) >> colorInsetShift;
            var insetB = (maxB - minB) >> colorInsetShift;

            // Inset by 1/16th
            minR = ((minR << colorInsetShift) + insetR) >> colorInsetShift;
            minG = ((minG << colorInsetShift) + insetG) >> colorInsetShift;
            minB = ((minB << colorInsetShift) + insetB) >> colorInsetShift;

            maxR = ((maxR << colorInsetShift) - insetR) >> colorInsetShift;
            maxG = ((maxG << colorInsetShift) - insetG) >> colorInsetShift;
            maxB = ((maxB << colorInsetShift) - insetB) >> colorInsetShift;

            minR = minR >= 0 ? minR : 0;
            minG = minG >= 0 ? minG : 0;
            minB = minB >= 0 ? minB : 0;

            maxR = maxR <= 255 ? maxR : 255;
            maxG = maxG <= 255 ? maxG : 255;
            maxB = maxB <= 255 ? maxB : 255;

            //I also never did a rounding in c++
            //
            // Optimal rounding
            minR = (minR & c5655Mask) | (minR >> 5);
            minG = (minG & c5656Mask) | (minG >> 6);
            minB = (minB & c5655Mask) | (minB >> 5);

            maxR = (maxR & c5655Mask) | (maxR >> 5);
            maxG = (maxG & c5656Mask) | (maxG >> 6);
            maxB = (maxB & c5655Mask) | (maxB >> 5);

            var c0 = new ColorRgb565((byte)minR, (byte)minG, (byte)minB);
            var c1 = new ColorRgb565((byte)maxR, (byte)maxG, (byte)maxB);


            if (hasAlpha && c0.data > c1.data)
            {
                var c = c0;
                c0 = c1;
                c1 = c;

            }
            output = TryColors(rawBlock, c0, c1, out var error);


            return output;
        }

        //only for testing...this is RgbaColor8
        public struct ColorRgba32 : IEquatable<ColorRgba32>
        {
            public byte R, G, B, A;

            public ColorRgba32(byte r, byte g, byte b, byte a)
            {
                this.R = r;
                this.G = g;
                this.B = b;
                this.A = a;
            }

            public bool Equals(ColorRgba32 other)
            {
                return R == other.R && G == other.G && B == other.B && A == other.A;
            }


        }

        internal struct ColorRgb24 : IEquatable<ColorRgb24>
        {
            public byte r, g, b;

            public ColorRgb24(byte r, byte g, byte b)
            {
                this.r = r;
                this.g = g;
                this.b = b;
            }

            private static byte Interpolate(int a, int b, int num, int den)
            {
                return (byte)(((den - num) * a + num * b) / (float)den);
            }

            public ColorRgb24 InterpolateHalf(ColorRgb24 c)
            {
                return new ColorRgb24(
                    (byte)((r + c.r) / 2),
                    (byte)((g + c.g) / 2),
                    (byte)((b + c.b) / 2)
                    );
            }


            public ColorRgb24 InterpolateThird(ColorRgb24 c, int n)
            {
                return new ColorRgb24(
                Interpolate(r, c.r, n, 3),
                Interpolate(g, c.g, n, 3),
                Interpolate(b, c.b, n, 3));
            }

            public bool Equals(ColorRgb24 other)
            {
                return r == other.r && g == other.g && b == other.b;
            }

        }

        internal struct ColorRgb565
        {

            private const ushort RedMask = 0b11111_000000_00000;
            private const int RedShift = 11;
            private const ushort GreenMask = 0b00000_111111_00000;
            private const int GreenShift = 5;
            private const ushort BlueMask = 0b00000_000000_11111;

            public ushort data = 0;

            public byte R
            {
                readonly get
                {
                    var r5 = (data & RedMask) >> RedShift;
                    return (byte)((r5 << 3) | (r5 >> 2));
                }
                set
                {
                    var r5 = value >> 3;
                    data = (ushort)(data & ~RedMask);
                    data = (ushort)(data | (r5 << RedShift));
                }
            }

            public byte G
            {
                readonly get
                {
                    var g6 = (data & GreenMask) >> GreenShift;
                    return (byte)((g6 << 2) | (g6 >> 4));
                }
                set
                {
                    var g6 = value >> 2;
                    data = (ushort)(data & ~GreenMask);
                    data = (ushort)(data | (g6 << GreenShift));
                }
            }

            public byte B
            {
                readonly get
                {
                    var b5 = data & BlueMask;
                    return (byte)((b5 << 3) | (b5 >> 2));
                }
                set
                {
                    var b5 = value >> 3;
                    data = (ushort)(data & ~BlueMask);
                    data = (ushort)(data | b5);
                }
            }

            public ColorRgb565(byte r, byte g, byte b)
            {
                data = 0;
                R = r;
                G = g;
                B = b;
            }
            public readonly ColorRgb24 ToColorRgb24()
            {
                return new ColorRgb24(R, G, B);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct RawBlock4X4Rgba32
        {
            public ColorRgba32 p00, p10, p20, p30;//row 0
            public ColorRgba32 p01, p11, p21, p31;//row 1
            public ColorRgba32 p02, p12, p22, p32;//row2
            public ColorRgba32 p03, p13, p23, p33;//row 3



            public Span<ColorRgba32> asPixelSpan => MemoryMarshal.CreateSpan(ref p00, 16);
            public Span<uint> asUintSpan => MemoryMarshal.Cast<ColorRgba32, uint>(asPixelSpan);


            public bool HasTransparentPixels()
            {
                var pixels = asPixelSpan;
                for (var i = 0; i < pixels.Length; i++)
                {
                    if (pixels[i].A < 255) return true;
                }
                return false;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Bc1Block
        {
            public ColorRgb565 color0;
            public ColorRgb565 color1;
            public uint colorIndices;

            public int this[int index]
            {
                readonly get => (int)(colorIndices >> (index * 2)) & 0b11;
                set
                {
                    colorIndices = (uint)(colorIndices & ~(0b11 << (index * 2)));
                    var val = value & 0b11;
                    colorIndices = colorIndices | ((uint)val << (index * 2));
                }
            }

            public readonly bool HasAlphaOrBlack => color0.data <= color1.data;

        }
    }
}
