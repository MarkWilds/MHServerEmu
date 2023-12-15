﻿namespace MHServerEmu.Games.Common
{
    public class Sphere
    {
        public Sphere(Vector3 center, float radius)
        {
            Center = center;
            Radius = radius;
        }

        public Vector3 Center { get; }
        public float Radius { get; }

        public Aabb ToAabb()
        {
            return new (new (Center.X - Radius, Center.Y - Radius, Center.Z - Radius),
                        new (Center.X + Radius, Center.Y + Radius, Center.Z + Radius));
        }

    }
}
