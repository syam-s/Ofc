﻿using OfcAlgorithm.Integration;
using OfcCore.Utility;

namespace OfcAlgorithm.Blocky.Method.FloatSimmilar
{
    class FloatSimmilarDecompression : DecompressionMethod
    {
        public FloatSimmilarDecompression(BlockyMetadata metadata) : base(metadata)
        {
        }


        public override int Read(IOfcNumberWriter writer, Block block, StreamBitReader reader)
        {
            // ReSharper disable once AssignmentInConditionalExpression
            if (block.OverrideGlobalNb = reader.ReadByte(1) > 0)
            {
                block.NeededBits = reader.ReadByte(Metadata.MaxNeededBitsNeededBitsNumber);
            }

            // ReSharper disable once AssignmentInConditionalExpression
            if (block.AbsoluteSign = reader.ReadByte(1) > 0)
            {
                block.IsSignNegative = reader.ReadByte(1) > 0;
            }

            var isNegative = block.AbsoluteSign && block.IsSignNegative;

            for (var i = 0; i < block.Length; i++)
            {
                var num = new OfcNumber();

                if (!block.AbsoluteSign)
                    isNegative = reader.ReadByte(1) > 0;

                num.Number = ((long)reader.Read(Metadata.MaxNeededBitsNumber)) * (isNegative ? -1 : 1);
                num.Exponent = block.Exponent;
                writer.Write(num);
            }

            return block.Length;
        }
    }
}