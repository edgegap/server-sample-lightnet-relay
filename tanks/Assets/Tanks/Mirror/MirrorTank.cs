using Mirror;
using UnityEngine;

namespace Tanks.MirrorRelay
{
    /// <summary>Mirror side of a tank: health SyncVar, fire Command + ClientRpc, destroy at 0 health.</summary>
    [RequireComponent(typeof(TankCore))]
    public class MirrorTank : NetworkBehaviour, ITankNet
    {
        [SyncVar] int health = TankCore.MaxHealth;

        TankCore core;

        void Awake() => core = GetComponent<TankCore>();

        public bool IsLocalOwner => isLocalPlayer;
        public bool IsServer => isServer;
        public bool IsSpawned => netId != 0;
        public int Health => health;
        // only the owner reads it (to pick a spawn point), and every player tank has its own netId
        public int OwnerId => (int)netId;

        public void RequestFire() => CmdFire();

        [Command]
        void CmdFire()
        {
            NetworkServer.Spawn(core.CreateProjectile());
            RpcOnFire();
        }

        [ClientRpc]
        void RpcOnFire() => core.PlayShoot();

        public void ServerTakeHit()
        {
            --health;
            if (health <= 0) NetworkServer.Destroy(gameObject);
        }
    }
}
