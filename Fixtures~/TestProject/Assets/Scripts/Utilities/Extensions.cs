using UnityEngine;

namespace TestProject.Utilities
{
    public static class Extensions
    {
        public static Vector3 Flat(this Vector3 v) => new Vector3(v.x, 0, v.z);
        public static bool IsInRange(this float value, float min, float max) => value >= min && value <= max;
    }
}
