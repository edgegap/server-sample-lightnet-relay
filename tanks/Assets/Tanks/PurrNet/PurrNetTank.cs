using PurrNet;
using UnityEngine;

namespace Tanks.PurrNetRelay
{
    /// <summary>PurrNet side of a tank: health SyncVar, fire ServerRpc + ObserversRpc, despawn at 0 health.</summary>
    [RequireComponent(typeof(TankCore))]
    public class PurrNetTank : NetworkBehaviour, ITankNet
    {
        [SerializeField] SyncVar<int> health = new SyncVar<int>(TankCore.MaxHealth);

        TankCore core;

        void Awake() => core = GetComponent<TankCore>();

        public bool IsLocalOwner => isOwner;
        public bool IsServer => isServer;
        public bool IsSpawned => isSpawned;
        public int Health => health.value;
        public int OwnerId => owner.HasValue ? (int)(owner.Value.id.value % int.MaxValue) : 0;

        public void RequestFire() => CmdFire();

        [ServerRpc]
        void CmdFire()
        {
            GameObject projectile = UnityProxy.Instantiate(core.projectilePrefab, core.projectileMount.position, core.projectileMount.rotation);
            networkManager.Spawn(projectile);
            RpcOnFire();
        }

        [ObserversRpc]
        void RpcOnFire() => core.PlayShoot();

        public void ServerTakeHit()
        {
            health.value--;
            if (health.value <= 0) Destroy(gameObject);
        }
    }
}
