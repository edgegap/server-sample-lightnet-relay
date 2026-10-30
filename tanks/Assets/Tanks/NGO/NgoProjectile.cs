using Unity.Netcode;

namespace Tanks.NgoRelay
{
    public class NgoProjectile : NetworkBehaviour, IProjectileNet
    {
        bool IProjectileNet.IsServer => IsServer;
        public void ServerDespawn() => NetworkObject.Despawn();
    }
}
