using UnityEngine;

namespace Tanks
{
    public interface IProjectileNet
    {
        bool IsServer { get; }
        void ServerDespawn();
    }

    /// <summary>
    /// Mirror's Tanks projectile without netcode: every machine simulates the flight from the spawn
    /// position (no position sync), the server despawns it on a hit or after <see cref="destroyAfter"/>.
    /// </summary>
    public class ProjectileCore : MonoBehaviour
    {
        /// <summary>Projectiles seen on this machine, for tests.</summary>
        public static int SpawnedCount;

        public float destroyAfter = 2;
        public Rigidbody rigidBody;
        public float force = 1000;

        IProjectileNet net;
        float spawnedAt;
        bool despawned;

        void Awake() => net = GetComponent<IProjectileNet>();

        void Start()
        {
            SpawnedCount++;
            spawnedAt = Time.time;
            rigidBody.AddForce(transform.forward * force);
        }

        void Update()
        {
            if (Time.time - spawnedAt > destroyAfter) Despawn();
        }

        void OnTriggerEnter(Collider co) => Despawn();

        void Despawn()
        {
            if (despawned || net == null || !net.IsServer) return;
            despawned = true;
            net.ServerDespawn();
        }
    }
}
