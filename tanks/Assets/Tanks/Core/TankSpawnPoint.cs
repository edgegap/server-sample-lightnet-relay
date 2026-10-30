using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Tanks
{
    /// <summary>A place a tank can start at. The scene's spawn points are used in turn by owner id.</summary>
    public class TankSpawnPoint : MonoBehaviour
    {
        static readonly List<TankSpawnPoint> all = new List<TankSpawnPoint>();

        void OnEnable() => all.Add(this);
        void OnDisable() => all.Remove(this);

        public static Transform Pick(int ownerId)
        {
            if (all.Count == 0) return null;
            List<TankSpawnPoint> sorted = all.OrderBy(p => p.name).ToList();
            return sorted[Mathf.Abs(ownerId) % sorted.Count].transform;
        }
    }
}
