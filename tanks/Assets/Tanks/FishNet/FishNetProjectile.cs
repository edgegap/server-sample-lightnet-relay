using FishNet.Object;

namespace Tanks.FishNetRelay
{
    public class FishNetProjectile : NetworkBehaviour, IProjectileNet
    {
        bool IProjectileNet.IsServer => IsServerInitialized;
        public void ServerDespawn() => Despawn();
    }
}
