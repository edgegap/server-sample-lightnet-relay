using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace Tanks.FishNetRelay
{
    /// <summary>FishNet side of a tank: health SyncVar, fire ServerRpc + ObserversRpc, despawn at 0 health.</summary>
    [RequireComponent(typeof(TankCore))]
    public class FishNetTank : NetworkBehaviour, ITankNet
    {
        readonly SyncVar<int> health = new SyncVar<int>(TankCore.MaxHealth);

        TankCore core;

        void Awake() => core = GetComponent<TankCore>();

        public bool IsLocalOwner => IsOwner;
        bool ITankNet.IsServer => IsServerInitialized;
        bool ITankNet.IsSpawned => IsSpawned;
        public int Health => health.Value;
        int ITankNet.OwnerId => OwnerId;

        public void RequestFire() => CmdFire();

        [ServerRpc]
        void CmdFire()
        {
            Spawn(core.CreateProjectile());
            RpcOnFire();
        }

        [ObserversRpc]
        void RpcOnFire() => core.PlayShoot();

        public void ServerTakeHit()
        {
            health.Value--;
            if (health.Value <= 0) Despawn();
        }
    }
}
