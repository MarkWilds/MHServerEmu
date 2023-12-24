﻿using MHServerEmu.Common.Extensions;

namespace MHServerEmu.Games.GameData.Prototypes.Markers
{
    public class ResourceMarkerPrototype : MarkerPrototype
    {
        public string Resource { get; }

        public ResourceMarkerPrototype(BinaryReader reader)
        {
            Resource = reader.ReadFixedString32();

            Position = reader.ReadVector3();
            Rotation = reader.ReadVector3();
        }
    }
}
