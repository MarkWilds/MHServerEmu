﻿using System.Text;
using Google.ProtocolBuffers;
using MHServerEmu.Common.Extensions;
using MHServerEmu.GameServer.GameData;

namespace MHServerEmu.GameServer.Misc
{
    public class EquipmentInvUISlot
    {
        public ulong Index { get; set; }
        public ulong PrototypeId { get; set; }

        public EquipmentInvUISlot(CodedInputStream stream)
        {
            Index = stream.ReadRawVarint64();
            PrototypeId = stream.ReadPrototypeId(PrototypeEnumType.Property);
        }

        public EquipmentInvUISlot(ulong index, ulong prototypeId)
        {
            Index = index;
            PrototypeId = prototypeId;
        }

        public byte[] Encode()
        {
            using (MemoryStream memoryStream = new())
            {
                CodedOutputStream stream = CodedOutputStream.CreateInstance(memoryStream);

                stream.WriteRawVarint64(Index);
                stream.WritePrototypeId(PrototypeId, PrototypeEnumType.Property);

                stream.Flush();
                return memoryStream.ToArray();
            }
        }

        public override string ToString()
        {
            using (MemoryStream memoryStream = new())
            using (StreamWriter streamWriter = new(memoryStream))
            {
                streamWriter.WriteLine($"Index: 0x{Index.ToString("X")}");
                streamWriter.WriteLine($"PrototypeId: {GameDatabase.GetPrototypePath(PrototypeId)}");

                streamWriter.Flush();
                return Encoding.UTF8.GetString(memoryStream.ToArray());
            }
        }

    }
}