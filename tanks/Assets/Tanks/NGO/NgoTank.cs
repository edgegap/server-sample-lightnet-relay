using Unity.Netcode;
using UnityEngine;

namespace Tanks.NgoRelay
{
    /// <summary>NGO side of a tank: health NetworkVariable, fire ServerRpc + ClientRpc, despawn at 0 health.</summary>
    [RequireComponent(typeof(TankCore))]
    public class NgoTank : NetworkBehaviour, ITankNet
    {
        readonly NetworkVariable<int> health = new NetworkVariable<int>(TankCore.MaxHealth);

        TankCore core;

        void Awake() => core = GetComponent<TankCore>();

        public bool IsLocalOwner => IsOwner;
        bool ITankNet.IsServer => IsServer;
        bool ITankNet.IsSpawned => IsSpawned;
        public int Health => health.Value;
        public int OwnerId => (int)OwnerClientId;

        public void RequestFire() => FireServerRpc();

        [ServerRpc]
        void FireServerRpc()
        {
            core.CreateProjectile().GetComponent<NetworkObject>().Spawn(true);
            FireClientRpc();
        }

        [ClientRpc]
        void FireClientRpc() => core.PlayShoot();

        public void ServerTakeHit()
        {
            health.Value--;
            if (health.Value <= 0) NetworkObject.Despawn();
        }
    }
}
