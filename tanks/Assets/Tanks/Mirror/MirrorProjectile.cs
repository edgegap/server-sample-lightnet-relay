using Mirror;

namespace Tanks.MirrorRelay
{
    public class MirrorProjectile : NetworkBehaviour, IProjectileNet
    {
        public bool IsServer => isServer;
        public void ServerDespawn() => NetworkServer.Destroy(gameObject);
    }
}
