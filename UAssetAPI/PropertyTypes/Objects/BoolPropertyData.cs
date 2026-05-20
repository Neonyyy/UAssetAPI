using System;
using System.IO;
using System.Linq;
using UAssetAPI.UnrealTypes;

namespace UAssetAPI.PropertyTypes.Objects;

/// <summary>
/// Describes a boolean (<see cref="bool"/>).
/// </summary>
public class BoolPropertyData : PropertyData<bool>
{
    public BoolPropertyData(FName name) : base(name) { }

    public BoolPropertyData() { }

    private static readonly FString CurrentPropertyType = new FString("BoolProperty");
    public override FString PropertyType => CurrentPropertyType;
    public override object DefaultValue => false;

    public override void Read(AssetBinaryReader reader, bool includeHeader, long leng1, long leng2 = 0, PropertySerializationContext serializationContext = PropertySerializationContext.Normal)
    {
        if (reader.Asset.HasUnversionedProperties || reader.Asset.ObjectVersionUE5 < ObjectVersionUE5.PROPERTY_TAG_COMPLETE_TYPE_NAME)
        {
            Value = ReadBooleanByteWithDiagnostics(reader, serializationContext);
        }
        else
        {
            if (serializationContext is PropertySerializationContext.Map or PropertySerializationContext.Array)
                Value = ReadBooleanByteWithDiagnostics(reader, serializationContext);
            else
                Value = this.PropertyTagFlags.HasFlag(EPropertyTagFlags.BoolTrue);
        }
        if (includeHeader)
        {
            this.ReadEndPropertyTag(reader);
        }
    }

    // Wraps reader.ReadBooleanByte() so that when the byte at the stream position isn't 0 or
    // 1, we capture a hexdump of ~32 bytes around the failed position into the asset's
    // parse-errors.log before re-throwing. Lets us see what's actually at the mis-aligned
    // read site for diagnostic purposes (e.g. BP_MJJJ_AI_New dep CDOs reporting "Invalid
    // boolean value 19" / "35") without rebuilding with DEBUGVERBOSE.
    private bool ReadBooleanByteWithDiagnostics(AssetBinaryReader reader, PropertySerializationContext serializationContext)
    {
        try
        {
            return reader.ReadBooleanByte();
        }
        catch (FormatException) when (reader.Asset != null && !string.IsNullOrEmpty(reader.Asset.FilePath))
        {
            TryWriteBoolFailureHexdump(reader, serializationContext);
            throw;
        }
    }

    private void TryWriteBoolFailureHexdump(AssetBinaryReader reader, PropertySerializationContext serializationContext)
    {
        try
        {
            long failedPos = reader.BaseStream.Position; // already 1 past the bad byte
            long badBytePos = failedPos - 1;
            long startPos = Math.Max(0, badBytePos - 16);
            long endPos = Math.Min(reader.BaseStream.Length, badBytePos + 17);
            int countToRead = (int)(endPos - startPos);
            if (countToRead <= 0) return;

            long savedPos = reader.BaseStream.Position;
            try
            {
                reader.BaseStream.Position = startPos;
                byte[] context = new byte[countToRead];
                int got = reader.BaseStream.Read(context, 0, countToRead);

                var parts = Enumerable.Range(0, got).Select(i =>
                {
                    long abs = startPos + i;
                    string hex = context[i].ToString("X2");
                    return abs == badBytePos ? "[" + hex + "]" : hex;
                });
                string hexdump = string.Join(" ", parts);

                string logPath = reader.Asset.FilePath + ".parse-errors.log";
                string contextTag = reader.Asset.IsParsingToPullSchemas ? " [during schema pull]" : string.Empty;
                string propName = Name?.ToString() ?? "?";
                File.AppendAllText(logPath,
                    "[" + DateTime.Now.ToString("O") + "]" + contextTag +
                    " BoolProperty misaligned read on property '" + propName + "'" +
                    " (serializationContext=" + serializationContext +
                    ", bad byte at file offset 0x" + badBytePos.ToString("X") + "): " +
                    hexdump + "\n\n");
            }
            finally
            {
                reader.BaseStream.Position = savedPos;
            }
        }
        catch { /* swallow logging failures */ }
    }

    public override int Write(AssetBinaryWriter writer, bool includeHeader, PropertySerializationContext serializationContext = PropertySerializationContext.Normal)
    {
        if (writer.Asset.HasUnversionedProperties || writer.Asset.ObjectVersionUE5 < ObjectVersionUE5.PROPERTY_TAG_COMPLETE_TYPE_NAME)
        {
            writer.Write(Value);
        }
        else if (serializationContext is PropertySerializationContext.Map or PropertySerializationContext.Array)
        {
            writer.Write(Value);
        }

        if (includeHeader)
        {
            this.WriteEndPropertyTag(writer);
        }
        return 0;
    }

    public override string ToString()
    {
        return Convert.ToString(Value);
    }

    public override void FromString(string[] d, UAsset asset)
    {
        Value = d[0].Equals("1") || d[0].ToLowerInvariant().Equals("true");
    }
}