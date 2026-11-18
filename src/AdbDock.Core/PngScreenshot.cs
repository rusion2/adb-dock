using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace AdbDock.Core;

internal static class PngScreenshot
{
    private static readonly (int X, int Y, int StepX, int StepY)[] Straight = [(0, 0, 1, 1)];
    private static readonly (int X, int Y, int StepX, int StepY)[] Adam7 =
        [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)];

    public static void Validate(Stream file, CancellationToken cancellationToken)
    {
        try { ReadImage(file, cancellationToken); }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException)
        {
            throw new InvalidDataException("ADB 返回的 PNG 截图不完整或已损坏，文件未保存。", exception);
        }
    }

    private static void ReadImage(Stream file, CancellationToken cancellationToken)
    {
        Span<byte> signature = stackalloc byte[8];
        file.ReadExactly(signature);
        if (BinaryPrimitives.ReadUInt64BigEndian(signature) != 0x89504E470D0A1A0AUL) throw new InvalidDataException();

        using var chunks = new ImageChunks(file, cancellationToken);
        chunks.ReadHeader();
        if (chunks.Kind != "IHDR" || chunks.Remaining != 13) throw new InvalidDataException();
        Span<byte> header = stackalloc byte[13];
        chunks.ReadPayload(header);
        var width = BinaryPrimitives.ReadInt32BigEndian(header);
        var height = BinaryPrimitives.ReadInt32BigEndian(header[4..]);
        var depth = header[8];
        var color = header[9];
        var channels = color switch
        {
            0 when depth is 1 or 2 or 4 or 8 or 16 => 1,
            2 when depth is 8 or 16 => 3,
            3 when depth is 1 or 2 or 4 or 8 => 1,
            4 when depth is 8 or 16 => 2,
            6 when depth is 8 or 16 => 4,
            _ => throw new InvalidDataException()
        };
        if (width <= 0 || height <= 0 || header[10] != 0 || header[11] != 0 || header[12] > 1)
            throw new InvalidDataException();

        var buffer = new byte[32768];
        var hasPalette = false;
        var hasImage = false;
        chunks.FinishChunk();
        while (true)
        {
            chunks.ReadHeader();
            if (chunks.Kind == "IDAT")
            {
                if (hasImage || (color == 3 && !hasPalette)) throw new InvalidDataException();
                using var pixels = new ZLibStream(chunks, CompressionMode.Decompress, leaveOpen: true);
                foreach (var pass in header[12] == 0 ? Straight : Adam7)
                {
                    if (width <= pass.X || height <= pass.Y) continue;
                    var columns = 1 + (width - 1 - pass.X) / pass.StepX;
                    var rows = 1 + (height - 1 - pass.Y) / pass.StepY;
                    var rowBytes = ((long)columns * channels * depth + 7) / 8;
                    for (int row = 0; row < rows; row++)
                    {
                        if (pixels.ReadByte() is < 0 or > 4) throw new InvalidDataException();
                        for (long left = rowBytes; left > 0; left -= buffer.Length)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            pixels.ReadExactly(buffer.AsSpan(0, (int)Math.Min(left, buffer.Length)));
                        }
                    }
                }
                if (pixels.ReadByte() != -1) throw new InvalidDataException();
                hasImage = true;
                // The zlib stream may finish before the end of its final IDAT,
                // including when the encoder adds one or more empty IDATs.
                do
                {
                    chunks.SkipPayload(buffer);
                    chunks.FinishChunk();
                    chunks.ReadHeader();
                } while (chunks.Kind == "IDAT");
            }
            switch (chunks.Kind)
            {
                case "IEND":
                    if (!hasImage || chunks.Remaining != 0) throw new InvalidDataException();
                    chunks.FinishChunk();
                    if (file.ReadByte() != -1) throw new InvalidDataException();
                    return;
                case "PLTE":
                    var length = chunks.Remaining;
                    if (hasImage || hasPalette || color is 0 or 4 || length is < 3 or > 768 ||
                        length % 3 != 0 || (color == 3 && length / 3 > (1 << depth))) throw new InvalidDataException();
                    hasPalette = true;
                    break;
                default:
                    if (!char.IsAsciiLetterLower(chunks.Kind[0])) throw new InvalidDataException();
                    break;
            }
            chunks.SkipPayload(buffer);
            chunks.FinishChunk();
        }
    }

    // PNG's single zlib stream can span many adjacent IDAT chunks. Keep the
    // CRC and decoder streaming so memory is independent of the picture size.
    private sealed class ImageChunks(Stream file, CancellationToken cancellationToken) : Stream
    {
        private static readonly uint[] CrcTable = MakeCrcTable();
        private uint _crc;
        public string Kind { get; private set; } = "";
        public int Remaining { get; private set; }

        public void ReadHeader()
        {
            cancellationToken.ThrowIfCancellationRequested();
            Span<byte> header = stackalloc byte[8];
            file.ReadExactly(header);
            Remaining = BinaryPrimitives.ReadInt32BigEndian(header);
            if (Remaining < 0) throw new InvalidDataException();
            foreach (var c in header[4..]) if (!char.IsAsciiLetter((char)c)) throw new InvalidDataException();
            Kind = Encoding.ASCII.GetString(header[4..]);
            _crc = uint.MaxValue;
            UpdateCrc(header[4..]);
        }

        public void ReadPayload(Span<byte> destination)
        {
            cancellationToken.ThrowIfCancellationRequested();
            file.ReadExactly(destination);
            UpdateCrc(destination);
            Remaining -= destination.Length;
        }

        public void SkipPayload(byte[] buffer)
        {
            while (Remaining > 0) ReadPayload(buffer.AsSpan(0, Math.Min(buffer.Length, Remaining)));
        }

        public void FinishChunk()
        {
            Span<byte> checksum = stackalloc byte[4];
            file.ReadExactly(checksum);
            if (~_crc != BinaryPrimitives.ReadUInt32BigEndian(checksum)) throw new InvalidDataException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty) return 0;
            while (Remaining == 0)
            {
                FinishChunk();
                ReadHeader();
                // If the inflater still needs input, reaching a different chunk
                // means a truncated zlib stream (including a missing checksum).
                if (Kind != "IDAT") throw new InvalidDataException();
            }
            var size = Math.Min(buffer.Length, Remaining);
            ReadPayload(buffer[..size]);
            return size;
        }

        private void UpdateCrc(ReadOnlySpan<byte> input)
        {
            foreach (byte value in input) _crc = CrcTable[(_crc ^ value) & 255] ^ (_crc >> 8);
        }

        private static uint[] MakeCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < table.Length; n++)
            {
                uint crc = n;
                for (int bit = 0; bit < 8; bit++) crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xEDB88320u;
                table[n] = crc;
            }
            return table;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
    }
}
