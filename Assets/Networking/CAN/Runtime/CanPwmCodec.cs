using System;

namespace ExcavatorApp.Networking.CAN
{
    /// <summary>
    /// CAN 8-byte payload codec:
    /// 4-bit header 1010 + 6×10-bit channels (MSB-first, continuous bit stream).
    /// </summary>
    public static class CanPwmCodec
    {
        public const int PayloadSizeBytes = 8;
        public const int ChannelCount = 6;
        public const ushort HeaderNibble = 0b1010;

        public static byte[] Encode(ReadOnlySpan<ushort> pwm)
        {
            if (pwm.Length != ChannelCount)
                throw new ArgumentException($"pwm must be length {ChannelCount}.", nameof(pwm));

            ulong bits = 0;
            int bitIndex = 0; // 0..63, where 0 maps to MSB (bit 63) of the ulong.

            void WriteBit(int bit)
            {
                int pos = 63 - bitIndex;
                bits |= ((ulong)(bit & 1) << pos);
                bitIndex++;
            }

            void WriteBits(ushort value, int width)
            {
                for (int b = width - 1; b >= 0; b--)
                    WriteBit((value >> b) & 1);
            }

            // Header: 1010
            WriteBit(1);
            WriteBit(0);
            WriteBit(1);
            WriteBit(0);

            for (int ch = 0; ch < ChannelCount; ch++)
            {
                ushort v = pwm[ch];
                if (v > 1000)
                    throw new ArgumentOutOfRangeException(nameof(pwm), $"Channel {ch + 1} value {v} is out of range (0..1000).");

                WriteBits(v, 10);
            }

            if (bitIndex != 64)
                throw new InvalidOperationException($"Internal packing error: wrote {bitIndex} bits, expected 64.");

            byte[] payload = new byte[PayloadSizeBytes];
            for (int i = 0; i < PayloadSizeBytes; i++)
                payload[i] = (byte)(bits >> (56 - (i * 8)));

            return payload;
        }

        public static void Decode(ReadOnlySpan<byte> payload, Span<ushort> pwmOut)
        {
            if (payload.Length != PayloadSizeBytes)
                throw new ArgumentException($"payload must be {PayloadSizeBytes} bytes.", nameof(payload));
            if (pwmOut.Length < ChannelCount)
                throw new ArgumentException($"pwmOut must be at least length {ChannelCount}.", nameof(pwmOut));

            // Avoid local functions capturing ReadOnlySpan<byte> (Unity/C# compiler limitation with ref-like locals).
            // Build MSB-first 64-bit stream in a ulong (payload[0] is the highest byte).
            ulong bits =
                ((ulong)payload[0] << 56) |
                ((ulong)payload[1] << 48) |
                ((ulong)payload[2] << 40) |
                ((ulong)payload[3] << 32) |
                ((ulong)payload[4] << 24) |
                ((ulong)payload[5] << 16) |
                ((ulong)payload[6] << 8) |
                payload[7];

            ushort header = (ushort)((bits >> 60) & 0xFu);
            if (header != HeaderNibble)
                throw new FormatException($"Bad header nibble: expected 0b{Convert.ToString(HeaderNibble, 2)}, got 0b{Convert.ToString(header, 2)}.");

            // After header (bits 63..60), channel 1 occupies bits 59..50, channel 2 occupies 49..40, ..., channel 6 occupies 9..0.
            for (int ch = 0; ch < ChannelCount; ch++)
            {
                int lo = 50 - (10 * ch);
                pwmOut[ch] = (ushort)((bits >> lo) & 0x3FFu);
            }
        }
    }
}

