using System.Text;

namespace ShiroBot.LagrangeAdapter;

// Small bounded wire codec for the SDK operations missing from Lagrange's public API.
// Field layouts follow Acidify's protocol schemas; unknown fields remain skippable.
internal sealed class ProtoWire
{
    internal readonly record struct Field(int Number, int Wire, ulong Integer, byte[] Bytes);
    private readonly MemoryStream _output = new();
    internal ProtoWire UInt(int field, ulong value)
    {
        Tag(field, 0); Varint(value); return this;
    }
    internal ProtoWire Text(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value));
    internal ProtoWire Message(int field, ProtoWire value) => Bytes(field, value.ToArray());
    internal ProtoWire Bytes(int field, ReadOnlySpan<byte> value)
    {
        Tag(field, 2); Varint((ulong)value.Length); _output.Write(value); return this;
    }
    private void Tag(int field, int wire)
    {
        if (field is < 1 or > 0x1fffffff) throw new ArgumentOutOfRangeException(nameof(field));
        Varint(((ulong)field << 3) | (uint)wire);
    }
    private void Varint(ulong value)
    {
        while (value > 127) { _output.WriteByte((byte)(value | 128)); value >>= 7; }
        _output.WriteByte((byte)value);
    }
    internal byte[] ToArray() => _output.ToArray();
    internal static List<Field> Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > 16 * 1024 * 1024) throw new InvalidDataException("Protocol response exceeds limit.");
        List<Field> result = []; int offset = 0;
        while (offset < bytes.Length)
        {
            if (result.Count >= 65536) throw new InvalidDataException("Too many protobuf fields.");
            var tag = ReadVarint(bytes, ref offset);
            var number = checked((int)(tag >> 3)); var wire = (int)(tag & 7);
            if (number is < 1 or > 0x1fffffff) throw new InvalidDataException("Invalid protobuf field.");
            if (wire == 0) result.Add(new Field(number, wire, ReadVarint(bytes, ref offset), []));
            else
            {
                var size = wire switch { 1 => 8, 5 => 4, 2 => checked((int)ReadVarint(bytes, ref offset)), _ => throw new InvalidDataException("Unsupported protobuf wire type.") };
                if (size > bytes.Length - offset) throw new InvalidDataException("Truncated protobuf field.");
                result.Add(new Field(number, wire, 0, bytes.Slice(offset, size).ToArray())); offset += size;
            }
        }
        return result;
    }
    private static ulong ReadVarint(ReadOnlySpan<byte> data, ref int offset)
    {
        ulong value = 0;
        for (var shift = 0; shift < 70; shift += 7)
        {
            if (offset == data.Length) throw new InvalidDataException("Truncated protobuf varint.");
            var b = data[offset++];
            if (shift == 63 && b > 1) throw new InvalidDataException("Protobuf varint overflow.");
            value |= (ulong)(b & 127) << shift;
            if (b < 128) return value;
        }
        throw new InvalidDataException("Invalid protobuf varint.");
    }
    internal static ulong Number(IEnumerable<Field> fields, int field) => fields.FirstOrDefault(f => f.Number == field && f.Wire == 0).Integer;
    internal static byte[] Data(IEnumerable<Field> fields, int field) => fields.FirstOrDefault(f => f.Number == field && f.Wire == 2).Bytes ?? [];
    internal static string String(IEnumerable<Field> fields, int field) => Encoding.UTF8.GetString(Data(fields, field));
    internal static List<Field> Child(IEnumerable<Field> fields, int field) => Parse(Data(fields, field));
}
