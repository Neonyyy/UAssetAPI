using Newtonsoft.Json;
using System;
using System.Linq;
using UAssetAPI.JSON;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace UAssetAPI.PropertyTypes.Objects;

/// <summary>
/// Describes a map.
/// </summary>
public class MapPropertyData : PropertyData
{
    /// <summary>
    /// The map that this property represents.
    /// </summary>
    [JsonProperty]
    [JsonConverter(typeof(TMapJsonConverter<PropertyData, PropertyData>))]
    public TMap<PropertyData, PropertyData> Value;

    /// <summary>
    /// Used when the length of the map is zero.
    /// </summary>]
    [JsonProperty]
    public FName KeyType;

    /// <summary>
    /// Used when the length of the map is zero.
    /// </summary>
    [JsonProperty]
    public FName ValueType;

    public bool ShouldSerializeKeyType() => Value.Count == 0;

    public bool ShouldSerializeValueType() => Value.Count == 0;

    [JsonProperty]
    public PropertyData[] KeysToRemove = null;

    public MapPropertyData(FName name) : base(name)
    {
        Value = new TMap<PropertyData, PropertyData>();
    }

    public MapPropertyData()
    {
        Value = new TMap<PropertyData, PropertyData>();
    }

    private static readonly FString CurrentPropertyType = new FString("MapProperty");
    public override FString PropertyType { get { return CurrentPropertyType; } }

    private PropertyData MapTypeToClass(FName type, FName name, AssetBinaryReader reader, int leng, bool includeHeader, bool isKey)
    {
        switch (type.Value.Value)
        {
            case "StructProperty":
                FName strucType = null;
                FPropertyTypeName propertyTypeNameLocal = PropertyTypeName?.GetParameter(isKey ? 0 : 1);
                if (!reader.Asset.HasUnversionedProperties && reader.Asset.ObjectVersionUE5 >= ObjectVersionUE5.PROPERTY_TAG_COMPLETE_TYPE_NAME)
                {
                    strucType = propertyTypeNameLocal?.GetParameter(0).GetName();
                }
                else if (reader.Asset.Mappings != null && reader.Asset.Mappings.TryGetPropertyData(Name, Ancestry, reader.Asset, out UsmapMapData mapDat))
                {
                    if (isKey && mapDat.InnerType is UsmapStructData strucDat1)
                    {
                        strucType = FName.DefineDummy(reader.Asset, strucDat1.StructType);
                    }
                    else if (mapDat.ValueType is UsmapStructData strucDat2)
                    {
                        strucType = FName.DefineDummy(reader.Asset, strucDat2.StructType);
                    }
                }
                else if (reader.Asset.MapStructTypeOverride.ContainsKey(name.Value.Value))
                {
                    if (isKey)
                    {
                        strucType = FName.DefineDummy(reader.Asset, reader.Asset.MapStructTypeOverride[name.Value.Value].Item1);
                    }
                    else
                    {
                        strucType = FName.DefineDummy(reader.Asset, reader.Asset.MapStructTypeOverride[name.Value.Value].Item2);
                        if (name.Value.Value == "TrackSignatureToTrackIdentifier" && reader.Asset.GetEngineVersion() <= EngineVersion.VER_UE4_18)
                            strucType = FName.DefineDummy(reader.Asset, "Generic");
                    }
                }

                if (strucType?.Value == null) strucType = FName.DefineDummy(reader.Asset, "Generic");

                StructPropertyData data = new StructPropertyData(name, strucType);
                data.Ancestry.Initialize(Ancestry, Name);
                data.Offset = reader.BaseStream.Position;
                data.PropertyTypeName = propertyTypeNameLocal;
                data.Read(reader, false, 1, 0, PropertySerializationContext.Map);
                return data;
            case "EnumProperty":
                // Unversioned enum-in-map has an ambiguous wire format: some assets store the
                // enum value as an FName (8 bytes, matches what EnumPropertyData.Read falls back
                // to in Map context due to its IsNormal() gate), others store it as the underlying
                // integer (1-8 bytes per UsmapEnumData.InnerType). Use the same peek heuristic
                // BytePropertyData.ReadCustom uses for the same ambiguity: if the next 8 bytes
                // look like a valid FName reference, treat as FName format; otherwise read the
                // raw integer.
                if (reader.Asset.HasUnversionedProperties && reader.Asset.Mappings != null
                    && reader.Asset.Mappings.TryGetPropertyData(Name, Ancestry, reader.Asset, out UsmapMapData mapDatForEnum)
                    && ((isKey ? mapDatForEnum.InnerType : mapDatForEnum.ValueType) is UsmapEnumData enumDat))
                {
                    long savedPos = reader.BaseStream.Position;
                    int peekedNamePointer = reader.ReadInt32();
                    int peekedNameIndex = reader.ReadInt32();
                    reader.BaseStream.Position = savedPos;

                    bool looksLikeFName = false;
                    var nameMapList = reader.Asset.GetNameMapIndexList();
                    if (peekedNamePointer >= 0 && peekedNamePointer < nameMapList.Count && peekedNameIndex == 0)
                    {
                        string nameRef = reader.Asset.GetNameReference(peekedNamePointer)?.ToString() ?? string.Empty;
                        looksLikeFName = !nameRef.Contains("/");
                    }

                    if (!looksLikeFName)
                    {
                        string innerTypeName = enumDat.InnerType.Type.ToString();
                        long? enumIndex = innerTypeName switch
                        {
                            "ByteProperty" => reader.ReadByte(),
                            "UInt16Property" => reader.ReadUInt16(),
                            "UInt32Property" => reader.ReadUInt32(),
                            "Int8Property" => reader.ReadSByte(),
                            "Int16Property" => reader.ReadInt16(),
                            "IntProperty" => reader.ReadInt32(),
                            "Int64Property" => reader.ReadInt64(),
                            _ => null
                        };

                        if (enumIndex.HasValue)
                        {
                            EnumPropertyData enumData = new EnumPropertyData(name);
                            enumData.Ancestry.Initialize(Ancestry, Name);
                            enumData.Offset = savedPos;
                            enumData.PropertyTypeName = PropertyTypeName?.GetParameter(isKey ? 0 : 1);
                            enumData.EnumType = FName.DefineDummy(reader.Asset, enumDat.Name);
                            enumData.InnerType = FName.DefineDummy(reader.Asset, innerTypeName);

                            if (reader.Asset.Mappings.EnumMap.TryGetValue(enumData.EnumType.Value.Value, out UsmapEnum enumMapping)
                                && enumMapping.Values != null
                                && enumMapping.Values.TryGetValue(enumIndex.Value, out string enumValueName))
                            {
                                enumData.Value = FName.DefineDummy(reader.Asset, enumValueName);
                            }
                            else
                            {
                                enumData.Value = FName.DefineDummy(reader.Asset, EnumPropertyData.InvalidEnumIndexFallbackPrefix + enumIndex.Value.ToString());
                            }

                            return enumData;
                        }
                    }
                }
                goto default;
            default:
                var res = MainSerializer.TypeToClass(type, name, Ancestry, Name, null, reader.Asset, null, leng, propertyTypeName: PropertyTypeName?.GetParameter(0));
                res.Ancestry.Initialize(Ancestry, Name);
                res.Read(reader, includeHeader, leng, 0, PropertySerializationContext.Map);
                return res;
        }
    }

    private TMap<PropertyData, PropertyData> ReadRawMap(AssetBinaryReader reader, FName type1, FName type2, int numEntries)
    {
        var resultingDict = new TMap<PropertyData, PropertyData>();

        PropertyData data1 = null;
        PropertyData data2 = null;
        for (int i = 0; i < numEntries; i++)
        {
            data1 = MapTypeToClass(type1, Name, reader, 0, false, true);
            data2 = MapTypeToClass(type2, Name, reader, 0, false, false);

            resultingDict.Add(data1, data2);
        }

        return resultingDict;
    }

    public override void Read(AssetBinaryReader reader, bool includeHeader, long leng1, long leng2 = 0, PropertySerializationContext serializationContext = PropertySerializationContext.Normal)
    {
        FName type1 = null, type2 = null;
        if (includeHeader && !reader.Asset.HasUnversionedProperties)
        {
            if (reader.Asset.ObjectVersionUE5 >= ObjectVersionUE5.PROPERTY_TAG_COMPLETE_TYPE_NAME)
            {
                if (PropertyTypeName is null) throw new FormatException("PropertyTypeName is required to read MapProperty with complete type names.");
                type1 = PropertyTypeName.GetParameter(0).GetName();
                type2 = PropertyTypeName.GetParameter(1).GetName();
            }
            else
            {
                type1 = reader.ReadFName();
                type2 = reader.ReadFName();
            }

            this.ReadEndPropertyTag(reader);
        }

        if (reader.Asset.Mappings != null && type1 == null && type2 == null && reader.Asset.Mappings.TryGetPropertyData(Name, Ancestry, reader.Asset, out UsmapMapData strucDat1))
        {
            type1 = FName.DefineDummy(reader.Asset, strucDat1.InnerType.Type.ToString());
            type2 = FName.DefineDummy(reader.Asset, strucDat1.ValueType.Type.ToString());
        }

        int numKeysToRemove = reader.ReadInt32();
        KeysToRemove = new PropertyData[numKeysToRemove];
        for (int i = 0; i < numKeysToRemove; i++)
        {
            KeysToRemove[i] = MapTypeToClass(type1, Name, reader, 0, false, true);
        }

        int numEntries = reader.ReadInt32();
        if (numEntries == 0)
        {
            KeyType = type1;
            ValueType = type2;
        }

        Value = ReadRawMap(reader, type1, type2, numEntries);
    }

    public override void ResolveAncestries(UAsset asset, AncestryInfo ancestrySoFar)
    {
        var ancestryNew = (AncestryInfo)ancestrySoFar.Clone();
        ancestryNew.SetAsParent(Name);

        if (Value != null)
        {
            foreach (var entry in Value)
            {
                entry.Key.ResolveAncestries(asset, ancestryNew);
                entry.Value.ResolveAncestries(asset, ancestryNew);
            }
        }
        base.ResolveAncestries(asset, ancestrySoFar);
    }

    private void WriteRawMap(AssetBinaryWriter writer, TMap<PropertyData, PropertyData> map, PropertySerializationContext serializationContext)
    {
        if (map == null) return;
        foreach (var entry in map)
        {
            if (serializationContext == PropertySerializationContext.CanBeZero && ((CanBeZeroStream)writer.BaseStream).HasWrittenNonZero) break;
            entry.Key.Offset = writer.BaseStream.Position;
            WriteMapElement(writer, entry.Key);
            entry.Value.Offset = writer.BaseStream.Position;
            WriteMapElement(writer, entry.Value);
        }
    }

    // Round-trip partner for the heuristic-gated integer read in MapTypeToClass. When the
    // read path identified the entry as an unversioned underlying-integer enum (EnumType +
    // InnerType both populated), write it back as the matching integer. The OLD-style FName
    // path leaves EnumType/InnerType null on EnumPropertyData, so it correctly falls through
    // to the default Write here.
    private static void WriteMapElement(AssetBinaryWriter writer, PropertyData element)
    {
        if (writer.Asset.HasUnversionedProperties
            && element is EnumPropertyData enumData
            && enumData.EnumType?.Value?.Value != null
            && enumData.InnerType?.Value?.Value != null)
        {
            long enumIndex = 0;
            if (enumData.Value != null && writer.Asset.Mappings != null
                && writer.Asset.Mappings.EnumMap.TryGetValue(enumData.EnumType.Value.Value, out UsmapEnum enumMapping)
                && enumMapping.Values != null)
            {
                bool found = false;
                foreach (var kv in enumMapping.Values)
                {
                    if (kv.Value == enumData.Value.Value.Value)
                    {
                        enumIndex = kv.Key;
                        found = true;
                        break;
                    }
                }
                if (!found && enumData.Value.Value.Value.StartsWith(EnumPropertyData.InvalidEnumIndexFallbackPrefix))
                {
                    long.TryParse(enumData.Value.Value.Value.Substring(EnumPropertyData.InvalidEnumIndexFallbackPrefix.Length), out enumIndex);
                }
            }

            switch (enumData.InnerType.Value.Value)
            {
                case "ByteProperty": writer.Write((byte)enumIndex); return;
                case "UInt16Property": writer.Write((ushort)enumIndex); return;
                case "UInt32Property": writer.Write((uint)enumIndex); return;
                case "Int8Property": writer.Write((sbyte)enumIndex); return;
                case "Int16Property": writer.Write((short)enumIndex); return;
                case "IntProperty": writer.Write((int)enumIndex); return;
                case "Int64Property": writer.Write((long)enumIndex); return;
            }
        }

        element.Write(writer, false, PropertySerializationContext.Map);
    }

    public override int Write(AssetBinaryWriter writer, bool includeHeader, PropertySerializationContext serializationContext = PropertySerializationContext.Normal)
    {
        if (includeHeader && !writer.Asset.HasUnversionedProperties)
        {
            if (writer.Asset.ObjectVersionUE5 < ObjectVersionUE5.PROPERTY_TAG_COMPLETE_TYPE_NAME)
            {
                if (Value.Count > 0)
                {
                    writer.Write(new FName(writer.Asset, Value.Keys.ElementAt(0).PropertyType));
                    writer.Write(new FName(writer.Asset, Value[0].PropertyType));
                }
                else
                {
                    writer.Write(KeyType);
                    writer.Write(ValueType);
                }
            }
            this.WriteEndPropertyTag(writer);
        }

        int here = (int)writer.BaseStream.Position;
        writer.Write(KeysToRemove?.Length ?? 0);
        if (KeysToRemove != null)
        {
            for (int i = 0; i < KeysToRemove.Length; i++)
            {
                var entry = KeysToRemove[i];
                entry.Offset = writer.BaseStream.Position;
                entry.Write(writer, false, PropertySerializationContext.Array);
            }
        }

        writer.Write(Value?.Count ?? 0);
        WriteRawMap(writer, Value, serializationContext);
        return (int)writer.BaseStream.Position - here;
    }

    protected override void HandleCloned(PropertyData res)
    {
        MapPropertyData cloningProperty = (MapPropertyData)res;

        if (this.Value != null)
        {
            var newDict = new TMap<PropertyData, PropertyData>();
            foreach (var entry in this.Value)
            {
                newDict[(PropertyData)entry.Key.Clone()] = (PropertyData)entry.Value.Clone();
            }
            cloningProperty.Value = newDict;
        }
        else
        {
            cloningProperty.Value = null;
        }

        cloningProperty.KeysToRemove = (PropertyData[])this.KeysToRemove?.Clone();
        cloningProperty.KeyType = (FName)this.KeyType?.Clone();
        cloningProperty.ValueType = (FName)this.ValueType?.Clone();
    }
}
