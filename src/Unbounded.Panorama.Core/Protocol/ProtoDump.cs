using System.Text;
using Google.Protobuf;

namespace Unbounded.Panorama.Protocol;

/// <summary>
/// Schema-less protobuf dump. Shows every field the device sent, including ones the
/// project schema does not declare.
/// </summary>
public static class ProtoDump
{
    public static string Dump(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder();
        if (!TryDump(data.ToArray(), 0, sb))
            sb.AppendLine($"<not a protobuf message: {Convert.ToHexString(data)}>");
        return sb.ToString();
    }

    private static bool TryDump(byte[] data, int depth, StringBuilder sb)
    {
        var lines = new StringBuilder();
        try
        {
            var input = new CodedInputStream(data);
            while (!input.IsAtEnd)
            {
                var tag = input.ReadTag();
                if (tag == 0)
                    return false;
                var field = WireFormat.GetTagFieldNumber(tag);
                var indent = new string(' ', depth * 2);
                switch (WireFormat.GetTagWireType(tag))
                {
                    case WireFormat.WireType.Varint:
                        var v = input.ReadUInt64();
                        lines.AppendLine($"{indent}{field}: {v} (varint, 0x{v:x})");
                        break;
                    case WireFormat.WireType.Fixed64:
                        lines.AppendLine($"{indent}{field}: 0x{input.ReadFixed64():x16} (fixed64)");
                        break;
                    case WireFormat.WireType.Fixed32:
                        lines.AppendLine($"{indent}{field}: 0x{input.ReadFixed32():x8} (fixed32)");
                        break;
                    case WireFormat.WireType.LengthDelimited:
                        var bytes = input.ReadBytes().ToByteArray();
                        var nested = new StringBuilder();
                        if (bytes.Length > 0 && TryDump(bytes, depth + 1, nested))
                        {
                            lines.AppendLine($"{indent}{field}: {{  // {bytes.Length} bytes{PrintableHint(bytes)}");
                            lines.Append(nested);
                            lines.AppendLine($"{indent}}}");
                        }
                        else
                        {
                            lines.AppendLine($"{indent}{field}: {Describe(bytes)}");
                        }
                        break;
                    default:
                        return false;
                }
            }
        }
        catch (InvalidProtocolBufferException)
        {
            return false;
        }
        sb.Append(lines);
        return true;
    }

    private static string PrintableHint(byte[] bytes) =>
        IsPrintableUtf8(bytes) ? $", also valid text \"{Encoding.UTF8.GetString(bytes)}\"" : "";

    private static string Describe(byte[] bytes) =>
        IsPrintableUtf8(bytes) ? $"\"{Encoding.UTF8.GetString(bytes)}\"" : $"bytes[{bytes.Length}] {Convert.ToHexString(bytes)}";

    private static bool IsPrintableUtf8(byte[] bytes)
    {
        try
        {
            var text = new UTF8Encoding(false, true).GetString(bytes);
            return text.All(c => !char.IsControl(c) || c is '\n' or '\r' or '\t');
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
