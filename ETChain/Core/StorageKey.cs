﻿using AntShares.Cryptography;
using AntShares.IO;
using System;
using System.IO;
using System.Linq;

namespace AntShares.Core
{
    public class StorageKey : IEquatable<StorageKey>, ISerializable
    {
        public UInt160 ScriptHash;
        public byte[] Key;

        int ISerializable.Size => ScriptHash.Size + Key.GetVarSize();

        void ISerializable.Deserialize(BinaryReader reader)
        {
            ScriptHash = reader.ReadSerializable<UInt160>();
            Key = reader.ReadVarBytes();
        }

        public bool Equals(StorageKey other)
        {
            if (ReferenceEquals(other, null))
                return false;
            if (ReferenceEquals(this, other))
                return true;
            return ScriptHash.Equals(other.ScriptHash) && Key.SequenceEqual(other.Key);
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(obj, null)) return false;
            if (!(obj is StorageKey)) return false;
            return Equals((StorageKey)obj);
        }

        public override int GetHashCode()
        {
            return ScriptHash.GetHashCode() + (int)Key.Murmur32(0);
        }

        void ISerializable.Serialize(BinaryWriter writer)
        {
            writer.Write(ScriptHash);
            writer.WriteVarBytes(Key);
        }
    }
}
