using Ryujinx.Graphics.Texture.Utils;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Ryujinx.Graphics.Texture.Encoders
{
    static class BC1Encoder
    {
        // Values Between 0~254 any pixel with Alpha below or equal this threshold will be invisible,
        // otherwise 100% opaque.
        private const int AlphaCutOff = 128;
        public static void Encode(Memory<byte> outputStorage, Memory<byte> data, int width, int height, EncodeMode mode)
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
                        var block = output.Slice(offset * 8, 8);
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
                        var block = output.Slice(offset * 8, 8);
                        CompressBlock(data.Span, x, y, width, height, block);
                        offset++;
                    }
                }
            }
        }



        private static void CompressBlock(ReadOnlySpan<byte> data, int x, int y, int width, int height, Span<byte> outputBlock)
        {
            int w = Math.Min(4, width - x);
            int h = Math.Min(4, height - y);

            var dataUint = MemoryMarshal.Cast<byte, uint>(data);

            int baseOffset = y * width + x;

            Span<uint> tile = new uint[16];

            for (int ty = 0; ty < h; ty++)
            {
                int rowOffset = baseOffset + ty * width;

                for (int tx = 0; tx < w; tx++)
                {
                    tile[ty * w + tx] = dataUint[rowOffset + tx];
                }
            }

            var colorBlock = MemoryMarshal.Cast<uint, RgbaColor8>(tile);

            (RgbaColor8 minColor, RgbaColor8 maxColor) = BC67Utils.GetMinMaxColors(tile, w, h);

            const int C565_5_MASK = 0xF8;
            const int C565_6_MASK = 0xFC;

            RgbaColor32[] colors = new RgbaColor32[4];
            var indicesPTR = MemoryMarshal.Cast<byte, int>(outputBlock.Slice(4));
            indicesPTR[0] = 0;
            var ColorsC0c1 = MemoryMarshal.Cast<byte, ushort>(outputBlock);

            colors[0].R = (maxColor.R & C565_5_MASK) | (maxColor.R >> 5);
            colors[0].G = (maxColor.G & C565_6_MASK) | (maxColor.G >> 6);
            colors[0].B = (maxColor.B & C565_5_MASK) | (maxColor.B >> 5);
            colors[1].R = (minColor.R & C565_5_MASK) | (minColor.R >> 5);
            colors[1].G = (minColor.G & C565_6_MASK) | (minColor.G >> 6);
            colors[1].B = (minColor.B & C565_5_MASK) | (minColor.B >> 5);


            var MaxColor565 = (ushort)(((colors[0].R >> 3) << 11) | ((colors[0].G >> 2) << 5) | (colors[0].B >> 3));
            var MinColor565 = (ushort)(((colors[1].R >> 3) << 11) | ((colors[1].G >> 2) << 5) | (colors[1].B >> 3));

            ColorsC0c1[0] = MaxColor565;
            ColorsC0c1[1] = MinColor565;

            bool hasAlpha = maxColor.A <= AlphaCutOff;
            if (hasAlpha)
            {
                colors[2].R = (maxColor.R + minColor.R) / 2;
                colors[2].G = (maxColor.G + minColor.G) / 2;
                colors[2].B = (maxColor.B + minColor.B) / 2;
                colors[3].R = colors[3].G = colors[3].B = 0;
            }
            else
            {
                colors[2].R = (2 * colors[0].R + 1 * colors[1].R) / 3;
                colors[2].G = (2 * colors[0].G + 1 * colors[1].G) / 3;
                colors[2].B = (2 * colors[0].B + 1 * colors[1].B) / 3;
                colors[3].R = (1 * colors[0].R + 2 * colors[1].R) / 3;
                colors[3].G = (1 * colors[0].G + 2 * colors[1].G) / 3;
                colors[3].B = (1 * colors[0].B + 2 * colors[1].B) / 3;
            }

            for (int i = 15; i >= 0; i--)
            {
                if (colorBlock[i].A <= AlphaCutOff)
                {
                    indicesPTR[0] |= 3 << (i << 1);//just set index 11, color3 (transparent black)
                    continue;
                }
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
                indicesPTR[0] |= (x2 | ((x0 | x1) << 1)) << (i << 1);

            }

        }

    }
}
